/* specbleach_adenoiser.h — Nereus stub
 *
 * Minimal stand-in for the libspecbleach header used when building WDSP
 * without the libspecbleach (NR4) backend. Only SpectralBleachHandle is
 * needed by sbnr.h's struct definition; all runtime calls live in
 * sbnr_stub.c.
 */

#ifndef NEREUS_SPECBLEACH_STUB_H
#define NEREUS_SPECBLEACH_STUB_H

typedef void *SpectralBleachHandle;

#endif
