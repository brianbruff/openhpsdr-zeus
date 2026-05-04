#!/bin/bash
# Script to create Zeus tarball for Linux
# Usage: ./create-linux-package.sh <version>
# Example: ./create-linux-package.sh 0.1.0

set -e

VERSION="${1:-0.0.0}"

echo "Creating Zeus package for Linux x64 v${VERSION}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/Zeus.Server/bin/Release/net10.0/linux-x64/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
PACKAGE_NAME="zeus-${VERSION}-linux-x64"
PACKAGE_DIR="${OUTPUT_DIR}/${PACKAGE_NAME}"

# Clean and create output directory
rm -rf "${PACKAGE_DIR}"
mkdir -p "${OUTPUT_DIR}"
mkdir -p "${PACKAGE_DIR}"

# Copy published files
echo "Copying published files..."
cp -r "${PUBLISH_DIR}"/* "${PACKAGE_DIR}/"

# Make the executable actually executable
chmod +x "${PACKAGE_DIR}/Zeus.Server"

# Create a launch script
cat > "${PACKAGE_DIR}/zeus" << 'EOF'
#!/bin/bash
# Zeus launcher script

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

# Pin the bundled libwdsp.so so an older system copy in /usr/lib or
# /usr/local/lib (e.g. left by a piHPSDR build) cannot shadow it. Linux
# does NOT search the executable's directory by default; without this
# line, dlopen("libwdsp.so") goes straight to LD_LIBRARY_PATH +
# /etc/ld.so.cache and may bind P/Invoke calls against a stale lib that
# pre-dates symbols Zeus relies on (e.g. SetRXAEMNRpost2*).
export LD_LIBRARY_PATH="${SCRIPT_DIR}/runtimes/linux-x64/native:${SCRIPT_DIR}/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"

# Cleanup handler to terminate the server subprocess on script exit.
# Ensures that Ctrl-C, kill, or terminal close properly stops Zeus.Server
# and prevents orphaned processes.
cleanup() {
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        echo ""
        echo "Stopping Zeus server..."
        kill -TERM "$SERVER_PID" 2>/dev/null || true
        # Wait up to 5 seconds for graceful shutdown
        for i in $(seq 1 10); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                break
            fi
            sleep 0.5
        done
        # Force kill if still running
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM

# Check if running in a display environment
if [ -n "$DISPLAY" ] || [ -n "$WAYLAND_DISPLAY" ]; then
    echo "Starting Zeus server on http://localhost:6060"
    echo "Opening browser in 2 seconds..."
    ./Zeus.Server &
    SERVER_PID=$!
    sleep 2

    # Try to open the browser
    if command -v xdg-open > /dev/null; then
        xdg-open http://localhost:6060 2>/dev/null &
    elif command -v gnome-open > /dev/null; then
        gnome-open http://localhost:6060 2>/dev/null &
    elif command -v kde-open > /dev/null; then
        kde-open http://localhost:6060 2>/dev/null &
    else
        echo "Could not automatically open browser."
        echo "Please open http://localhost:6060 in your web browser."
    fi

    echo "Zeus is running. Press Ctrl-C to stop."
    wait $SERVER_PID
else
    # No display, just run the server
    echo "Starting Zeus server on http://localhost:6060"
    echo "Open this URL in your web browser to access Zeus."
    ./Zeus.Server &
    SERVER_PID=$!
    echo "Zeus is running. Press Ctrl-C to stop."
    wait $SERVER_PID
fi
EOF
chmod +x "${PACKAGE_DIR}/zeus"

# Create README
cat > "${PACKAGE_DIR}/README.txt" << EOF
Zeus v${VERSION} for Linux

Installation:
1. Extract this archive to a location of your choice (e.g., ~/zeus or /opt/zeus)
2. Run ./zeus from the extracted directory
3. Your browser will open to http://localhost:6060

Command line usage:
  ./zeus           # Start Zeus and open browser (if in GUI environment)
  ./Zeus.Server    # Start Zeus server only (manual browser access)

Requirements:
- Linux x64 system (glibc-based; no system packages required — FFTW3 is
  statically linked into libwdsp.so and the .NET runtime is bundled)

For more information:
https://github.com/brianbruff/openhpsdr-zeus

License: GNU GPL v2 or later
Copyright (C) 2025-2026 Brian Keating (EI6LF), Douglas J. Cerrato (KB2UKA), and contributors
EOF

# Copy LICENSE
cp "${REPO_ROOT}/LICENSE" "${PACKAGE_DIR}/" 2>/dev/null || echo "LICENSE file not found, skipping"

# Create tarball
TARBALL_NAME="${PACKAGE_NAME}.tar.gz"
TARBALL_PATH="${OUTPUT_DIR}/${TARBALL_NAME}"
echo "Creating tarball..."
cd "${OUTPUT_DIR}"
tar -czf "${TARBALL_NAME}" "${PACKAGE_NAME}"
cd "${SCRIPT_DIR}"

echo "Package created at ${TARBALL_PATH}"
echo ""
echo "To install:"
echo "  tar -xzf ${TARBALL_NAME}"
echo "  cd ${PACKAGE_NAME}"
echo "  ./zeus"
