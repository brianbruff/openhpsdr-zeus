/* specbleach_adenoiser.h — Zeus stub
 *
 * Minimal stand-in for the libspecbleach header used when building WDSP
 * without the libspecbleach (NR4) backend. Only SpectralBleachHandle is
 * needed by sbnr.h's struct definition; all runtime calls live in
 * sbnr_stub.c.
 */

#ifndef ZEUS_SPECBLEACH_STUB_H
#define ZEUS_SPECBLEACH_STUB_H

typedef void *SpectralBleachHandle;

#endif
