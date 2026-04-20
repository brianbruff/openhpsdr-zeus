# native/ — WDSP cross-platform build

This directory vendors the WDSP DSP engine (Warren Pratt, GPLv3) and builds it
as a shared library that `Nereus.Dsp` loads via P/Invoke.

Source baseline: **`deskhpsdr/wdsp-1.29/`**. deskhpsdr already ported WDSP off
MSVC with a `linux_port.{c,h}` shim and `#ifdef _WIN32` guards. Thetis's own
WDSP tree is MSVC-only and is **not** suitable as an upstream.

Layout:

```
native/
  wdsp/                 # upstream .c/.h from deskhpsdr/wdsp-1.29
  wdsp/stubs/           # minimal headers + no-op rnnr/sbnr for MVP
  wdsp/wdsp_export.h    # WDSP_EXPORT visibility macro (replaces PORT)
  wdsp/CMakeLists.txt   # the real build
  build.sh              # convenience wrapper -> stages .dylib into Nereus.Dsp
  build/                # generated CMake cache (gitignored)
```

## Build on macOS (arm64 / x86_64)

```sh
brew install fftw cmake
./native/build.sh                # Release, output -> Nereus.Dsp/runtimes/<rid>/native/
./native/build.sh Debug          # optional: Debug build
```

The script auto-detects `osx-arm64` vs `osx-x64` and stages `libwdsp.dylib`
into the matching `Nereus.Dsp/runtimes/<rid>/native/` directory. .NET's default
native library resolution picks it up with no extra configuration.

## Build on Linux (x86_64 / arm64)

```sh
sudo apt install libfftw3-dev cmake build-essential pkg-config     # Debian/Ubuntu
sudo dnf install fftw-devel cmake gcc pkgconf                      # Fedora/RHEL
./native/build.sh
```

Produces `Nereus.Dsp/runtimes/linux-x64/native/libwdsp.so` (or `linux-arm64`).

## Build on Windows (x64)

Windows support is wired into the CMakeLists but has not been validated. Rough
recipe:

```powershell
winget install Kitware.CMake
winget install Microsoft.VisualStudio.2022.BuildTools   # or full VS
# FFTW3: either vcpkg (vcpkg install fftw3:x64-windows) or
# download prebuilt DLLs from https://www.fftw.org/install/windows.html and
# point CMake at them via -DCMAKE_PREFIX_PATH.
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A x64
cmake --build native\build --config Release
copy native\build\Release\wdsp.dll Nereus.Dsp\runtimes\win-x64\native\
```

Known Windows TODOs:

- No pkg-config on Windows by default; `find_package(FFTW3)` only fires if the
  user supplies `-DCMAKE_PREFIX_PATH=…`. A `cmake/FindFFTW3.cmake` module is
  the likely next step (doc 03 §3.4 notes this).
- `/fp:precise` is set, but wider MSVC-specific warning suppression may be
  needed once someone runs a real build.

## MVP API surface

`-fvisibility=hidden` is set at the compiler level, so only the functions
marked `PORT` (→ `WDSP_EXPORT`) in the upstream WDSP headers are exported.
That's ~500 symbols on the current build — a proper superset of the ~20 the
C# wrapper in `Nereus.Dsp/` uses. The wrapper only P/Invokes names that
actually exist.

Verify the MVP surface after a build:

```sh
nm -gU Nereus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib \
  | grep -E 'OpenChannel|CloseChannel|SetRXAMode|XCreateAnalyzer|SetAnalyzer|GetPixels|Spectrum0|fexchange0|DestroyAnalyzer'
```

Note: the symbol is `DestroyAnalyzer` (capital D), not `destroy_analyzer`.
`Spectrum`, `Spectrum0`, and `Spectrum2` are all exported; `Spectrum0` is the
one `fexchange0`-driven callers use.

## Source modifications vs. upstream

Diff against `deskhpsdr/wdsp-1.29/` is intentionally tiny:

1. `comm.h` — replaced `#define PORT __declspec(dllexport)` with an include of
   `wdsp_export.h` and `#define PORT WDSP_EXPORT`. This is the single change
   needed to get proper symbol export on all three OSes.
2. `wdsp_export.h` — new file, holds the cross-platform visibility macro.
3. `stubs/rnnoise.h` + `stubs/specbleach_adenoiser.h` — minimal opaque types
   so `rnnr.h` / `sbnr.h` compile without Xiph RNNoise or libspecbleach
   available. Only active when `-DWDSP_WITH_NR3_NR4=OFF` (the default).
4. `stubs/rnnr_stub.c` + `stubs/sbnr_stub.c` — no-op replacements for
   `rnnr.c` and `sbnr.c`. These provide the entry points RXA.c calls (with
   `run` stuck at 0, the NR3/NR4 branches never execute) so we can build
   without pulling in the upstream noise-reduction libraries. To enable NR3/4
   later: vendor RNNoise and libspecbleach, then build with
   `cmake -DWDSP_WITH_NR3_NR4=ON …`.

No other files are modified. `linux_port.{c,h}` does all the Win32 → POSIX
shimming (pthreads, aligned malloc, Sleep, `__declspec`).

## Re-vendoring upstream WDSP

Bumping to a newer deskhpsdr snapshot is mechanical:

```sh
rm native/wdsp/*.c native/wdsp/*.h
cp /path/to/new-deskhpsdr/wdsp-1.29/*.{c,h} native/wdsp/
# re-apply the comm.h PORT edit (see step 1 above)
./native/build.sh
```

Don't copy deskhpsdr's `.o` files, `Makefile`, or `COMPILE.*` notes — we own
the build system now.
