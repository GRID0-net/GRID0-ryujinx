using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common;
using Ryujinx.Common.Helper;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems
{
    /// <summary>
    /// [GRID0] Where an update comes from, modelled on Ryujinx-Nextendo's updater.
    ///
    /// GRID0 builds carry the GRID0+ patches and settings, so they must never be "updated" to a
    /// stock Ryubing build: every update path asks the GRID0-net releases instead of the Ryubing
    /// update server. The archive download, extraction and in-place replacement in Updater.cs
    /// are reused; an AppImage is replaced as the single file it is.
    /// </summary>
    internal static partial class Updater
    {
        // The release LIST rather than /releases/latest, which skips pre-releases.
        private const string Grid0ReleasesApi =
            "https://api.github.com/repos/GRID0-net/GRID0-ryujinx/releases?per_page=10";

        private const string Grid0ReleasesPage = "https://github.com/GRID0-net/GRID0-ryujinx/releases";

        // The updater replaces the running emulator with what it downloads, so these hosts are the
        // trust boundary of the whole feature.
        private static readonly string[] _grid0AssetHosts =
        [
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
        ];

        /// <summary>
        /// True for an https url GitHub serves for this repository. Matched on the parsed host,
        /// never as a substring: Contains("GRID0-net/") would also accept evil.com/GRID0-net/x.
        /// </summary>
        private static bool Grid0IsOwnReleaseUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (!_grid0AssetHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/GRID0-net/GRID0-ryujinx/", StringComparison.OrdinalIgnoreCase);
        }

        // Set when running from an AppImage: the install is one read-only-mounted file, so the
        // archive path (which rewrites the folder the executable sits in) cannot be used.
        private static string RunningAppImage =>
            OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null;

        /// <summary>
        /// Checks the GRID0 releases and, if a newer build exists, offers to install it. Never
        /// installs without the player saying yes.
        /// </summary>
        public static async Task BeginGrid0UpdateAsync(bool showVersionUpToDate = false)
        {
            if (_running)
            {
                return;
            }

            _running = true;

            try
            {
                if (!Version.TryParse(Program.Version, out Version currentVersion))
                {
                    Logger.Warning?.Print(LogClass.Application, $"[GRID0] Cannot parse the running version ('{Program.Version}'); skipping the update check.");

                    return;
                }

                (Version newVersion, string assetUrl) = await QueryGrid0ReleaseAsync();

                if (newVersion is null)
                {
                    if (showVersionUpToDate)
                    {
                        await ContentDialogHelper.CreateWarningDialog(
                            "Could not check for GRID0 updates.",
                            $"The release list could not be reached. Newer builds are listed at {Grid0ReleasesPage}");
                    }

                    return;
                }

                if (newVersion <= currentVersion)
                {
                    if (showVersionUpToDate)
                    {
                        await ContentDialogHelper.CreateUpdaterUpToDateInfoDialog(
                            LocaleManager.Instance[LocaleKeys.DialogUpdaterAlreadyOnLatestVersionMessage],
                            string.Empty,
                            Grid0ReleasesPage);
                    }

                    Logger.Info?.Print(LogClass.Application, "[GRID0] Up to date.");

                    return;
                }

                Logger.Info?.Print(LogClass.Application, $"[GRID0] Version found: {currentVersion} -> {newVersion}");

                // Refuse anything not served by GitHub for this repository, and treat it like a
                // release without a build for this platform: offer the page instead.
                if (assetUrl is not null && !Grid0IsOwnReleaseUrl(assetUrl))
                {
                    Logger.Error?.Print(LogClass.Application, $"[GRID0] Refusing update: asset url is not this repository's ({assetUrl}).");
                    assetUrl = null;
                }

                bool accepted = false;

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    UserResult choice = await ContentDialogHelper.CreateUpdaterChoiceDialog(
                        LocaleManager.Instance[LocaleKeys.RyujinxUpdater],
                        LocaleManager.Instance[LocaleKeys.RyujinxUpdaterMessage],
                        $"GRID0 {currentVersion} → {newVersion}",
                        Grid0ReleasesPage);

                    accepted = choice == UserResult.Yes;
                });

                if (!accepted)
                {
                    return;
                }

                // No build this updater can install here (macOS, an unknown architecture): the
                // release page is the honest answer.
                if (assetUrl is null)
                {
                    OpenHelper.OpenUrl(Grid0ReleasesPage);

                    return;
                }

                if (RunningAppImage is { } appImage)
                {
                    await UpdateAppImage(appImage, assetUrl);

                    return;
                }

                // UpdateRyujinx owns _running from here and clears it itself.
                await UpdateRyujinx(assetUrl);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"[GRID0] Update check failed: {ex.Message}");
            }
            finally
            {
                if (!_updateSuccessful)
                {
                    _running = false;
                }
            }
        }

        /// <summary>
        /// Silent check for the "update available" indicator: no dialogs, false on any failure.
        /// </summary>
        public static async Task<bool> Grid0UpdateAvailableAsync()
        {
            try
            {
                if (!Version.TryParse(Program.Version, out Version currentVersion))
                {
                    return false;
                }

                (Version newVersion, _) = await QueryGrid0ReleaseAsync();

                return newVersion is not null && newVersion > currentVersion;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The highest published release and its asset for this install, or (null, null) when
        /// GitHub cannot be reached. The asset url is null when no build fits.
        /// </summary>
        private static async Task<(Version Version, string AssetUrl)> QueryGrid0ReleaseAsync()
        {
            using HttpClient http = ConstructHttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            HttpResponseMessage response = await http.GetAsync(Grid0ReleasesApi);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning?.Print(LogClass.Application, $"[GRID0] Release check returned {(int)response.StatusCode}.");

                return (null, null);
            }

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            Version best = null;
            string bestAsset = null;

            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean())
                {
                    continue;
                }

                if (!release.TryGetProperty("tag_name", out JsonElement tagElement)
                    || !Version.TryParse((tagElement.GetString() ?? "").TrimStart('v', 'V'), out Version version))
                {
                    continue;
                }

                // Newest-first is by publish date; compare versions so a late hotfix to an older
                // line cannot win.
                if (best is not null && version <= best)
                {
                    continue;
                }

                best = version;
                bestAsset = FindGrid0Asset(release);
            }

            return (best, bestAsset);
        }

        /// <summary>
        /// The asset this install can replace itself with: the AppImage for an AppImage, otherwise
        /// the archive the extractor in Updater.cs reads (.7z on Windows, .tar.xz on Linux).
        /// </summary>
        private static string FindGrid0Asset(JsonElement release)
        {
            if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };

            Func<string, bool> fits;

            if (arch is null || OperatingSystem.IsMacOS())
            {
                // The macOS build is an .app bundle in a .tar.gz, which the in-place updater does
                // not handle; the player is sent to the release page instead.
                return null;
            }
            else if (RunningAppImage is not null)
            {
                fits = name => name.EndsWith($"-{arch}.AppImage", StringComparison.OrdinalIgnoreCase);
            }
            else if (OperatingSystem.IsWindows())
            {
                fits = name => name.EndsWith($"-win_{arch}.7z", StringComparison.OrdinalIgnoreCase);
            }
            else if (OperatingSystem.IsLinux())
            {
                fits = name => name.EndsWith($"-linux_{arch}.tar.xz", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                return null;
            }

            return assets.EnumerateArray()
                .Select(a => (
                    Name: a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                    Url: a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null))
                .Where(a => a.Url is not null && fits(a.Name))
                .Select(a => a.Url)
                .FirstOrDefault();
        }

        /// <summary>
        /// Replaces the running AppImage with the downloaded one and restarts it. The new file is
        /// written next to the old one and renamed over it, so an interrupted download never
        /// leaves the player without a working emulator.
        /// </summary>
        private static async Task UpdateAppImage(string appImage, string downloadUrl)
        {
            string staging = appImage + ".grid0update";

            FATaskDialog taskDialog = new()
            {
                Header = LocaleManager.Instance[LocaleKeys.RyujinxUpdater],
                SubHeader = LocaleManager.Instance[LocaleKeys.UpdaterDownloading],
                IconSource = new FASymbolIconSource { Symbol = FASymbol.Download },
                ShowProgressBar = true,
                XamlRoot = RyujinxApp.MainWindow,
            };

            Exception failure = null;

            taskDialog.Opened += async (_, _) =>
            {
                try
                {
                    using HttpClient http = ConstructHttpClient();
                    using HttpResponseMessage response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    long total = response.Content.Headers.ContentLength ?? -1;

                    await using (Stream source = await response.Content.ReadAsStreamAsync())
                    await using (FileStream target = File.Create(staging))
                    {
                        byte[] buffer = new byte[1 << 16];
                        long written = 0;
                        int read;

                        while ((read = await source.ReadAsync(buffer)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read));
                            written += read;

                            if (total > 0)
                            {
                                taskDialog.SetProgressBarState(GetPercentage(written, total), FATaskDialogProgressState.Normal);
                            }
                        }
                    }

                    File.SetUnixFileMode(staging, File.GetUnixFileMode(appImage) | UnixFileMode.UserExecute);
                    File.Move(staging, appImage, true);

                    _updateSuccessful = true;
                }
                catch (Exception ex)
                {
                    failure = ex;

                    try
                    {
                        File.Delete(staging);
                    }
                    catch
                    {
                        // Best effort: a leftover staging file is harmless.
                    }
                }

                taskDialog.Hide();
            };

            await taskDialog.ShowAsync(true);

            if (failure is not null)
            {
                Logger.Error?.Print(LogClass.Application, $"[GRID0] AppImage update failed: {failure.Message}");

                await ContentDialogHelper.CreateWarningDialog(
                    "The GRID0 update could not be installed.",
                    $"{failure.Message}\n\nDownload it from {Grid0ReleasesPage}");

                return;
            }

            if (!_updateSuccessful)
            {
                return;
            }

            bool restart = await ContentDialogHelper.CreateChoiceDialog(
                LocaleManager.Instance[LocaleKeys.RyujinxUpdater],
                LocaleManager.Instance[LocaleKeys.DialogUpdaterCompleteMessage],
                LocaleManager.Instance[LocaleKeys.DialogUpdaterRestartMessage]);

            if (restart)
            {
                ProcessStartInfo start = new(appImage) { UseShellExecute = false };

                foreach (string argument in CommandLineState.Arguments)
                {
                    start.ArgumentList.Add(argument);
                }

                Process.Start(start);
                Environment.Exit(0);
            }
        }
    }
}
