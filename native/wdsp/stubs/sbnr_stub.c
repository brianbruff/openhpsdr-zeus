/* sbnr_stub.c — Zeus NR4 stub
 *
 * No-op replacement for sbnr.c used by the MVP build. Upstream sbnr.c
 * links against libspecbleach; we defer that dependency and compile this
 * file instead (see native/wdsp/CMakeLists.txt, option WDSP_WITH_NR3_NR4).
 * Struct layout matches sbnr.h so that RXA.c's accesses — notably
 * `rxa[ch].sbnr.p->run` — remain well-defined. `run` stays zero, so the
 * NR4 path is never taken.
 */

#include "comm.h"

SBNR create_sbnr(int run, int position, int size, double *in, double *out, int rate) {
    (void)run; (void)position; (void)size; (void)in; (void)out; (void)rate;
    sbnr *a = (sbnr *)calloc(1, sizeof(sbnr));
    return a;
}

void destroy_sbnr(SBNR a) {
    if (a) free(a);
}

void setSize_sbnr(SBNR a, int size)            { (void)a; (void)size; }
void setBuffers_sbnr(SBNR a, double *in, double *out) { (void)a; (void)in; (void)out; }
void xsbnr(SBNR a, int pos)                    { (void)a; (void)pos; }
void setSamplerate_sbnr(SBNR a, int rate)      { (void)a; (void)rate; }
