using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Feeds the DMX channel buffer from Truss records arriving over OSC, which
    /// is how <c>truss-relay --osc</c> delivers them: one message, <c>/truss/dmx</c>,
    /// with the record as its single blob argument. The bytes are the record the
    /// stream carries, so a VJ sees in the Editor what viewers would get from the
    /// stream, universes, ages and budget included, with no encoder, ingest or
    /// player running.
    ///
    /// Only that one message shape is read. A bundle, a message with another
    /// address or argument type, or something that is not OSC at all is counted
    /// as ignored and never reaches the decoder, so another sender on the port
    /// cannot take the source down or light a fixture.
    ///
    /// The socket is read on its own thread, which cuts the record out of each
    /// datagram and queues it; the records are decoded on the main thread, in
    /// the frame they are handed to the manager.
    /// </summary>
    [AddComponentMenu("VRSL-URP/VRSL Truss OSC DMX Source")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class VRSLTrussOscDmxSource : VRSLTrussRecordSource
    {
        /// <summary>The address <c>truss-relay --osc</c> sends records under.</summary>
        public const string Address = "/truss/dmx";
        /// <summary>The relay's default port, clear of Art-Net and the other
        /// lighting tools that commonly share a machine.</summary>
        public const int DefaultPort = 12100;

        [Tooltip("UDP port to listen on. truss-relay --osc sends to 12100 unless told "
               + "otherwise. A change takes effect the next time the component is enabled.")]
        public int port = DefaultPort;

        [Tooltip("Address to listen on. 0.0.0.0 hears every adapter, which is what a relay "
               + "on another machine needs; 127.0.0.1 hears only this machine.")]
        public string listenAddress = "0.0.0.0";

        /// <summary>Whether the socket is open. False with <see cref="LastError"/>
        /// set means the port could not be taken, usually because something
        /// else on this machine already listens on it.</summary>
        public bool Listening => _socket != null;
        /// <summary>Why the socket could not be opened, or null.</summary>
        public string LastError { get; private set; }
        /// <summary>Datagrams that arrived on the port since enable.</summary>
        public ulong DatagramsReceived => (ulong)Interlocked.Read(ref _received);
        /// <summary>Datagrams that were not a <c>/truss/dmx</c> message with one
        /// blob, and so were never decoded.</summary>
        public ulong DatagramsIgnored => (ulong)Interlocked.Read(ref _ignored);

        // Records the socket thread has cut out of datagrams, waiting for the
        // main thread. Past this many nobody is reading, and the newest matter
        // more than the oldest.
        const int MaxPending = 256;
        const int ReceiveTimeoutMs = 200;

        readonly object _lock = new object();
        List<byte[]> _pending  = new List<byte[]>();
        List<byte[]> _draining = new List<byte[]>();
        long _received, _ignored;
        UdpClient _socket;
        Thread    _thread;
        volatile bool _running;

        protected override void OnEnable()
        {
            base.OnEnable();
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _ignored, 0);
            LastError = null;
            Open();
        }

        protected override void OnDisable()
        {
            Close();
            base.OnDisable();
        }

        protected override void Update()
        {
            base.Update();
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                (_pending, _draining) = (_draining, _pending);
            }
            foreach (var record in _draining)
            {
                var result = Accept(record);
                if (result != VRSLTrussDmx.Result.Ok && ShouldLog(result))
                    Debug.LogWarning($"[VRSL URP] Dropped a DMX record from OSC: {result} "
                                   + $"({record.Length} bytes). Further drops for this reason "
                                   + "are counted, not logged.", this);
            }
            _draining.Clear();
        }

        void Open()
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(listenAddress), port);
                var socket = new UdpClient(endpoint);
                socket.Client.ReceiveTimeout = ReceiveTimeoutMs;
                _socket  = socket;
                _running = true;
                _thread  = new Thread(() => Receive(socket))
                {
                    IsBackground = true,
                    Name         = "VRSL Truss OSC",
                };
                _thread.Start();
            }
            catch (Exception e)
            {
                _socket   = null;
                LastError = e.Message;
                Debug.LogError($"[VRSL URP] The Truss OSC DMX Source cannot listen on "
                             + $"{listenAddress}:{port}, so no DMX will arrive this way: {e.Message}. "
                             + "If another tool holds the port, stop it or change the port here "
                             + "and in truss-relay --osc.", this);
            }
        }

        void Close()
        {
            _running = false;
            var socket = _socket;
            _socket = null;
            // Closing the socket is what unblocks a receive in progress.
            socket?.Close();
            _thread?.Join(ReceiveTimeoutMs * 2);
            _thread = null;
            lock (_lock)
            {
                _pending.Clear();
                _draining.Clear();
            }
        }

        void Receive(UdpClient socket)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] datagram;
                try
                {
                    datagram = socket.Receive(ref from);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows reports an ICMP port-unreachable as a reset on the
                    // next receive. Nothing is sent from here, so it is another
                    // process's business, and the socket is fine.
                    continue;
                }
                catch (Exception)
                {
                    // Closed from Close(), or a socket fault nothing here can
                    // mend. Either way the thread is done.
                    return;
                }

                Interlocked.Increment(ref _received);
                if (!TryReadBlob(datagram, out int offset, out int length))
                {
                    Interlocked.Increment(ref _ignored);
                    continue;
                }
                var record = new byte[length];
                Buffer.BlockCopy(datagram, offset, record, 0, length);
                lock (_lock)
                {
                    if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
                    _pending.Add(record);
                }
            }
        }

        /// <summary>
        /// Where the blob of a <c>/truss/dmx</c> message with exactly one blob
        /// argument sits in <paramref name="datagram"/>. OSC strings are
        /// NUL-terminated and padded to four bytes; a blob is a big-endian
        /// length and its bytes. Anything else, a bundle included, is not ours.
        /// </summary>
        internal static bool TryReadBlob(byte[] datagram, out int offset, out int length)
        {
            offset = 0;
            length = 0;
            if (!ReadString(datagram, 0, out string address, out int next) || address != Address)
                return false;
            if (!ReadString(datagram, next, out string tags, out next) || tags != ",b")
                return false;
            if (next + 4 > datagram.Length) return false;
            int declared = (datagram[next] << 24) | (datagram[next + 1] << 16)
                         | (datagram[next + 2] << 8) | datagram[next + 3];
            if (declared < 0 || declared > datagram.Length - next - 4) return false;
            offset = next + 4;
            length = declared;
            return true;
        }

        static bool ReadString(byte[] datagram, int at, out string text, out int next)
        {
            text = null;
            next = at;
            if (at >= datagram.Length) return false;
            int nul = Array.IndexOf(datagram, (byte)0, at);
            if (nul < 0) return false;
            text = System.Text.Encoding.ASCII.GetString(datagram, at, nul - at);
            // The string, its NUL, and padding to the next multiple of four. A
            // datagram cut inside that padding is malformed.
            next = at + ((nul - at + 4) & ~3);
            return next <= datagram.Length;
        }
    }
}
