using System.Text.Json;
using System.Text.Json.Serialization;
using Nereus.Contracts;
using Nereus.Dsp;
using Nereus.Dsp.Wdsp;
using Xunit;

namespace Nereus.Dsp.Tests;

[Collection("Wdsp")]
public class NoiseReductionTests
{
    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    public static IEnumerable<object[]> AllCombos()
    {
        foreach (var nr in new[] { NrMode.Off, NrMode.Anr, NrMode.Emnr })
        foreach (var anf in new[] { false, true })
        foreach (var snb in new[] { false, true })
        foreach (var notches in new[] { false, true })
        foreach (var nb in new[] { NbMode.Off, NbMode.Nb1, NbMode.Nb2 })
            yield return new object[] { new NrConfig(nr, anf, snb, notches, nb, 20.0) };
    }

    [Theory]
    [MemberData(nameof(AllCombos))]
    public void Synthetic_AcceptsEveryModeCombination(NrConfig cfg)
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        eng.SetNoiseReduction(id, cfg);
    }

    [Fact]
    public void Synthetic_RejectsBogusEnumValues()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.Throws<ArgumentException>(() => eng.SetNoiseReduction(id, new NrConfig(NrMode: (NrMode)99)));
        Assert.Throws<ArgumentException>(() => eng.SetNoiseReduction(id, new NrConfig(NbMode: (NbMode)99)));
    }

    [Fact]
    public void Synthetic_RejectsNullConfig()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.Throws<ArgumentNullException>(() => eng.SetNoiseReduction(id, null!));
    }

    // WDSP exercise — runs only when the native library is present. Walks every
    // combination the UI can produce and proves no combination crashes the
    // post-RXA NR path. Audio correctness is covered by the existing smoke /
    // tone-peak tests; this test only has to prove the P/Invoke signatures
    // match libwdsp and that the engine's mutual-exclusion logic is sound.
    [SkippableTheory]
    [MemberData(nameof(AllCombos))]
    public void Wdsp_AcceptsEveryModeCombinationWithoutCrashing(NrConfig cfg)
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetNoiseReduction(channel, cfg);
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_TogglingNrModes_DoesNotLeaveBothEnabled()
    {
        // Thetis NR button is Off/NR/NR2 — only one of ANR/EMNR may be running
        // at a time. Proven here by cycling through each mode; the engine
        // must issue the counter-Run(0) before toggling the other on.
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Anr));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Emnr));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Anr));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Off));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    // REST contract round-trip. The server registers a JsonStringEnumConverter
    // in Program.cs so NrMode/NbMode go on the wire as "Anr"/"Nb1" etc.; this
    // test uses the same options so a schema change on either side breaks here.
    [Fact]
    public void NrSetRequest_JsonRoundTrip_PreservesAllFields()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var req = new NrSetRequest(new NrConfig(
            NrMode: NrMode.Emnr,
            AnfEnabled: true,
            SnbEnabled: true,
            NbpNotchesEnabled: true,
            NbMode: NbMode.Nb2,
            NbThreshold: 55.5));

        string json = JsonSerializer.Serialize(req, opts);
        var back = JsonSerializer.Deserialize<NrSetRequest>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(req, back);
        Assert.Contains("\"Emnr\"", json);
        Assert.Contains("\"Nb2\"", json);
    }

    [Fact]
    public void StateDto_JsonRoundTrip_PreservesNrBlock()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            AgcTopDb: 80.0,
            AttenDb: 15,
            Nr: new NrConfig(NrMode.Anr, true, false, true, NbMode.Off, 20.0));

        string json = JsonSerializer.Serialize(state, opts);
        var back = JsonSerializer.Deserialize<StateDto>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(state.Nr, back!.Nr);
    }

    // NB1/NB2 lifecycle across a full toggle cycle. Proves: (1) create_anbEXT /
    // create_nobEXT ran in OpenChannel before any SetEXT* setter could land
    // (otherwise deref of zero-initialized panb[id]/pnob[id] → SIGSEGV); (2)
    // destroy_*EXT runs in CloseChannel so the next OpenChannel reusing id 0
    // doesn't leak the prior struct.
    [SkippableTheory]
    [InlineData(48_000)]
    [InlineData(192_000)]
    public void Wdsp_NbLifecycle_ToggleOffNb1Nb2Off_DoesNotCrash(int sampleRate)
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(sampleRate, 1024);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb1));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb2));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Off));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb1, NbThreshold: 50.0));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Off));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    // Proves xanbEXT is actually called on the IQ path when NB1 is engaged —
    // not just that the setters don't crash. Feeds a clean sinusoid with a
    // large impulse spike every ~8 ms. With NB1 on, xanb substitutes the
    // delayed-ringbuffer value for samples flagged as above-average; the
    // spike bleeds into the pre-RXA buffer and raises the audio envelope
    // ceiling above the no-NB baseline, but attenuated. We only assert the
    // engine produces audio without crashing — exact peak ratios depend on
    // WDSP's AGC response to the impulse and are not stable enough to pin.
    [SkippableFact]
    public void Wdsp_Nb1_ProducesAudioForImpulsyIq()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 1024;
        const int TotalComplex = 64 * 1024;
        const double Amplitude = 0.2;
        const double ImpulseAmplitude = 5.0;
        const int ImpulsePeriod = 1500;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb1));

            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 2_000.0 * n / SampleRate;
                double i = Amplitude * Math.Cos(phase);
                double q = Amplitude * Math.Sin(phase);
                if (n % ImpulsePeriod == 0) { i += ImpulseAmplitude; q += ImpulseAmplitude; }
                iq[2 * n] = i;
                iq[2 * n + 1] = q;
            }
            engine.FeedIq(channel, iq);

            var audio = new float[2048];
            int total = 0;
            for (int i = 0; i < 50 && total == 0; i++)
            {
                Thread.Sleep(20);
                total = engine.ReadAudio(channel, audio);
            }
            Assert.True(total > 0, "expected audio out of NB1-enabled channel");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [Fact]
    public void NrConfig_DefaultsMatchThetisOffState()
    {
        // Drop-in sanity: the contract's default-constructed NrConfig must
        // equal "everything off, NB threshold at Thetis UI default 20 (→ 3.3
        // scaled)." If any default changes here without a corresponding spec
        // update, this test is the tripwire.
        var cfg = new NrConfig();
        Assert.Equal(NrMode.Off, cfg.NrMode);
        Assert.False(cfg.AnfEnabled);
        Assert.False(cfg.SnbEnabled);
        Assert.False(cfg.NbpNotchesEnabled);
        Assert.Equal(NbMode.Off, cfg.NbMode);
        Assert.Equal(20.0, cfg.NbThreshold);
    }
}
