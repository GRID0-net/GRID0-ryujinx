using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Proxy;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Ryujinx.Tests.HLE
{
    public class SocketPollTests
    {
        [TestCase(SocketType.Dgram, false, false, false)]
        [TestCase(SocketType.Dgram, false, true, false)]
        [TestCase(SocketType.Stream, false, true, true)]
        [TestCase(SocketType.Stream, true, false, true)]
        [TestCase(SocketType.Stream, true, true, false)]
        public void PollDoesNotTreatDatagramErrorsAsHangups(SocketType type, bool connected, bool bound, bool hangup)
        {
            ErrorSocket socket = new(type, connected, bound);
            ManagedSocket managed = (ManagedSocket)Activator.CreateInstance(typeof(ManagedSocket),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { socket }, null);
            PollEvent evnt = new(new PollEventData(), managed);
            LinuxError result = ManagedSocketPollManager.Instance.Poll(new List<PollEvent> { evnt }, 0, out int count);
            Assert.That(result, Is.EqualTo(LinuxError.SUCCESS));
            Assert.That(count, Is.EqualTo(1));
            Assert.That(evnt.Data.OutputEvents.HasFlag(PollEventTypeMask.Error), Is.True);
            Assert.That(evnt.Data.OutputEvents.HasFlag(PollEventTypeMask.Disconnected), Is.EqualTo(hangup));
        }

        private sealed class ErrorSocket(SocketType type, bool connected, bool bound) : IPollableSocket
        {
            public bool Readable => false;
            public bool Writable => false;
            public bool Error => true;
            public bool Connected => connected;
            public bool IsBound => bound;
            public SocketType SocketType => type;
            public AddressFamily AddressFamily => AddressFamily.InterNetwork;
            public ProtocolType ProtocolType => type == SocketType.Dgram ? ProtocolType.Udp : ProtocolType.Tcp;
            public EndPoint RemoteEndPoint => null;
            public EndPoint LocalEndPoint => null;
            public bool Blocking { get; set; }
            public int Available => 0;
            public void Dispose() { }
            public int Receive(Span<byte> buffer) => throw new NotSupportedException();
            public int Receive(Span<byte> buffer, SocketFlags flags) => throw new NotSupportedException();
            public int Receive(Span<byte> buffer, SocketFlags flags, out SocketError error) => throw new NotSupportedException();
            public int ReceiveFrom(Span<byte> buffer, SocketFlags flags, ref EndPoint remoteEP) => throw new NotSupportedException();
            public int Send(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
            public int Send(ReadOnlySpan<byte> buffer, SocketFlags flags) => throw new NotSupportedException();
            public int Send(ReadOnlySpan<byte> buffer, SocketFlags flags, out SocketError error) => throw new NotSupportedException();
            public int SendTo(ReadOnlySpan<byte> buffer, SocketFlags flags, EndPoint remoteEP) => throw new NotSupportedException();
            public bool Poll(int microSeconds, SelectMode mode) => throw new NotSupportedException();
            public ISocketImpl Accept() => throw new NotSupportedException();
            public void Bind(EndPoint ep) => throw new NotSupportedException();
            public void Connect(EndPoint ep) => throw new NotSupportedException();
            public void Listen(int backlog) => throw new NotSupportedException();
            public void GetSocketOption(SocketOptionLevel level, SocketOptionName name, byte[] value) => throw new NotSupportedException();
            public void SetSocketOption(SocketOptionLevel level, SocketOptionName name, int value) => throw new NotSupportedException();
            public void SetSocketOption(SocketOptionLevel level, SocketOptionName name, object value) => throw new NotSupportedException();
            public void Shutdown(SocketShutdown how) => throw new NotSupportedException();
            public void Disconnect(bool reuse) => throw new NotSupportedException();
            public void Close() { }
        }
    }
}
