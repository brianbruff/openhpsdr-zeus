# libspecbleach — vendoring notes

This is an in-tree copy of **libspecbleach** (Luciano Dato), the spectral-bleaching
audio noise-reduction library that WDSP's `sbnr.c` / SBNR / NR4 path links against.

## Provenance

Sourced from the **MW0LGE-modified** copy that ships with Thetis, at:

```
Thetis/Project Files/lib/NR_Algorithms_x64/src/libspecbleach/
```

Choosing Thetis's copy over upstream `lucianodato/libspecbleach` is deliberate:
Thetis's NR4 behavior is the reference Zeus is matching. Using Thetis's exact
libspecbleach means our `specbleach_adaptive_*` calls in `native/wdsp/sbnr.c`
behave bit-for-bit the same on every platform.

The MW0LGE modifications are concentrated in `CMakeLists.txt` (FFTW3f path
discovery for the Windows build); the algorithmic source under `src/` matches
upstream as of the Thetis snapshot.

## Licence

LGPL v2.1 (see `LICENSE`). LGPL → GPL-2.0-or-later is one-way compatible, so
linking libspecbleach into Zeus's GPL-2-or-later distribution is fine. The
`LICENSE` file is preserved verbatim.

## Re-vendoring

```sh
THETIS=/path/to/Thetis-source
rm -rf native/libspecbleach
mkdir native/libspecbleach
cp -a "$THETIS/Project Files/lib/NR_Algorithms_x64/src/libspecbleach/." native/libspecbleach/
# preserve this VENDORING.md if upstream snapshot doesn't carry it
```

If Thetis bumps libspecbleach to a newer snapshot, repeat. Don't pick up
`old.gitignore` blindly into Zeus's `.gitignore` — it's a stray file from the
upstream meson build and only makes sense in libspecbleach's own repo.

## Build

The library is built as a sub-target of `native/wdsp/CMakeLists.txt` when
`-DWDSP_WITH_NR3_NR4=ON` (see `native/README.md` § "MVP API surface" and the
Phase 1b CMake wiring). The Meson build files (`meson.build`, `meson_options.txt`)
are left in place for parity with upstream but **not used** by Zeus — Zeus only
drives the CMake build path.
