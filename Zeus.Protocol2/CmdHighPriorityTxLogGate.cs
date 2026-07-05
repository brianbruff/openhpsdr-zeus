// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2;

internal sealed class CmdHighPriorityTxLogGate
{
    private readonly object _sync = new();
    private readonly long _intervalMs;
    private bool _hasLogged;
    private bool _lastRun;
    private bool _lastMox;
    private bool _lastTun;
    private long _nextDueMs;

    public CmdHighPriorityTxLogGate(long intervalMs = 1000)
    {
        if (intervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _intervalMs = intervalMs;
    }

    public bool ShouldLog(long nowMs, bool run, bool mox, bool tun)
    {
        lock (_sync)
        {
            bool changed = _hasLogged
                && (run != _lastRun || mox != _lastMox || tun != _lastTun);
            if (!_hasLogged || changed || nowMs >= _nextDueMs)
            {
                _hasLogged = true;
                _lastRun = run;
                _lastMox = mox;
                _lastTun = tun;
                _nextDueMs = nowMs + _intervalMs;
                return true;
            }

            return false;
        }
    }
}
