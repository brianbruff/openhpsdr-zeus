using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// DSP settings persistence — stores NR/NB/ANF/SNB/NBP parameters plus the
// active band-plan region so the operator's configuration survives restarts.
// Shares zeus-prefs.db with BandMemoryStore and LayoutStore.
public sealed class DspSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DspSettingsEntry> _entries;
    private readonly ILiteCollection<BandPlanPrefsEntry> _bandPlanPrefs;
    private readonly ILogger<DspSettingsStore> _log;

    public DspSettingsStore(ILogger<DspSettingsStore> log)
    {
        _log = log;
        var dbPath = GetDatabasePath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<DspSettingsEntry>("dsp_settings");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);
        _bandPlanPrefs = _db.GetCollection<BandPlanPrefsEntry>("band_plan_prefs");

        _log.LogInformation("DspSettingsStore initialized at {Path}", dbPath);
    }

    public NrConfig? Get(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        if (e is null)
            return null;

        return new NrConfig(
            NrMode: e.NrMode,
            AnfEnabled: e.AnfEnabled,
            SnbEnabled: e.SnbEnabled,
            NbpNotchesEnabled: e.NbpNotchesEnabled,
            NbMode: e.NbMode,
            NbThreshold: e.NbThreshold);
    }

    public void Upsert(NrConfig config, string profileId = "default")
    {
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            _entries.Insert(new DspSettingsEntry
            {
                ProfileId = profileId,
                NrMode = config.NrMode,
                AnfEnabled = config.AnfEnabled,
                SnbEnabled = config.SnbEnabled,
                NbpNotchesEnabled = config.NbpNotchesEnabled,
                NbMode = config.NbMode,
                NbThreshold = config.NbThreshold,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.NrMode = config.NrMode;
            existing.AnfEnabled = config.AnfEnabled;
            existing.SnbEnabled = config.SnbEnabled;
            existing.NbpNotchesEnabled = config.NbpNotchesEnabled;
            existing.NbMode = config.NbMode;
            existing.NbThreshold = config.NbThreshold;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    public string? GetCurrentRegionId()
    {
        var entry = _bandPlanPrefs.FindOne(x => x.Key == "CurrentRegionId");
        return entry?.Value;
    }

    public void SetCurrentRegionId(string regionId)
    {
        var existing = _bandPlanPrefs.FindOne(x => x.Key == "CurrentRegionId");
        if (existing is null)
        {
            _bandPlanPrefs.Insert(new BandPlanPrefsEntry { Key = "CurrentRegionId", Value = regionId });
        }
        else
        {
            existing.Value = regionId;
            _bandPlanPrefs.Update(existing);
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

public sealed class DspSettingsEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public NrMode NrMode { get; set; }
    public bool AnfEnabled { get; set; }
    public bool SnbEnabled { get; set; }
    public bool NbpNotchesEnabled { get; set; }
    public NbMode NbMode { get; set; }
    public double NbThreshold { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class BandPlanPrefsEntry
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
