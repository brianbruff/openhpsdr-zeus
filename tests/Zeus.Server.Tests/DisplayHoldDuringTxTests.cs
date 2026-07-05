// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Regression lock for the 2026-07-04 P3 waterfall reset-storm fix: while the
// host TX-IQ path is armed (keyed), Protocol3SidecarFrameForwarder.PollDisplayAsync
// holds the sidecar's RX display frames instead of broadcasting them, so the
// WDSP TX display exclusively owns the panadapter/waterfall during transmit
// (P2 parity). Broadcasting both sources while keyed made the frame geometry
// alternate every tick, which reset the client's display planner on every
// flip and smeared the waterfall until unkey.
//
// PollDisplayAsync itself is a private instance method only reachable through
// the poll loop (which needs a live sidecar HTTP endpoint), so it is not
// called directly here. What IS reachable and IS the actual regression
// surface is the gate it reads — Protocol3SidecarBridge.TxIqStreamingArmed —
// which PushTxControlAsync (also exercised by the real MOX/TX-control forward
// path in Protocol3SidecarControlForwarder) arms on key-down and disarms on
// unkey. If this flag stops tracking key state correctly, the display-hold
// gate silently breaks regardless of how PollDisplayAsync reads it.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DisplayHoldDuringTxTests
{
    // No diagnostics URL is ever configured (no ConnectAsync call), so every
    // sidecar HTTP post inside PushTxControlAsync short-circuits to a no-op
    // (Protocol3SidecarBridge.PostControlAsync: "if (diagnosticsUrl is null)
    // return;") without needing a fake HttpMessageHandler. The arm/disarm
    // side effect happens unconditionally around that no-op post, which is
    // exactly the behaviour under test here.
    private static Protocol3SidecarBridge NewBridge() => new(
        new ThrowingHttpClientFactory(),
        new ConfigurationBuilder().Build(),
        NullLogger<Protocol3SidecarBridge>.Instance);

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("network not expected in these tests");
    }

    [Fact]
    public void TxIqStreamingArmed_IsFalseBeforeAnyTxControlPush()
    {
        using var sidecar = NewBridge();
        Assert.False(sidecar.TxIqStreamingArmed);
    }

    [Fact]
    public async Task TxIqStreamingArmed_BecomesTrueOnMoxKeyDown()
    {
        using var sidecar = NewBridge();

        await sidecar.PushTxControlAsync(
            mox: true, txEnable: true, tune: false, driveLevel: 100, CancellationToken.None);

        Assert.True(sidecar.TxIqStreamingArmed);
    }

    [Fact]
    public async Task TxIqStreamingArmed_BecomesTrueOnTuneWithoutMox()
    {
        using var sidecar = NewBridge();

        await sidecar.PushTxControlAsync(
            mox: false, txEnable: false, tune: true, driveLevel: 10, CancellationToken.None);

        Assert.True(sidecar.TxIqStreamingArmed);
    }

    [Fact]
    public async Task TxIqStreamingArmed_ReturnsFalseAfterUnkey()
    {
        using var sidecar = NewBridge();

        await sidecar.PushTxControlAsync(
            mox: true, txEnable: true, tune: false, driveLevel: 100, CancellationToken.None);
        Assert.True(sidecar.TxIqStreamingArmed);

        await sidecar.PushTxControlAsync(
            mox: false, txEnable: false, tune: false, driveLevel: 0, CancellationToken.None);

        Assert.False(sidecar.TxIqStreamingArmed);
    }

    [Fact]
    public async Task TxIqStreamingArmed_StaysFalseWhenTxControlPushIsAllFalse()
    {
        using var sidecar = NewBridge();

        // A TX-control push with mox/txEnable/tune all false (e.g. an
        // unsolicited/duplicate "off" event) must never arm streaming.
        await sidecar.PushTxControlAsync(
            mox: false, txEnable: false, tune: false, driveLevel: 0, CancellationToken.None);

        Assert.False(sidecar.TxIqStreamingArmed);
    }

    [Fact]
    public async Task PrepareHostTxIqAsync_DisarmsStreamingBeforeSettling()
    {
        using var sidecar = NewBridge();
        await sidecar.PushTxControlAsync(
            mox: true, txEnable: true, tune: false, driveLevel: 100, CancellationToken.None);
        Assert.True(sidecar.TxIqStreamingArmed);

        // No diagnostics URL configured, so this returns immediately after
        // the unconditional DisarmTxIqStreaming() at its top (see
        // Protocol3SidecarBridge.PrepareHostTxIqAsync).
        await sidecar.PrepareHostTxIqAsync(CancellationToken.None);

        Assert.False(sidecar.TxIqStreamingArmed);
    }
}
