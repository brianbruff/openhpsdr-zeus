using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Per-band last-used (hz, mode). Lives in its own unencrypted LiteDB file
// (zeus-prefs.db) next to zeus.db; band memory isn't sensitive and sharing
// the encrypted credential file would mean either leaking the password or
// juggling two LiteDB connections to the same file.
public sealed class BandMemoryStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandMemoryEntry> _entries;
    private readonly ILogger<BandMemoryStore> _log;

    public BandMemoryStore(ILogger<BandMemoryStore> log)
    {
        _log = log;
        var dbPath = GetDatabasePath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<BandMemoryEntry>("band_memory");
        _entries.EnsureIndex(x => x.Band, unique: true);

        _log.LogInformation("BandMemoryStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<BandMemoryDto> GetAll()
    {
        return _entries
            .FindAll()
            .Select(e => new BandMemoryDto(e.Band, e.Hz, e.Mode))
            .ToArray();
    }

    public BandMemoryDto? Get(string band)
    {
        var e = _entries.FindOne(x => x.Band == band);
        return e is null ? null : new BandMemoryDto(e.Band, e.Hz, e.Mode);
    }

    public void Upsert(string band, long hz, RxMode mode)
    {
        var existing = _entries.FindOne(x => x.Band == band);
        if (existing is null)
        {
            _entries.Insert(new BandMemoryEntry
            {
                Band = band,
                Hz = hz,
                Mode = mode,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Hz = hz;
            existing.Mode = mode;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    public void Dispose() => _db.Dispose();

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }
}

public sealed class BandMemoryEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public long Hz { get; set; }
    public RxMode Mode { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
