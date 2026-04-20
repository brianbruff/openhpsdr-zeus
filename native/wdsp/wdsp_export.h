/* wdsp_export.h
 *
 * Cross-platform symbol-visibility macro for the WDSP shared library.
 *
 * On Windows we emit __declspec(dllexport) when building the library;
 * on POSIX (Linux, macOS) we combine -fvisibility=hidden at the compiler
 * level with __attribute__((visibility("default"))) on exported entry points
 * so the exported ABI is a deliberate subset of the source's static surface.
 *
 * Replaces the Windows-only `PORT` macro in upstream WDSP (`comm.h`).
 */

#ifndef WDSP_EXPORT_H
#define WDSP_EXPORT_H

#if defined(_WIN32)
#  define WDSP_EXPORT __declspec(dllexport)
#else
#  define WDSP_EXPORT __attribute__((visibility("default")))
#endif

#endif /* WDSP_EXPORT_H */
