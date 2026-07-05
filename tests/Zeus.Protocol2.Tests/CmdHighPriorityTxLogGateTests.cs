// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Protocol2;

namespace Zeus.Protocol2.Tests;

public sealed class CmdHighPriorityTxLogGateTests
{
    [Fact]
    public void ShouldLog_SuppressesSteadyStateUntilOneSecondCadence()
    {
        var gate = new CmdHighPriorityTxLogGate();

        Assert.True(gate.ShouldLog(nowMs: 10_000, run: true, mox: false, tun: false));
        Assert.False(gate.ShouldLog(nowMs: 10_250, run: true, mox: false, tun: false));
        Assert.False(gate.ShouldLog(nowMs: 10_999, run: true, mox: false, tun: false));
        Assert.True(gate.ShouldLog(nowMs: 11_000, run: true, mox: false, tun: false));
    }

    [Fact]
    public void ShouldLog_EmitsImmediatelyWhenRunMoxOrTunChangesFromLastLoggedValues()
    {
        var gate = new CmdHighPriorityTxLogGate();

        Assert.True(gate.ShouldLog(nowMs: 0, run: true, mox: false, tun: false));
        Assert.True(gate.ShouldLog(nowMs: 100, run: true, mox: true, tun: false));
        Assert.True(gate.ShouldLog(nowMs: 200, run: true, mox: true, tun: true));
        Assert.True(gate.ShouldLog(nowMs: 300, run: false, mox: true, tun: true));
        Assert.False(gate.ShouldLog(nowMs: 400, run: false, mox: true, tun: true));
    }
}
