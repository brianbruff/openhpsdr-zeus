/* rnnr_stub.c — Nereus NR3 stub
 *
 * No-op replacement for rnnr.c used by the MVP build. Upstream rnnr.c
 * links against RNNoise; we defer that dependency and compile this file
 * instead (see native/wdsp/CMakeLists.txt, option WDSP_WITH_NR3_NR4).
 * Struct layout matches rnnr.h so that RXA.c's accesses — notably
 * `rxa[ch].rnnr.p->run` — remain well-defined. The `run` field stays
 * zero, so the NR3 path in RXA.c's ResCheck is never taken.
 */

#include "comm.h"

RNNR create_rnnr(int run, int position, int size, double *in, double *out, int rate) {
    (void)run; (void)position; (void)size; (void)in; (void)out; (void)rate;
    rnnr *a = (rnnr *)calloc(1, sizeof(rnnr));
    return a;
}

void destroy_rnnr(RNNR a) {
    if (a) free(a);
}

void setSize_rnnr(RNNR a, int size)            { (void)a; (void)size; }
void setBuffers_rnnr(RNNR a, double *in, double *out) { (void)a; (void)in; (void)out; }
void xrnnr(RNNR a, int pos)                    { (void)a; (void)pos; }
void setSamplerate_rnnr(RNNR a, int rate)      { (void)a; (void)rate; }
