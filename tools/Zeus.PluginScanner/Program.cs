using Zeus.PluginHost.Discovery;

namespace Zeus.Tools.PluginScanner;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var roots = new List<string>();

        if (args.Length > 0)
        {
            roots.AddRange(args);
        }
        else
        {
            Console.WriteLine("Zeus VST Plugin Scanner");
            Console.WriteLine("=======================");
            Console.WriteLine();
            Console.WriteLine("Enter the folder to scan. The scanner walks subfolders recursively,");
            Console.WriteLine("so point at the parent directory that contains your plugins.");
            Console.WriteLine();
            Console.Write("Plugin folder path: ");
            var path = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(path))
            {
                Console.Error.WriteLine("No path provided. Exiting.");
                return 1;
            }

            path = Environment.ExpandEnvironmentVariables(path);
            if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            {
                path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
            }

            roots.Add(path);
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"Path does not exist or is not a directory: {root}");
                return 2;
            }
        }

        IBinaryHeaderSniffer sniffer = new BinaryHeaderSniffer();
        IPluginScanner scanner = new Zeus.PluginHost.Discovery.PluginScanner(sniffer);

        Console.WriteLine();
        Console.WriteLine($"Scanning {roots.Count} root(s) recursively...");
        var startedAt = DateTime.UtcNow;

        var manifests = await scanner.ScanAsync(roots);

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
}
