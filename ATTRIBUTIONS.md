# Zeus — Provenance and Attributions

This file is the canonical, human-readable statement of provenance for the
Zeus project. It exists so that anyone reading the code — or auditing it —
can trace Zeus's lineage, see who the work rests on, and understand how the
licence obligations flow through the project.

Per-file headers reference this document by name. This file is the
authoritative list; those headers are a reminder.

## License

Zeus is distributed under the **GNU General Public License, version 2 or
(at your option) any later version** (GPL-2.0-or-later). The full licence
text is in [`LICENSE`](LICENSE). Every first-party source file in this
repository carries the `SPDX-License-Identifier: GPL-2.0-or-later` tag
plus a short-form copyright and attribution block.

This licence was chosen deliberately to align Zeus with its primary
upstreams and reference projects:

- **Thetis** — GPL v2 or later
- **WDSP** — GPL v2 or later
- **pihpsdr** — GPL v2 or later
- **DeskHPSDR** — GPL v2 or later

Zeus's "or later" clause preserves forward-compatibility with downstream
GPL v3 works.

## Zeus contributors

Zeus is maintained by:

- **Brian Keating (EI6LF)** — project lead
- **Douglas J. Cerrato (KB2UKA)** — contributor

Additional contributions are visible in `git log` and in the repository's
pull-request history.

## Relationship to Thetis

Zeus is **an independent reimplementation in .NET — not a fork** of
Thetis. No Thetis binary is distributed with Zeus, and no Thetis source
file is carried in the Zeus tree.

That said, Zeus was **developed with direct reference to the Thetis
source** as the authoritative specification of OpenHPSDR Protocol-1 /
Protocol-2 client behaviour. The following categories of knowledge were
learned by reading Thetis source:

- Protocol-1 and Protocol-2 discovery and framing
- WDSP initialisation ordering and channel-state transitions
- Meter pipelines (S-meter, TX-stage meters)
- AGC curves, filter widths, bandwidth scheduling
- TX safety behaviour (SWR trip, TX timeout, TUNE)
- Console/radio wiring conventions

Under the GPL, code whose structure, behaviour, or implementation
detail is substantially informed by a GPL-covered work is itself
a derivative work. Accordingly, the Zeus codebase is treated as
**subject to the GNU General Public License**, the licence of its
upstream. Zeus's per-file headers, this document, and the root
`LICENSE` file together carry the full GPL v2-or-later notice
through the derivation chain.

Where any Zeus file is later identified as a close port of a specific
Thetis source file — rather than behaviour-informed original code — that
file will carry an additional per-file header naming the Thetis source,
the original copyright holders, and the date of modification, as required
by GPL v2 §2(a).

## Thetis — lineage and contributors

Thetis continues a long-running GPL-governed software lineage:

1. **FlexRadio PowerSDR** — the original GPL-licensed Software-Defined
   Radio client from FlexRadio Systems.
2. **OpenHPSDR ecosystem** (TAPR / OpenHPSDR) — continuation of the
   PowerSDR codebase as an open-hardware / open-source SDR platform.
3. **Thetis** — the modernised OpenHPSDR client implementation used as
   Zeus's reference.

The authoritative Thetis tree referenced by Zeus is:
<https://github.com/ramdor/Thetis>

Zeus gratefully acknowledges the Thetis contributors whose work — carried
forward through the lineage above — made this project possible:

| Name | Callsign |
| --- | --- |
| Richard Samphire | MW0LGE |
| Warren Pratt | NR0V |
| Laurence Barker | G8NJJ |
| Rick Koch | N1GP |
| Bryan Rambo | W4WMT |
| Chris Codella | W2PA |
| Doug Wigley | W5WC |
| Richard Allen | W5SD |
| Joe Torrey | WD5Y |
| Andrew Mansfield | M0YGG |
| Reid Campbell | MI0BOT |
| **FlexRadio Systems** | *(corporate)* |

Some Thetis contributions carry dual-licensing statements in addition
to the GPL. Where Zeus references or is informed by a specific Thetis
source file, any such dual-licensing notice from that file is to be
preserved in the corresponding Zeus per-file header — not stripped to
GPL alone.

## WDSP

Zeus loads **WDSP** (Warren Pratt, NR0V) via P/Invoke for all on-air DSP.
WDSP source ships in-tree under [`native/wdsp/`](native/wdsp/); its
upstream licence, copyright notices, and author attribution are
preserved in every file as received. Zeus builds a shared library from
that source at build time — it does not modify WDSP.

WDSP is Copyright (C) Warren Pratt (NR0V) and is distributed under
**GNU General Public License, version 2 or later**. See
<https://github.com/TAPR/OpenHPSDR-Thetis/tree/master/Project%20Files/Source/wdsp>
for the upstream.

Five small shim / glue files under `native/wdsp/` and
`native/wdsp/stubs/` were authored by Zeus contributors and are
GPL-2.0-or-later under the Zeus copyright:

- `native/wdsp/wdsp_export.h`
- `native/wdsp/stubs/rnnoise.h`
- `native/wdsp/stubs/rnnr_stub.c`
- `native/wdsp/stubs/sbnr_stub.c`
- `native/wdsp/stubs/specbleach_adenoiser.h`

## Relationship to pihpsdr

Zeus is independent of pihpsdr but **routinely consulted pihpsdr source as
the authoritative reference for Saturn-class (ANAN G2, G2 MkII, Saturn /
Saturn-XDMA) Protocol-2 behaviour**, particularly for:

- Hardware-peak values per board class (`transmitter.c`)
- Wire-format byte semantics on `CmdHighPriority` and `CmdTx` (`new_protocol.c`)
- PureSignal arm sequence and `tx_ps_reset` / `tx_ps_resume` patterns
- ALEX antenna routing for the PS feedback DDC pair
- DDC0 / DDC1 sample-pair convention into `pscc()`

pihpsdr is maintained by **Christoph Wüllen, DL1YCF** at
[github.com/dl1ycf/pihpsdr](https://github.com/dl1ycf/pihpsdr) and is
licensed GPL-2.0-or-later, compatible with Zeus.

Zeus acknowledges the following pihpsdr contributors whose work informed
Zeus's Protocol-2 / PureSignal implementation:

| Callsign |
| --- |
| DL1YCF (Christoph Wüllen) |
| DH1KLM |

## Relationship to DeskHPSDR

Zeus is independent of DeskHPSDR but consulted DeskHPSDR as a
cross-reference for HPSDR client behaviour. DeskHPSDR is maintained by
**Heiko, DL1BZ** at [github.com/dl1bz/deskhpsdr](https://github.com/dl1bz/deskhpsdr)
and is licensed GPL-2.0-or-later, compatible with Zeus.

## Third-party assets and imagery

Images under `docs/pics/` are original screenshots of the Zeus user
interface, unless explicitly stated otherwise in an adjacent caption or
`NOTICE` entry. No FlexRadio, Apache Labs (ANAN), or Thetis marketing
imagery is reproduced in this repository.

## Per-file header format

Every first-party Zeus source file begins with an SPDX identifier,
the Zeus copyright line, the short GPL notice, and an acknowledgement
block that names all twelve Thetis contributors, references pihpsdr
(DL1YCF / DH1KLM) and DeskHPSDR (DL1BZ), and points back at this file.
See any source file for the canonical form.

## Reporting attribution concerns

If you believe Zeus has inadequately attributed your work — or carries
content that should be attributed to you or to an upstream project —
please open an issue at
<https://github.com/brianbruff/openhpsdr-zeus/issues> or contact
the project lead directly. Zeus will treat attribution corrections as
a priority class of change.
