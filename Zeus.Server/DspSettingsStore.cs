using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// DSP settings persistence — stores NR/NB/ANF/SNB/NBP parameters so the
// operator's preferred noise reduction and blanker configuration survives
// server restarts. Shares zeus-prefs.db with BandMemoryStore and LayoutStore
// (DSP settings aren't sensitive).
//
// NR2 post2 + NR4 (Sbnr) tunables are persisted as nullable scalars on the
// existing entry — null means "use the engine's NrDefaults baseline" so the
// operator can reset a field by clearing it. No new POCO type is introduced
// because LiteDB's BsonMapper races on parallel construction (commit b57c12d).
public sealed class DspSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DspSettingsEntry> _entries;
    private readonly ILogger<DspSettingsStore> _log;

    public DspSettingsStore(ILogger<DspSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? GetDatabasePath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<DspSettingsEntry>("dsp_settings");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);

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
            NbThreshold: e.NbThreshold,
            EmnrPost2Run: e.EmnrPost2Run,
            EmnrPost2Factor: e.EmnrPost2Factor,
            EmnrPost2Nlevel: e.EmnrPost2Nlevel,
            EmnrPost2Rate: e.EmnrPost2Rate,
            EmnrPost2Taper: e.EmnrPost2Taper,
            Nr4ReductionAmount: e.Nr4ReductionAmount,
            Nr4SmoothingFactor: e.Nr4SmoothingFactor,
            Nr4WhiteningFactor: e.Nr4WhiteningFactor,
            Nr4NoiseRescale: e.Nr4NoiseRescale,
            Nr4PostFilterThreshold: e.Nr4PostFilterThreshold,
            Nr4NoiseScalingType: e.Nr4NoiseScalingType,
            Nr4Position: e.Nr4Position);
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
                EmnrPost2Run = config.EmnrPost2Run,
                EmnrPost2Factor = config.EmnrPost2Factor,
                EmnrPost2Nlevel = config.EmnrPost2Nlevel,
                EmnrPost2Rate = config.EmnrPost2Rate,
                EmnrPost2Taper = config.EmnrPost2Taper,
                Nr4ReductionAmount = config.Nr4ReductionAmount,
                Nr4SmoothingFactor = config.Nr4SmoothingFactor,
                Nr4WhiteningFactor = config.Nr4WhiteningFactor,
                Nr4NoiseRescale = config.Nr4NoiseRescale,
                Nr4PostFilterThreshold = config.Nr4PostFilterThreshold,
                Nr4NoiseScalingType = config.Nr4NoiseScalingType,
                Nr4Position = config.Nr4Position,
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
            existing.EmnrPost2Run = config.EmnrPost2Run;
            existing.EmnrPost2Factor = config.EmnrPost2Factor;
            existing.EmnrPost2Nlevel = config.EmnrPost2Nlevel;
            existing.EmnrPost2Rate = config.EmnrPost2Rate;
            existing.EmnrPost2Taper = config.EmnrPost2Taper;
            existing.Nr4ReductionAmount = config.Nr4ReductionAmount;
            existing.Nr4SmoothingFactor = config.Nr4SmoothingFactor;
            existing.Nr4WhiteningFactor = config.Nr4WhiteningFactor;
            existing.Nr4NoiseRescale = config.Nr4NoiseRescale;
            existing.Nr4PostFilterThreshold = config.Nr4PostFilterThreshold;
            existing.Nr4NoiseScalingType = config.Nr4NoiseScalingType;
            existing.Nr4Position = config.Nr4Position;
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
    // NR2 (EMNR) post2 comfort-noise tunables. Null means "engine default".
    public bool? EmnrPost2Run { get; set; }
    public double? EmnrPost2Factor { get; set; }
    public double? EmnrPost2Nlevel { get; set; }
    public double? EmnrPost2Rate { get; set; }
    public int? EmnrPost2Taper { get; set; }
    // NR4 (SBNR) tunables. Null means "engine default".
    public double? Nr4ReductionAmount { get; set; }
    public double? Nr4SmoothingFactor { get; set; }
    public double? Nr4WhiteningFactor { get; set; }
    public double? Nr4NoiseRescale { get; set; }
    public double? Nr4PostFilterThreshold { get; set; }
    public int? Nr4NoiseScalingType { get; set; }
    public int? Nr4Position { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
