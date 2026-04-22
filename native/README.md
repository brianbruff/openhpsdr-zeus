# native/ — WDSP cross-platform build

This directory vendors the WDSP DSP engine (Warren Pratt, GPLv3) and builds it
as a shared library that `Zeus.Dsp` loads via P/Invoke.

Source baseline: **upstream WDSP 1.29** (Warren Pratt) plus a
`linux_port.{c,h}` portability shim and `#ifdef _WIN32` guards to get WDSP off
MSVC. Thetis's own WDSP tree is MSVC-only and is **not** suitable as an
upstream.

Layout:

```
native/
  wdsp/                 # vendored upstream WDSP 1.29 .c/.h
  wdsp/stubs/           # minimal headers + no-op rnnr/sbnr for MVP
  wdsp/wdsp_export.h    # WDSP_EXPORT visibility macro (replaces PORT)
  wdsp/CMakeLists.txt   # the real build
  build.sh              # convenience wrapper -> stages .dylib into Zeus.Dsp
  build/                # generated CMake cache (gitignored)
```

## Build on macOS (arm64 / x86_64)

```sh
brew install fftw cmake
./native/build.sh                # Release, output -> Zeus.Dsp/runtimes/<rid>/native/
./native/build.sh Debug          # optional: Debug build
```

The script auto-detects `osx-arm64` vs `osx-x64` and stages `libwdsp.dylib`
into the matching `Zeus.Dsp/runtimes/<rid>/native/` directory. .NET's default
native library resolution picks it up with no extra configuration.

## Build on Linux (x86_64 / arm64)

```sh
sudo apt install libfftw3-dev cmake build-essential pkg-config     # Debian/Ubuntu
sudo dnf install fftw-devel cmake gcc pkgconf                      # Fedora/RHEL
./native/build.sh
```

Produces `Zeus.Dsp/runtimes/linux-x64/native/libwdsp.so` (or `linux-arm64`).

## Build on Windows (x64 / arm64)

Windows native libraries are built automatically via GitHub Actions (see
`.github/workflows/build-native-libs.yml`). The workflow uses vcpkg to install
FFTW3 and builds for both x64 and arm64.

For local development:

```powershell
# Install dependencies
vcpkg install fftw3:x64-windows
# or for ARM64: vcpkg install fftw3:arm64-windows

# Configure (x64)
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A x64 `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake"

# Configure (ARM64)
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A ARM64 `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake"

# Build
cmake --build native\build --config Release

# Stage
copy native\build\Release\wdsp.dll Zeus.Dsp\runtimes\win-x64\native\
# or for ARM64: copy native\build\Release\wdsp.dll Zeus.Dsp\runtimes\win-arm64\native\
```

## Automated Builds via GitHub Actions

Native libraries for Windows and Linux are automatically built by the
`.github/workflows/build-native-libs.yml` workflow. This workflow:

- Builds for Windows (x64, arm64) using MSVC and vcpkg
- Builds for Linux (x64, arm64) using GCC
- Stages the libraries in `Zeus.Dsp/runtimes/<rid>/native/`
- Can be triggered manually via workflow_dispatch or automatically on changes to `native/`

To trigger a manual build, go to Actions → "Build Native WDSP Libraries" → "Run workflow".

## MVP API surface

`-fvisibility=hidden` is set at the compiler level, so only the functions
marked `PORT` (→ `WDSP_EXPORT`) in the upstream WDSP headers are exported.
That's ~500 symbols on the current build — a proper superset of the ~20 the
C# wrapper in `Zeus.Dsp/` uses. The wrapper only P/Invokes names that
actually exist.

Verify the MVP surface after a build:

```sh
nm -gU Zeus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib \
  | grep -E 'OpenChannel|CloseChannel|SetRXAMode|XCreateAnalyzer|SetAnalyzer|GetPixels|Spectrum0|fexchange0|DestroyAnalyzer'
```

Note: the symbol is `DestroyAnalyzer` (capital D), not `destroy_analyzer`.
`Spectrum`, `Spectrum0`, and `Spectrum2` are all exported; `Spectrum0` is the
one `fexchange0`-driven callers use.

## Source modifications vs. upstream

Diff against upstream WDSP 1.29 is intentionally tiny:

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

Bumping to a newer WDSP snapshot is mechanical:

```sh
rm native/wdsp/*.c native/wdsp/*.h
cp /path/to/new-wdsp-1.29/*.{c,h} native/wdsp/
# re-apply the comm.h PORT edit (see step 1 above)
./native/build.sh
```

Don't copy upstream `.o` files, `Makefile`, or `COMPILE.*` notes — we own the
build system now.
