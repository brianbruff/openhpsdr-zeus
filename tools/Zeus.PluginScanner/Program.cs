using Zeus.PluginHost.Discovery;

namespace Zeus.Tools.PluginScanner;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Zeus VST Plugin Scanner");
        Console.WriteLine("=======================");
        Console.WriteLine();

        var skipDefaults = args.Any(a => a == "--no-defaults" || a == "-N");
        var customRoots = args.Where(a => !a.StartsWith('-')).Select(ExpandPath).ToList();

        var roots = new List<string>();

        if (!skipDefaults)
        {
            var defaults = DefaultPluginPaths.ForCurrentPlatform();
            if (defaults.Count > 0)
            {
                Console.WriteLine($"Auto-included standard plugin paths for {OSName()}:");
                foreach (var p in defaults)
                {
                    Console.WriteLine($"  {p}");
                }
                roots.AddRange(defaults);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"No standard plugin paths exist on this system for {OSName()}.");
                Console.WriteLine();
            }
        }

        if (customRoots.Count > 0)
        {
            Console.WriteLine("Additional folders from command line:");
            foreach (var p in customRoots)
            {
                Console.WriteLine($"  {p}");
            }
            roots.AddRange(customRoots);
            Console.WriteLine();
        }
        else if (Console.IsInputRedirected == false)
        {
            Console.WriteLine("Add another folder to scan? (Plugins often live in vendor subfolders.)");
            Console.Write("Path (Enter to skip): ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
            {
                roots.Add(ExpandPath(input));
            }
            Console.WriteLine();
        }

        if (roots.Count == 0)
        {
            Console.Error.WriteLine("No paths to scan. Provide a folder as argv or via the prompt.");
            return 1;
        }

        var existing = new List<string>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
            {
                existing.Add(root);
            }
            else
            {
                Console.WriteLine($"  (skipping — not a directory) {root}");
            }
        }

        if (existing.Count == 0)
        {
            Console.Error.WriteLine("None of the supplied paths exist. Exiting.");
            return 2;
        }

        IBinaryHeaderSniffer sniffer = new BinaryHeaderSniffer();
        IPluginScanner scanner = new Zeus.PluginHost.Discovery.PluginScanner(sniffer);

        Console.WriteLine($"Scanning {existing.Count} root(s) recursively...");
        var startedAt = DateTime.UtcNow;

        var manifests = await scanner.ScanAsync(existing);

        var elapsed = DateTime.UtcNow - startedAt;

        Console.WriteLine($"Completed in {elapsed.TotalMilliseconds:F0} ms. Found {manifests.Count} plugin(s).");
        Console.WriteLine();

        var byKind = manifests
            .GroupBy(m => (m.Format, m.Platform, m.Bitness))
            .OrderBy(g => g.Key.Format)
            .ThenBy(g => g.Key.Platform)
            .ThenBy(g => g.Key.Bitness);

        Console.WriteLine("Summary by (Format, Platform, Bitness):");
        foreach (var g in byKind)
        {
            Console.WriteLine($"  {g.Key.Format,-6} {g.Key.Platform,-8} {g.Key.Bitness,-6}  {g.Count(),4}");
        }
        Console.WriteLine();

        Console.WriteLine("Plugins:");
        Console.WriteLine($"  {"Format",-6} {"Platform",-8} {"Bitness",-7} {"DisplayName",-40} Path");
        Console.WriteLine($"  {new string('-', 6)} {new string('-', 8)} {new string('-', 7)} {new string('-', 40)} {new string('-', 4)}");
        foreach (var m in manifests)
        {
            var name = m.DisplayName.Length > 40 ? m.DisplayName[..37] + "..." : m.DisplayName;
            Console.WriteLine($"  {m.Format,-6} {m.Platform,-8} {m.Bitness,-7} {name,-40} {m.FilePath}");
        }

        var withWarnings = manifests.Where(m => m.ScanWarnings.Count > 0).ToList();
        if (withWarnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Scan warnings ({withWarnings.Count}):");
            foreach (var m in withWarnings)
            {
                Console.WriteLine($"  {m.FilePath}");
                foreach (var w in m.ScanWarnings)
                {
                    Console.WriteLine($"    - {w}");
                }
            }
        }

        return 0;
    }

    private static string ExpandPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
        }
        return path;
    }

    private static string OSName()
    {
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "this platform";
    }
}
