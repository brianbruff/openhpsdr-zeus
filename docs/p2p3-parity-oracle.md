# P2→P3 Two-Tier Parity Oracle (Phase 0.5)

**Status:** Libraries + negative-control proof delivered. NOT wired into live capture
(that is Phase 1, gated). The live stack stays stopped.

The oracle answers one question — *does the n9dsp DSP path produce the same result
as the WDSP path the radio has always used?* — with **measured** verdicts instead of
asserted ones. It is deliberately split into two tiers so cheap gating can unblock the
TX migration immediately while the rigorous fidelity oracle matures behind placeholder
thresholds that still need operator signoff.

Everything here is **protocol-agnostic DSP-comparison math**. There is no dependency on
a live radio, the network, or the n9dsp native backend: every entry point is a pure
function over interleaved-float IQ / real-sample buffers. That is what makes it
*standalone-testable today* and *drop-in for a live capture probe in Phase 1*.

## Deliverables

| Artifact | Location | Role |
|---|---|---|
| Comparator library (C#) | `N9DSP/tests/Zeus.Protocol3.Tests/Protocol3ParityOracle.cs` | Tier-A + Tier-B pure math |
| Negative-control tests (C#, xUnit) | `N9DSP/tests/Zeus.Protocol3.Tests/Protocol3ParityOracleTests.cs` | Proof the gates fire |
| Native proof (C++) | `N9DSP/tests/n9dsp_tests.cpp` (`parity_tier_a_*`) | Mirror of Tier-A gate + controls |
| This document | `rebuild_develop/docs/p2p3-parity-oracle.md` | Method, thresholds, capture points |

Run the C# oracle:

```
dotnet test C:\Users\Admin\Desktop\N9DSP\tests\Zeus.Protocol3.Tests\Zeus.Protocol3.Tests.csproj \
  --filter "FullyQualifiedName~Protocol3ParityOracleTests" -v q --nologo
```

The native Tier-A proof runs inside the existing `n9dsp_tests` binary (last three calls
in `main`; `--emit-metrics` surfaces the negative-control error magnitudes).

---

## Tier-A — cheap gating IQ-egress diff

**Purpose:** the go/no-go gate for the future TX migration. Compares one block of
WDSP-path TX IQ against the same block produced by the n9dsp path and returns a single
`ParityVerdict.Pass`.

**Entry point:** `Protocol3ParityOracle.CompareIqEgress(reference, candidate, TierAConfig)`
where `reference` = WDSP-path interleaved IQ `[I,Q,I,Q,…]`, `candidate` = n9dsp-path IQ.

**Checks (all must hold for PASS):**

| Metric | Meaning | Placeholder threshold (`TierAConfig.Default`) |
|---|---|---|
| Peak abs error | worst per-sample deviation | `MaxPeakAbsError = 1.0e-3` |
| RMS error | aggregate deviation across the block | `MaxRmsError = 3.0e-4` |
| EVM | error-vector magnitude as % of reference RMS | `MaxEvmPercent = 1.0` |

These three values are the **Tier-A gating tolerance** and gate Phase 1. They are the
values flagged for operator signoff (ADR open question #1). The timing/ceiling envelope
is a separate gate and is **not** part of this struct — Tier-A is pure amplitude fidelity.

### Tier-A capture point (Phase 1 wiring — not done yet)

- **WDSP side / capture point:**
  `rebuild_develop/Zeus.Server.Hosting/DspPipelineService.cs`
  signature-anchored `public void ForwardTxIqToP2(ReadOnlySpan<float> iqInterleaved)`
  (currently line 5323; the plan's `:5315` anchor is signature-qualified per Amendment B —
  trust the signature, not the raw line, across edits).
- **n9dsp side / entrypoint:**
  `N9DSP/include/n9dsp/n9dsp.h` → `n9dsp_process_tx_audio_to_iq` (**ABI 49**,
  `N9DSP_ABI_VERSION 49u`).

In Phase 1 the flag chooses *where audio→IQ happens*; the oracle taps both paths' IQ at
`ForwardTxIqToP2` and runs `CompareIqEgress` as an auto rollback tripwire ("Tier-A canary
failure"). Until then the comparator only sees synthetic fixtures.

---

## Tier-B — rigorous RX / display / S-meter oracle (non-blocking lane)

**Purpose:** fidelity comparison for the receive path, spectral display, and S-meter,
where "close enough" needs numbers. Non-gating until thresholds are frozen.

**Entry point:**
`Protocol3ParityOracle.CompareRxFidelity(referenceIq, candidateIq, referenceSMeterDb, candidateSMeterDb, TierBConfig)`

**Checks (all must hold for PASS):**

| Metric | Meaning | Placeholder threshold (`TierBConfig.Default`) |
|---|---|---|
| Normalized cross-correlation | waveform-shape equivalence (Pearson; identical = 1.0, inverted = −1.0) | `MinCorrelation = 0.999` |
| Spectral RMS delta (dB) | per-bin magnitude-difference RMS vs reference-spectrum RMS, Hann-windowed naive DFT (display-parity proxy, no FFT lib) | `MaxSpectralDeltaDb = 1.0` |
| S-meter delta (dB) | absolute difference of the two paths' reported signal strength | `MaxSMeterDeltaDb = 1.0` |

> **TODO(signoff):** every Tier-B threshold above is a **PLACEHOLDER**. Freeze each one
> against a measured WDSP baseline before it gates anything (plan ADR open question #1).
> The markers live inline in `Protocol3ParityOracle.cs` on `TierBConfig.Default`.

EVM is also computed and reported in the Tier-B verdict for diagnostics, but is not itself
a Tier-B gate (it is a Tier-A gate).

---

## Negative-control methodology

A comparator is only trustworthy if it **FAILS on signals that are meaningfully
different**. Every tier ships with an identical/passing control AND corrupted controls
that must fail. This is the anti-"green because it never really checks" guard.

### Tier-A controls (C# + native)

| Control | Expectation | Result |
|---|---|---|
| Identical block | PASS | ✅ passes (peak/RMS = 0) |
| Sub-tolerance dither (±1e-5 sinusoid) | PASS | ✅ passes (n9dsp need not be bit-identical) |
| **Phase-inverted block** (sign flip) | **FAIL** | ✅ FAILS — peak abs error **0.8** (native metric), ≫ 1e-3 |
| **3-sample-shifted block** | **FAIL** | ✅ FAILS — RMS error **0.164** (native metric), ≫ 3e-4 |
| Length mismatch | FAIL | ✅ FAILS (`length-mismatch`) |

### Tier-B controls (C#)

| Control | Expectation | Result |
|---|---|---|
| Identical RX fidelity | PASS | ✅ passes |
| **Phase-inverted IQ block** | **FAIL** | ✅ FAILS — correlation → −1, below 0.999 (`rx-fidelity-corr`) |
| **Injected 3 dB S-meter offset** | **FAIL** | ✅ FAILS — 3 dB > 1 dB ceiling (`rx-fidelity-smeter`) |
| Moved-tone spectrum (equal energy, different bin) | FAIL | ✅ FAILS — spectral delta > 1 dB |

Both mandated Tier-B negative controls — the **3 dB S-meter offset** and the
**phase-inverted IQ block** — fire.

---

## Placeholder thresholds summary (for operator signoff)

| Tier | Constant | Placeholder value | Gates |
|---|---|---|---|
| A | `MaxPeakAbsError` | `1.0e-3` | Phase 1 (TX migration) |
| A | `MaxRmsError` | `3.0e-4` | Phase 1 |
| A | `MaxEvmPercent` | `1.0` % | Phase 1 |
| B | `MinCorrelation` | `0.999` | nothing yet — TODO(signoff) |
| B | `MaxSpectralDeltaDb` | `1.0` dB | nothing yet — TODO(signoff) |
| B | `MaxSMeterDeltaDb` | `1.0` dB | nothing yet — TODO(signoff) |

All values are engineering defaults chosen to be sane starting points, **not** frozen
contract values. Tier-A tolerances need operator signoff before they gate the Phase 1
cutover; Tier-B thresholds can lag until a WDSP baseline is captured.

## Scope boundaries

- **In scope (done):** standalone comparator libraries + negative-control proof.
- **Out of scope (Phase 1, gated):** wiring the oracle into live IQ capture at
  `ForwardTxIqToP2`, the timing/ceiling envelope, and any change to the WDSP↔n9dsp flag.
- The autonomous PA-protection / TX fail-close path in the radio is untouched and is
  independent of this oracle.
