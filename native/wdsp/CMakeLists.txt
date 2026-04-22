# CMakeLists.txt — Zeus WDSP shared library build
#
# Produces:
#   macOS   : libwdsp.dylib  (arm64, x86_64)
#   Linux   : libwdsp.so     (x86_64, arm64)
#   Windows : wdsp.dll       (x64, arm64) — untested here, target is CMake-friendly
#
# Source is vendored upstream WDSP 1.29 (Warren Pratt) plus a linux_port.c
# portability shim. Only the PORT macro in comm.h is patched (→ WDSP_EXPORT in
# wdsp_export.h) so every upstream __declspec(dllexport) resolves to the right
# platform-specific visibility attribute.
#
# FFTW3 (double precision) is required. Single-precision fftw3f is NOT used on
# the RX call path WDSP-1.29 takes (verified: no fftwf_* references in the
# upstream sources), so we link fftw3 only.
#
# NR3 (RNNoise) and NR4 (libspecbleach) are off for MVP: we compile
# stubs/rnnr_stub.c and stubs/sbnr_stub.c in place of rnnr.c/sbnr.c. Enable via
# -DWDSP_WITH_NR3_NR4=ON once those deps are vendored.

cmake_minimum_required(VERSION 3.20)
project(wdsp
    VERSION 1.29.0
    DESCRIPTION "WDSP DSP engine, packaged for Zeus"
    LANGUAGES C
)

option(WDSP_WITH_NR3_NR4 "Build with RNNoise (NR3) and libspecbleach (NR4) support" OFF)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# -----------------------------------------------------------------------------
# Source list (mirrors upstream WDSP 1.29 Makefile SOURCES, minus rnnr.c and
# sbnr.c which are replaced by stubs when WDSP_WITH_NR3_NR4=OFF).
# -----------------------------------------------------------------------------
set(WDSP_SOURCES
    amd.c ammod.c amsq.c analyzer.c anf.c anr.c apfshadow.c bandpass.c
    calcc.c calculus.c cblock.c cfcomp.c cfir.c channel.c cmath.c compress.c
    delay.c dexp.c div.c doublepole.c eer.c emnr.c emph.c eq.c fcurve.c
    FDnoiseIQ.c fir.c firmin.c fmd.c fmmod.c fmsq.c gain.c gaussian.c gen.c
    icfir.c iir.c impulse_cache.c iobuffs.c iqc.c linux_port.c lmath.c main.c
    matchedCW.c meter.c meterlog10.c nbp.c nob.c nobII.c osctrl.c patchpanel.c
    resample.c rmatch.c RXA.c sender.c shift.c siphon.c slew.c snb.c ssql.c
    syncbuffs.c TXA.c utilities.c varsamp.c version.c wcpAGC.c wisdom.c zetaHat.c
)

if(WDSP_WITH_NR3_NR4)
    list(APPEND WDSP_SOURCES rnnr.c sbnr.c)
else()
    list(APPEND WDSP_SOURCES stubs/rnnr_stub.c stubs/sbnr_stub.c)
endif()

add_library(wdsp SHARED ${WDSP_SOURCES})

target_include_directories(wdsp
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
)

# Stub headers satisfy #include "rnnoise.h" and <specbleach_adenoiser.h> in
# rnnr.h / sbnr.h when the real NR3/NR4 libraries are absent.
if(NOT WDSP_WITH_NR3_NR4)
    target_include_directories(wdsp PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/stubs)
endif()

# -----------------------------------------------------------------------------
# Compiler flags
# -----------------------------------------------------------------------------
if(MSVC)
    target_compile_options(wdsp PRIVATE /O2 /fp:precise /W3 /wd4244 /wd4267)
    target_compile_definitions(wdsp PRIVATE _CRT_SECURE_NO_WARNINGS)
else()
    target_compile_options(wdsp PRIVATE
        -O3
        -pthread
        -Wno-parentheses
        -Wno-unused-result
        -Wno-unused-but-set-variable
        -Wno-unused-variable
        -Wno-unused-function
        -Wno-deprecated-non-prototype
        -Wno-implicit-function-declaration
        -Wno-incompatible-pointer-types
    )
    target_compile_definitions(wdsp PRIVATE _GNU_SOURCE)
    target_link_options(wdsp PRIVATE -pthread)
endif()

# -----------------------------------------------------------------------------
# FFTW3 (double precision)
# -----------------------------------------------------------------------------
find_package(PkgConfig QUIET)
set(_fftw_found FALSE)

if(PkgConfig_FOUND)
    pkg_check_modules(FFTW3 QUIET fftw3)
    if(FFTW3_FOUND)
        target_include_directories(wdsp PRIVATE ${FFTW3_INCLUDE_DIRS})
        target_link_libraries(wdsp PRIVATE ${FFTW3_LIBRARIES})
        target_link_directories(wdsp PRIVATE ${FFTW3_LIBRARY_DIRS})
        set(_fftw_found TRUE)
    endif()
endif()

# vcpkg / CMake CONFIG mode — used when the vcpkg toolchain is active (Windows)
# or any platform where FFTW3 ships a CMake config package.
if(NOT _fftw_found)
    find_package(FFTW3 CONFIG QUIET)
    if(FFTW3_FOUND)
        target_link_libraries(wdsp PRIVATE FFTW3::fftw3)
        set(_fftw_found TRUE)
        message(STATUS "FFTW3 found via CMake config (vcpkg or system package)")
    endif()
endif()

# macOS Homebrew fallback (arm64: /opt/homebrew, x86_64: /usr/local).
if(NOT _fftw_found AND APPLE)
    foreach(_prefix /opt/homebrew/opt/fftw /usr/local/opt/fftw)
        if(EXISTS ${_prefix}/include/fftw3.h)
            target_include_directories(wdsp PRIVATE ${_prefix}/include)
            target_link_directories(wdsp PRIVATE ${_prefix}/lib)
            target_link_libraries(wdsp PRIVATE fftw3)
            set(_fftw_found TRUE)
            message(STATUS "FFTW3 found via Homebrew prefix: ${_prefix}")
            break()
        endif()
    endforeach()
endif()

if(NOT _fftw_found)
    message(FATAL_ERROR "FFTW3 not found. Install with: brew install fftw (macOS) | apt install libfftw3-dev (Linux) | see native/README.md for Windows.")
endif()

# -----------------------------------------------------------------------------
# Windows system libraries: main.c calls AvSetMmThreadCharacteristics /
# AvSetMmThreadPriority / AvRevertMmThreadCharacteristics (Multimedia Class
# Scheduler "Pro Audio") which live in avrt.lib. Thetis's wdsp.vcxproj does
# the same.
# -----------------------------------------------------------------------------
if(WIN32)
    target_link_libraries(wdsp PRIVATE avrt)
endif()

# -----------------------------------------------------------------------------
# Output naming: match .NET NativeLibrary resolution defaults.
#   macOS   : libwdsp.dylib  (OUTPUT_NAME=wdsp + PREFIX=lib + .dylib)
#   Linux   : libwdsp.so
#   Windows : wdsp.dll       (no lib prefix)
# -----------------------------------------------------------------------------
set_target_properties(wdsp PROPERTIES
    OUTPUT_NAME wdsp
    C_VISIBILITY_PRESET hidden
)
if(WIN32)
    set_target_properties(wdsp PROPERTIES PREFIX "")
endif()

message(STATUS "wdsp: build type = ${CMAKE_BUILD_TYPE}")
message(STATUS "wdsp: NR3/NR4 enabled = ${WDSP_WITH_NR3_NR4}")
