using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// UI layout persistence — stores the opaque flexlayout-react JSON blob so the
// operator's panel arrangement survives page reloads and reinstalls.
// Shares zeus-prefs.db with BandMemoryStore (layout isn't sensitive).
public sealed class LayoutStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LayoutEntry> _entries;
    private readonly ILogger<LayoutStore> _log;

    public LayoutStore(ILogger<LayoutStore> log)
    {
        _log = log;
        var dbPath = GetDatabasePath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<LayoutEntry>("ui_layout");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);

        _log.LogInformation("LayoutStore initialized at {Path}", dbPath);
    }

    public UiLayoutDto? Get(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        return e is null
            ? null
            : new UiLayoutDto(e.LayoutJson, new DateTimeOffset(e.UpdatedUtc).ToUnixTimeMilliseconds());
    }

    public void Upsert(string layoutJson, string profileId = "default")
    {
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            _entries.Insert(new LayoutEntry
            {
                ProfileId = profileId,
                LayoutJson = layoutJson,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.LayoutJson = layoutJson;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    public void Delete(string profileId = "default")
        => _entries.DeleteMany(x => x.ProfileId == profileId);

    public void Dispose() => _db.Dispose();

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }
}

public sealed class LayoutEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
