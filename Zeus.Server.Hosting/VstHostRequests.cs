// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstHostRequests — JSON DTOs for the /api/plughost/* REST surface.
// Tiny records, JSON-deserialized by the minimal-API model binder.

namespace Zeus.Server;

public sealed record VstHostMasterRequest(bool Enabled);

public sealed record VstHostSlotLoadRequest(string Path);

public sealed record VstHostBypassRequest(bool Bypass);

public sealed record VstHostParameterRequest(double Value);

public sealed record VstHostSearchPathRequest(string Path);
