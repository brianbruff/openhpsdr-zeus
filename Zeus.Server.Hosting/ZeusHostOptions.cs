// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Which entry-point built the host. Surfaced on /api/capabilities so the
/// frontend can decide whether plugin GUIs (which open as native OS
/// windows on the box running the host) are reachable to the operator.
/// </summary>
public enum ZeusHostMode
{
    /// <summary>Standalone server (Zeus.Server). Operator's browser may be remote.</summary>
    Server,
    /// <summary>Photino desktop shell (Zeus.Desktop). Operator is necessarily at the host's display.</summary>
    Desktop,
}

/// <summary>
/// Configuration for <see cref="ZeusHost"/>. Service mode (Zeus.Server) and
/// desktop mode (Zeus.Desktop) construct different option shapes; everything
/// else about the host is identical.
/// </summary>
public sealed class ZeusHostOptions
{
    /// <summary>
    /// Which entry-point built the host. Defaults to <see cref="ZeusHostMode.Server"/>;
    /// Zeus.Desktop sets <see cref="ZeusHostMode.Desktop"/> explicitly.
    /// </summary>
    public ZeusHostMode HostMode { get; init; } = ZeusHostMode.Server;

    /// <summary>HTTP listening port. Service-mode default 6060; desktop mode passes a free port.</summary>
    public int HttpPort { get; init; } = 6060;

    /// <summary>
    /// HTTPS listening port. Set to 0 to skip HTTPS (used in desktop mode where
    /// the Photino webview hits 127.0.0.1 only and never needs getUserMedia).
    /// </summary>
    public int HttpsPort { get; init; }

    /// <summary>
    /// True (default, service mode): bind Kestrel on all interfaces so the LAN
    /// can reach the SPA + API. False (desktop mode): loopback only.
    /// </summary>
    public bool BindAllInterfaces { get; init; } = true;

    /// <summary>
    /// True (service mode): generate/load the self-signed LAN cert and bind HTTPS.
    /// False (desktop mode): skip HTTPS entirely.
    /// </summary>
    public bool UseHttpsLanCert { get; init; } = true;

    /// <summary>
    /// Print the multi-line startup banner to stdout. Service mode = true (the
    /// console window is the operator-facing UI). Desktop mode = false (Photino
    /// is the UI; the console is hidden anyway).
    /// </summary>
    public bool PrintConsoleBanner { get; init; } = true;
}
