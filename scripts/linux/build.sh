#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# Build natively on the Linux PC or remote Pi. Override for smaller-memory Pis.
cmake -S . -B build/linux -DCMAKE_BUILD_TYPE=Release
cmake --build build/linux --parallel "${BUILD_JOBS:-2}"
