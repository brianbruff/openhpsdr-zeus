// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class StreamingHub
{
    private const int MaxBacklogPerClient = 4;

    // Matches MsgType.MicPcm; the client→server uplink type-byte.
    private const byte MsgTypeMicPcm = 0x20;

    // Largest client→server payload we'll reassemble. A mic PCM frame is
    // 1 + 960*4 = 3841 bytes; 16 KB leaves comfortable headroom if the
    // contract ever adds a control frame. Receives larger than this are
    // dropped to bound memory.
    private const int MaxInboundMessageBytes = 16 * 1024;

    private readonly ILogger<StreamingHub> _log;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    // Latest WDSP wisdom phase. Set by StreamingHubWisdomBridge on phase-changed
    // events; read on every AttachClientAsync so late joiners see the current
    // state without waiting for the next transition. Volatile because the
    // writer is on a worker thread and readers can be on any hub caller.
    private volatile byte _wisdomPhase;

    public StreamingHub(ILogger<StreamingHub> log) { _log = log; }

    public int ClientCount => _clients.Count;

    /// <summary>Updates the hub's cached wisdom phase so clients attaching
    /// after the one-shot broadcast still receive the current state.</summary>
    public void SetWisdomPhase(Zeus.Contracts.WisdomPhase phase)
    {
        _wisdomPhase = (byte)phase;
    }

    /// <summary>
    /// Raised when a client sends a <c>MicPcm</c> binary frame. The handler
    /// receives the payload with the 1-byte type prefix stripped — plain
    /// f32le samples. Subscribers must be fast: the handler runs on the
    /// WS receive loop and blocking it will stall further uplink.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? MicPcmReceived;

    public async Task AttachClientAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var session = new ClientSession(id, ws, _log, this);
        _clients[id] = session;
        _log.LogInformation("ws.client.connected id={Id} total={Count}", id, _clients.Count);

        // Prime the new client with the current wisdom phase. Without this a
        // client that joins after the ready event would sit at default (Idle)
        // and render the pulsing Connect button indefinitely.
        session.TryEnqueue(BuildWisdomPayload((Zeus.Contracts.WisdomPhase)_wisdomPhase));

        try
        {
            await session.RunAsync(ct);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _log.LogInformation("ws.client.disconnected id={Id} total={Count}", id, _clients.Count);
        }
    }

    internal void DispatchInbound(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length == 0) return;
        byte type = frame.Span[0];
        switch (type)
        {
            case MsgTypeMicPcm:
                if (MicPcmReceived is { } handler)
                {
                    try { handler(frame.Slice(1)); }
                    catch (Exception ex) { _log.LogWarning(ex, "MicPcmReceived handler threw"); }
                }
                break;
            default:
                // Unknown uplink type — log once so a misaligned client is obvious,
                // but don't tear down the connection.
                _log.LogDebug("ws.inbound unknown type=0x{Type:X2} len={Len}", type, frame.Length);
                break;
        }
    }

    public void Broadcast(in DisplayFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = frame.TotalByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            var payload = new ReadOnlyMemory<byte>(rented, 0, total).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Broadcast(in AudioFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = frame.TotalByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            var payload = new ReadOnlyMemory<byte>(rented, 0, total).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Broadcast(in TxMetersFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = TxMetersFrame.ByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            var payload = new ReadOnlyMemory<byte>(rented, 0, total).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Broadcast(in TxMetersV2Frame frame)
    {
        if (_clients.IsEmpty) return;

        int total = TxMetersV2Frame.ByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            var payload = new ReadOnlyMemory<byte>(rented, 0, total).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Broadcast(in RxMeterFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = RxMeterFrame.ByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            var payload = new ReadOnlyMemory<byte>(rented, 0, total).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Broadcast(in PaTempFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = PaTempFrame.ByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            var payload = new ReadOnlyMemory<byte>(rented, 0, total).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Broadcast(in WisdomStatusFrame frame)
    {
        SetWisdomPhase(frame.Phase);
        if (_clients.IsEmpty) return;
        var payload = BuildWisdomPayload(frame.Phase);
        foreach (var client in _clients.Values) client.TryEnqueue(payload);
    }

    private static byte[] BuildWisdomPayload(Zeus.Contracts.WisdomPhase phase)
    {
        var buf = new byte[WisdomStatusFrame.ByteLength];
        var writer = new FixedBufferWriter(buf, buf.Length);
        new WisdomStatusFrame(phase).Serialize(writer);
        return buf;
    }

    public void Broadcast(in AlertFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = AlertFrame.MaxByteLength;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var writer = new FixedBufferWriter(rented, total);
            frame.Serialize(writer);
            // AlertFrame has variable length; need to compute actual size
            var payload = new ReadOnlyMemory<byte>(rented, 0, 2 + System.Text.Encoding.UTF8.GetByteCount(frame.Message)).ToArray();
            foreach (var client in _clients.Values) client.TryEnqueue(payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private sealed class ClientSession
    {
        public Guid Id { get; }
        private readonly WebSocket _ws;
        private readonly ILogger _log;
        private readonly StreamingHub _hub;
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxBacklogPerClient)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        public ClientSession(Guid id, WebSocket ws, ILogger log, StreamingHub hub)
        {
            Id = id; _ws = ws; _log = log; _hub = hub;
        }

        public void TryEnqueue(byte[] payload) => _queue.Writer.TryWrite(payload);

        public async Task RunAsync(CancellationToken ct)
        {
            var sendTask = SendLoopAsync(ct);
            var recvTask = ReceiveLoopAsync(ct);
            await Task.WhenAny(sendTask, recvTask);
            _queue.Writer.TryComplete();
            try { await Task.WhenAll(sendTask, recvTask); } catch { /* drained */ }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var frame in _queue.Reader.ReadAllAsync(ct))
                {
                    if (_ws.State != WebSocketState.Open) break;
                    await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "ws send loop ended for {Id}", Id);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            // 8 KB receive window. A mic PCM frame (3841 B) arrives in one or
            // two ReceiveAsync calls depending on chunking; the accumulator
            // below stitches fragments up to MaxInboundMessageBytes before
            // dispatch.
            var buf = new byte[8 * 1024];
            // Reuse across messages; reset on each EndOfMessage.
            byte[]? accum = null;
            int accumLen = 0;
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        // Ignore text frames — no textual control channel in the MVP.
                        continue;
                    }

                    int chunkLen = result.Count;
                    // Fast path: single-fragment message with no pending accumulator —
                    // dispatch the buffer view directly, no allocation.
                    if (result.EndOfMessage && accum is null)
                    {
                        _hub.DispatchInbound(new ReadOnlyMemory<byte>(buf, 0, chunkLen));
                        continue;
                    }

                    if (accum is null)
                    {
                        accum = ArrayPool<byte>.Shared.Rent(Math.Max(chunkLen, 4096));
                        accumLen = 0;
                    }
                    if (accumLen + chunkLen > MaxInboundMessageBytes)
                    {
                        _log.LogWarning("ws.inbound oversize id={Id} len={Len}", Id, accumLen + chunkLen);
                        ArrayPool<byte>.Shared.Return(accum);
                        accum = null;
                        accumLen = 0;
                        continue;
                    }
                    if (accumLen + chunkLen > accum.Length)
                    {
                        int newSize = Math.Min(MaxInboundMessageBytes, accum.Length * 2);
                        while (newSize < accumLen + chunkLen) newSize = Math.Min(MaxInboundMessageBytes, newSize * 2);
                        var grown = ArrayPool<byte>.Shared.Rent(newSize);
                        Buffer.BlockCopy(accum, 0, grown, 0, accumLen);
                        ArrayPool<byte>.Shared.Return(accum);
                        accum = grown;
                    }
                    Buffer.BlockCopy(buf, 0, accum, accumLen, chunkLen);
                    accumLen += chunkLen;

                    if (result.EndOfMessage)
                    {
                        _hub.DispatchInbound(new ReadOnlyMemory<byte>(accum, 0, accumLen));
                        ArrayPool<byte>.Shared.Return(accum);
                        accum = null;
                        accumLen = 0;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                _log.LogDebug(ex, "ws recv loop ended for {Id}", Id);
            }
            finally
            {
                if (accum is not null) ArrayPool<byte>.Shared.Return(accum);
            }
        }
    }

    private sealed class FixedBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buf;
        private readonly int _capacity;
        private int _written;

        public FixedBufferWriter(byte[] buf, int capacity) { _buf = buf; _capacity = capacity; }

        public void Advance(int count)
        {
            if (_written + count > _capacity) throw new InvalidOperationException("buffer overflow");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _buf.AsMemory(_written, _capacity - _written);

        public Span<byte> GetSpan(int sizeHint = 0) => _buf.AsSpan(_written, _capacity - _written);
    }
}
