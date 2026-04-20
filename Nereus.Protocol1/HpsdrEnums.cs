using Nereus.Protocol1.Discovery;

namespace Nereus.Protocol1;

/// <summary>
/// Protocol-1 RX sample rate selector — encoded in C1 bits [1:0] of the
/// config register (CC0=0x00). See docs/prd/02-protocol1-integration.md §6.
/// </summary>
public enum HpsdrSampleRate : byte
{
    Rate48k = 0,
    Rate96k = 1,
    Rate192k = 2,
    Rate384k = 3,
}

/// <summary>
/// Extended RX attenuator, 0–31 dB. Wire-encoded in the dedicated attenuator
/// register CC0=0x14 (deskhpsdr old_protocol.c:2891-2968). The Db value is the
/// same API across boards; ControlFrame maps it per-board:
/// <list type="bullet">
/// <item>HL2 (<see cref="HpsdrBoardKind.HermesLite2"/>) writes C4 = 0x40 | (60 − Db) — HL2 has no hardware
/// attenuator, so "attenuate by N dB" is expressed as "reduce firmware RX
/// gain by N units from max".</item>
/// <item>Standard HPSDR (ANAN / Hermes / Orion) writes C4 = 0x20 | (Db &amp; 0x1F).</item>
/// </list>
/// </summary>
public readonly record struct HpsdrAtten(int Db)
{
    public const int MinDb = 0;
    public const int MaxDb = 31;

    public static HpsdrAtten Zero { get; } = new(0);

    public int ClampedDb => Math.Clamp(Db, MinDb, MaxDb);
}

/// <summary>
/// Alex RX antenna selector — encoded in C3 bits [7:5]. ANT1 is the default
/// for every supported board.
/// </summary>
public enum HpsdrAntenna : byte
{
    Ant1 = 0,
    Ant2 = 1,
    Ant3 = 2,
}

public sealed record StreamConfig(HpsdrSampleRate Rate, bool PreampOn, HpsdrAtten Atten)
{
    public int SampleRateHz => Rate switch
    {
        HpsdrSampleRate.Rate48k => 48_000,
        HpsdrSampleRate.Rate96k => 96_000,
        HpsdrSampleRate.Rate192k => 192_000,
        HpsdrSampleRate.Rate384k => 384_000,
        _ => throw new ArgumentOutOfRangeException(nameof(Rate), Rate, null),
    };
}
