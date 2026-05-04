// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus.Desktop — Photino-based shell that hosts the Zeus.Server.Hosting
// pipeline in-process and renders the SPA inside a native webview window.
// Lifecycle: window-close drives host shutdown, so there's no orphaned
// backend on the local port.

using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Photino.NET;
using Zeus.Server;

namespace Zeus.Desktop;

internal static class Program
{
    // [STAThread] forces the process's main thread into a single-threaded COM
    // apartment. Photino on Windows wraps WebView2, which is COM, and the
    // CoreWebView2 environment must be created on an STA thread. .NET's
    // top-level-statements entry point is MTA by default — when WebView2 init
    // runs on an MTA thread, Photino.Native silently fails to spawn the
    // msedgewebview2.exe child and we get a bare native window with no
    // browser surface inside (the v0.5.0 black-screen bug). On macOS / Linux
    // [STAThread] is a no-op.
    [STAThread]
    private static void Main(string[] args)
    {
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
            HostMode = ZeusHostMode.Desktop,
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
        // Photino on macOS sometimes ignores SetSize on first show — Cocoa initialises
        // the NSWindow at a small default and only the *minimum* size is honoured
        // reliably. Pinning SetMinWidth/SetMinHeight at the desired width forces the
        // frame to open wide enough to clear the SPA's mobile breakpoint (900px) and
        // give the panadapter usable headroom.
        const int MinWidth = 1280;
        const int InitialWidth = 1680;
        const int InitialHeight = 1050;

        // Photino's window/dock icon is set per-OS. Windows expects .ico (Photino's
        // SetIconFile binds it to the NSWindow / HWND), Linux GTK expects PNG, and
        // macOS draws the dock icon from CFBundleIconFile in Info.plist — so during
        // `dotnet run` on macOS the SetIconFile call is a no-op (the .app bundle
        // generator wires the icns separately). Both files ship next to the binary
        // via the csproj's <Content Include="zeus.png/.ico"> so AppContext.BaseDirectory
        // resolves correctly from `dotnet run` output and from a published bundle.
        var iconFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus.ico" : "zeus.png";
        var iconPath = Path.Combine(AppContext.BaseDirectory, iconFileName);

        var window = new PhotinoWindow()
            .SetTitle("Zeus")
            .SetUseOsDefaultLocation(false)
            .SetMinWidth(MinWidth)
            .SetMinHeight(800)
            .SetSize(InitialWidth, InitialHeight)
            .Center()
            .SetIconFile(iconPath)
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
    }
}
