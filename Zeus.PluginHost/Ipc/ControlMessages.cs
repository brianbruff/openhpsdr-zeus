// ControlMessages.cs — type model for the length-prefixed control plane.
//
// Phase 1 wire encoding is raw bytes (see ControlChannel). Phase 2 will
// switch the encoding to length-prefixed CBOR (PeterO.Cbor or
// System.Formats.Cbor) once the LoadPlugin / SetParam shapes are nailed
// down on the C++ side.
//
// The Phase 1 messages we actually exchange are:
//
//   Hello      — handshake. Sidecar -> host once, with version + caps.
//   Goodbye    — graceful shutdown. Host -> sidecar.
//   Heartbeat  — 1 Hz, both directions, deadman keepalive.
//   LogLine    — sidecar -> host, plain text for diagnostic forwarding.
//
// Plugin lifecycle (LoadPlugin / UnloadPlugin / SetParam / GetState /
// SetState) is deferred to Phase 2 and only stubbed here for forward
// reference in code reviews.

using System;

namespace Zeus.PluginHost.Ipc;

/// <summary>
/// Discriminator for the four Phase 1 control messages, plus stubs for
/// the Phase 2 messages so the type model is easy to extend without a
/// breaking rename.
/// </summary>
public enum ControlMessageType : ushort
{
    /// <summary>Reserved invalid value — never sent on the wire.</summary>
    None = 0,

    // ---- Phase 1 ---------------------------------------------------------
    Hello = 1,
    Goodbye = 2,
    Heartbeat = 3,
    LogLine = 4,

    // ---- Phase 2 (TODO) — declared here so adding them later is additive,
    //                       not a renumbering of existing values.
    LoadPlugin = 100,
    UnloadPlugin = 101,
    SetParam = 102,
    GetState = 103,
    SetState = 104,
}

/// <summary>Phase 1 handshake. Sidecar tells host its version + capabilities.</summary>
public sealed record HelloMessage(
    string SidecarVersion,
    uint ProtocolVersion,
    string Capabilities);

/// <summary>Host -> sidecar shutdown signal. Sidecar should drain and exit.</summary>
public sealed record GoodbyeMessage(string Reason);

/// <summary>Bidirectional 1 Hz keepalive. Carries a sequence so each end
/// can detect packet loss and the silent-sidecar SIGKILL recovery path.</summary>
public sealed record HeartbeatMessage(ulong Sequence, long UnixTimeMs);

/// <summary>Sidecar diagnostic. Severity matches the host's
/// <see cref="IPluginHostLog"/> levels.</summary>
public sealed record LogLineMessage(LogLevel Severity, string Text);

/// <summary>Subset of log levels the sidecar emits over the wire.</summary>
public enum LogLevel : byte
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

// ---- Phase 2 stubs ------------------------------------------------------
//
// These records are intentionally placeholders. The CBOR shape will be
// finalized when the C++ side implements LoadPlugin; until then nothing
// in Phase 1 should construct or send these.

/// <summary>TODO(phase2): plugin path + bitness probe payload.</summary>
public sealed record LoadPluginMessage(string PluginPath);

/// <summary>TODO(phase2): unload by slot id.</summary>
public sealed record UnloadPluginMessage(uint SlotId);

/// <summary>TODO(phase2): per-parameter set, normalized 0..1.</summary>
public sealed record SetParamMessage(uint SlotId, uint ParamId, float Normalized);
