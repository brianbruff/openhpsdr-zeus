using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Contracts.Tests;

/// <summary>
/// Unit tests for the auto-ATT control loop in <see cref="RadioService"/>.
/// Drives <c>HandleAdcOverload(status, nowMs)</c> directly with synthetic
/// timestamps so we can verify throttling, ramp-up, decay, and the red-lamp
/// counter without spinning up a Protocol1Client.
/// </summary>
public class AutoAttControlLoopTests
{
    private static RadioService MakeService() =>
        new(NullLoggerFactory.Instance);

    private static readonly AdcOverloadStatus Overload = new(Adc0: true, Adc1: false);
    private static readonly AdcOverloadStatus Clean = new(Adc0: false, Adc1: false);

    [Fact]
    public void Defaults_AutoAttOn_ZeroBaselineAndOffset_NoWarning()
    {
        var r = MakeService();
        var s = r.Snapshot();

        Assert.True(s.AutoAttEnabled);
        Assert.Equal(0, s.AttenDb);
        Assert.Equal(0, s.AttOffsetDb);
        Assert.False(s.AdcOverloadWarning);
    }

    [Fact]
    public void FirstOverloadEvent_EstablishesTickBaseline_NoImmediateStep()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 1_000);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void OverloadRepeated_RampsOneStepPerTickWindow()
    {
        var r = MakeService();

        // First event is a free "baseline" — no step applied (throttle rule).
        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 50);  // inside same window — ignored
        Assert.Equal(0, r.Snapshot().AttOffsetDb);

        // Cross the 100 ms boundary: one step applied.
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);

        // Another window crossing.
        r.HandleAdcOverload(Overload, nowMs: 210);
        Assert.Equal(2, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void OverloadSustained_SaturatesAt31dB()
    {
        var r = MakeService();
        // Start tick baseline.
        r.HandleAdcOverload(Overload, nowMs: 0);
        for (int i = 1; i <= 50; i++)
        {
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        }
        Assert.Equal(31, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void ClearAfterRamp_DecaysToZero()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);
        // Ramp to 5 dB offset.
        for (int i = 1; i <= 5; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.Equal(5, r.Snapshot().AttOffsetDb);

        // Decay by feeding clean events.
        for (int i = 1; i <= 5; i++)
            r.HandleAdcOverload(Clean, nowMs: 500 + i * 100 + 5);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);

        // Further clean events don't go negative.
        r.HandleAdcOverload(Clean, nowMs: 2000);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void OverloadBurstsWithinWindow_CountAsOneStep()
    {
        var r = MakeService();
        r.HandleAdcOverload(Clean, nowMs: 0);    // baseline tick
        // Many events in one 100ms window.
        for (int i = 1; i <= 50; i++)
            r.HandleAdcOverload(Overload, nowMs: i);
        // First boundary cross — one step.
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void AdcOverloadWarning_FlipsRedWhenCounterExceedsThree()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);   // baseline
        // Each overload tick adds +2 to the counter, clamped to 5. Red lamp on
        // counter>3 means 2 overload ticks are enough.
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.False(r.Snapshot().AdcOverloadWarning); // counter=2 after 1 tick
        r.HandleAdcOverload(Overload, nowMs: 210);
        Assert.True(r.Snapshot().AdcOverloadWarning);  // counter=4 after 2 ticks
    }

    [Fact]
    public void AdcOverloadWarning_DecaysToFalse()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 105);
        r.HandleAdcOverload(Overload, nowMs: 210);
        Assert.True(r.Snapshot().AdcOverloadWarning); // counter=4

        // Each clean tick decrements by 1. After 1 clean tick counter=3 → !warn.
        r.HandleAdcOverload(Clean, nowMs: 315);
        Assert.False(r.Snapshot().AdcOverloadWarning);
    }

    [Fact]
    public void AutoAttDisabled_EventsHaveNoEffect()
    {
        var r = MakeService();
        r.SetAutoAtt(false);

        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 105);
        r.HandleAdcOverload(Overload, nowMs: 210);

        var s = r.Snapshot();
        Assert.False(s.AutoAttEnabled);
        Assert.Equal(0, s.AttOffsetDb);
        Assert.False(s.AdcOverloadWarning);
    }

    [Fact]
    public void MoxOn_SuspendsControlLoop()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);

        r.SetMox(true);
        // While MOX, nothing happens to the ramp (TX path owns its own atten).
        for (int i = 2; i <= 10; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);

        r.SetMox(false);
        // After RX resumes, stepping resumes. The first post-MOX event fires a
        // tick because the 100 ms boundary has long since elapsed; subsequent
        // events keep the ramp going.
        int before = r.Snapshot().AttOffsetDb;
        r.HandleAdcOverload(Overload, nowMs: 2000);
        r.HandleAdcOverload(Overload, nowMs: 2105);
        Assert.True(r.Snapshot().AttOffsetDb > before,
            "offset must advance once MOX clears");
    }

    [Fact]
    public void TurningAutoAttOff_ResetsOffsetAndWarning()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);
        for (int i = 1; i <= 4; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.Equal(4, r.Snapshot().AttOffsetDb);
        Assert.True(r.Snapshot().AdcOverloadWarning);

        r.SetAutoAtt(false);
        var s = r.Snapshot();
        Assert.False(s.AutoAttEnabled);
        Assert.Equal(0, s.AttOffsetDb);
        Assert.False(s.AdcOverloadWarning);
    }

    [Fact]
    public void StateDto_SerializationRoundTrip_PreservesAutoAttFields()
    {
        var opts = new System.Text.Json.JsonSerializerOptions();
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            AttenDb: 3,
            AutoAttEnabled: true,
            AttOffsetDb: 7,
            AdcOverloadWarning: true);

        string json = System.Text.Json.JsonSerializer.Serialize(state, opts);
        var back = System.Text.Json.JsonSerializer.Deserialize<StateDto>(json, opts);

        Assert.NotNull(back);
        Assert.True(back.AutoAttEnabled);
        Assert.Equal(7, back.AttOffsetDb);
        Assert.True(back.AdcOverloadWarning);
        Assert.Equal(3, back.AttenDb);
    }
}
