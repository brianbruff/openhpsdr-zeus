#!/usr/bin/env bash
# native/build.sh — build libwdsp and stage it for .NET consumption.
#
# Usage:
#   ./native/build.sh                 # Release, osx-arm64 layout
#   ./native/build.sh Debug           # pass build type as arg
#
# Run from the repo root. Output goes to Zeus.Dsp/runtimes/<rid>/native/ so
# `dotnet publish` / `NativeLibrary.Load("wdsp")` picks it up automatically.

set -euo pipefail

BUILD_TYPE="${1:-Release}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
SRC_DIR="${SCRIPT_DIR}/wdsp"
BUILD_DIR="${SCRIPT_DIR}/build"

# Detect platform + arch -> .NET RID + shared-lib filename.
case "$(uname -s)" in
    Darwin)
        case "$(uname -m)" in
            arm64)  RID="osx-arm64" ;;
            x86_64) RID="osx-x64"   ;;
            *) echo "Unsupported macOS arch: $(uname -m)" >&2; exit 1 ;;
        esac
        LIB_NAME="libwdsp.dylib"
        ;;
    Linux)
        case "$(uname -m)" in
            aarch64|arm64) RID="linux-arm64" ;;
            x86_64)        RID="linux-x64"   ;;
            *) echo "Unsupported Linux arch: $(uname -m)" >&2; exit 1 ;;
        esac
        LIB_NAME="libwdsp.so"
        ;;
    *)
        echo "Unsupported host OS: $(uname -s). Use cmake directly on Windows." >&2
        exit 1
        ;;
esac

DEST_DIR="${REPO_ROOT}/Zeus.Dsp/runtimes/${RID}/native"
mkdir -p "${DEST_DIR}"

echo "==> Configuring (${BUILD_TYPE}, ${RID})"
cmake -S "${SRC_DIR}" -B "${BUILD_DIR}" -DCMAKE_BUILD_TYPE="${BUILD_TYPE}"

echo "==> Building"
cmake --build "${BUILD_DIR}" --config "${BUILD_TYPE}" --parallel

echo "==> Staging ${LIB_NAME} -> ${DEST_DIR}"
cp "${BUILD_DIR}/${LIB_NAME}" "${DEST_DIR}/${LIB_NAME}"

echo "==> Done. ${DEST_DIR}/${LIB_NAME}"
ls -lh "${DEST_DIR}/${LIB_NAME}"
