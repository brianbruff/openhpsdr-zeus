using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nereus.Dsp.Wdsp;

namespace Nereus.Dsp;

public enum DspEngineKind { Auto, Wdsp, Synthetic }

public static class DspEngineFactory
{
    // Note: the Phase 3 server (DspPipelineService) does NOT use this factory —
    // it constructs engines directly so it can swap Synthetic<->WDSP tied to the
    // Protocol1Client connect/disconnect lifecycle. This factory remains for
    // tests and any future consumer that just wants a one-shot engine.
    public static IDspEngine Create(DspEngineKind kind = DspEngineKind.Auto, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        switch (kind)
        {
            case DspEngineKind.Wdsp:
                logger.LogInformation("Dsp engine: WDSP (forced)");
                return new WdspDspEngine();

            case DspEngineKind.Synthetic:
                logger.LogInformation("Dsp engine: synthetic (forced)");
                return new SyntheticDspEngine();

            case DspEngineKind.Auto:
            default:
                // Auto always returns Synthetic: WDSP without an IQ source returns
                // flag=0 from GetPixels (blank screen — useless for idle/demo UX).
                // Consumers that have an IQ source should construct WdspDspEngine
                // directly (or pass DspEngineKind.Wdsp explicitly).
                var wdspAvailable = WdspNativeLoader.TryProbe();
                logger.LogInformation(
                    "Dsp engine: synthetic (auto — libwdsp {Status})",
                    wdspAvailable ? "available but caller did not request Wdsp" : "not loadable");
                return new SyntheticDspEngine();
        }
    }
}
