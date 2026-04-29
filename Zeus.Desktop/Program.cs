// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus.Desktop — Photino-based shell that hosts the Zeus.Server.Hosting
// pipeline in-process and renders the SPA inside a native webview window.
// Lifecycle: window-close drives host shutdown, so there's no orphaned
// backend on the local port.

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Photino.NET;
using Zeus.Server;

// macOS Cocoa requires UI work (window/menu construction) to happen on the
// initial process thread. .NET console apps don't install a SynchronizationContext,
// so any `await` would resume the rest of Main on a thread-pool thread —
// which then crashes Photino with "API misuse: setting the main menu on a
// non-main thread". Block synchronously through the host startup so the
// Photino calls below stay on the main thread; Kestrel runs on its own
// thread pool either way and is unaffected.

// Loopback-only, OS-assigned port, no LAN HTTPS, no console banner — the
// Photino webview is the only consumer. Picking port 0 lets the OS hand us
// a guaranteed-free port; we read it back from IServer after StartAsync so
// no TOCTOU race with a concurrent listener.
var hostOptions = new ZeusHostOptions
{
    HttpPort = 0,
    BindAllInterfaces = false,
    UseHttpsLanCert = false,
    PrintConsoleBanner = false,
};

var app = ZeusHost.Build(args, hostOptions);
ZeusHost.InitializeAsync(app).GetAwaiter().GetResult();
app.StartAsync().GetAwaiter().GetResult();

// Resolve the bound URL after Start — Kestrel writes the OS-assigned port
// into IServerAddressesFeature here. Exactly one HTTP address because
// hostOptions.UseHttpsLanCert=false and BindAllInterfaces=false.
var addresses = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
var startUrl = addresses.Addresses.FirstOrDefault()
    ?? throw new InvalidOperationException("Kestrel reported no listening addresses.");

Console.WriteLine($"Zeus.Desktop hosting backend at {startUrl}");

// SetUseOsDefaultLocation(false)+Center so first launch doesn't drop the
// window in the corner. Title is the marketing name; we prefix "Openhpsdr"
// elsewhere in copy but the OS title bar stays short.
var window = new PhotinoWindow()
    .SetTitle("Zeus")
    .SetUseOsDefaultLocation(false)
    .SetSize(1400, 900)
    .Center()
    .Load(new Uri(startUrl));

// Translate Ctrl-C / SIGTERM into a window close so `dotnet run` (and the
// installer's launcher script) can shut Zeus down without leaving the
// Photino native loop blocking the main thread. Without this, signals only
// reach Kestrel and the UI loop holds the process open until killed.
Console.CancelKeyPress += (_, e) => { e.Cancel = true; window.Close(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => window.Close();

// WaitForClose blocks the main thread until the user closes the window. On
// macOS this satisfies Cocoa's "UI on main thread" requirement; Kestrel
// runs on its own thread-pool, untouched by the windowing loop.
window.WaitForClose();

Console.WriteLine("Window closed; stopping backend.");
app.StopAsync().GetAwaiter().GetResult();
