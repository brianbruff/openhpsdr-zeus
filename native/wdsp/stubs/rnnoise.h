/* rnnoise.h — Nereus stub
 *
 * Minimal stand-in for <rnnoise.h> used when building WDSP without the
 * RNNoise (NR3) backend. Only the opaque DenoiseState type is needed by
 * rnnr.h's struct definition; all runtime calls live in rnnr_stub.c.
 */

#ifndef NEREUS_RNNOISE_STUB_H
#define NEREUS_RNNOISE_STUB_H

typedef struct DenoiseState DenoiseState;

#endif
