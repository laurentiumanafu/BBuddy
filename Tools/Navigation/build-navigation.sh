#!/usr/bin/env bash
# Builds Navigation.dll for x64 (and optionally x86) from Likon69/Navigation-C-.
#
# Why: Lib\Navigation.dll is a 32-bit build. The bot is x64 now, so it needs an
# x64 DLL with the same C ABI. The upstream source compiles for MSVC only, so four
# one-line portability fixes are applied first (patches/navigation-x64-port.patch).
#
# Requirements: git, zig (any recent version; zig carries a clang + lld that target
# Windows). On Windows use build-navigation.ps1 with MSVC instead.
#
#   ./build-navigation.sh              # x64 only, into Lib/x64/
#   ./build-navigation.sh --both       # x64 + x86, into Lib/x64/ and Lib/
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UPSTREAM="${NAVIGATION_UPSTREAM:-https://github.com/Likon69/Navigation-C-.git}"
PINNED_COMMIT="${NAVIGATION_COMMIT:-221dfe2}"
WORK="${NAVIGATION_BUILD_DIR:-/tmp/navigation-build}"
PATCH="$REPO_ROOT/Tools/Navigation/patches/navigation-x64-port.patch"

SOURCES=(
  Detour/Source/DetourAlloc.cpp
  Detour/Source/DetourAssert.cpp
  Detour/Source/DetourCommon.cpp
  Detour/Source/DetourNavMesh.cpp
  Detour/Source/DetourNavMeshBuilder.cpp
  Detour/Source/DetourNavMeshQuery.cpp
  Detour/Source/DetourNode.cpp
  DetourCrowd/Source/DetourPathCorridor.cpp
  DllMain.cpp
  MoveMap.cpp
  Navigation.cpp
  NavBridge.cpp
  OffMeshManager.cpp
  PathFinder.cpp
  PathResult.cpp
)

# Same defines as Navigation.vcxproj: DT_POLYREF64 changes dtPolyRef to 64-bit,
# which the C# PathResult layout assumes.
DEFINES=(
  -DUSE_STANDARD_MALLOC -DDT_POLYREF64 -DPREPARED_SLN
  -D_WINDOWS -D_WIN32 -D_CRT_SECURE_NO_WARNINGS -DNAVIGATION_EXPORTS
  -include windows.h
)
INCLUDES=(-I . -I Detour/Include -I DetourCrowd/Include -I g3dlite/Include -I Utilities)

command -v zig >/dev/null || { echo "zig not found (pip install ziglang, or use build-navigation.ps1 on Windows)"; exit 1; }

if [ ! -d "$WORK/Navigation" ]; then
  rm -rf "$WORK"
  git clone "$UPSTREAM" "$WORK"
fi
cd "$WORK"
git checkout -q "$PINNED_COMMIT" 2>/dev/null || echo "note: pinned commit $PINNED_COMMIT unavailable, using HEAD"
git checkout -q -- . || true
if ! git apply --check "$PATCH" 2>/dev/null; then
  echo "patch does not apply; upstream changed. Inspect Tools/Navigation/patches/navigation-x64-port.patch"
  exit 1
fi
git apply "$PATCH"

build() {
  local target="$1" out="$2"
  mkdir -p "$(dirname "$out")"
  ( cd "$WORK/Navigation" && zig c++ -target "$target" -shared -O2 -w \
      "${DEFINES[@]}" "${INCLUDES[@]}" -static-libgcc -static-libstdc++ \
      -o "$out" "${SOURCES[@]}" )
  echo "built $out"
}

build x86_64-windows-gnu "$REPO_ROOT/Lib/x64/Navigation.dll"
if [ "${1:-}" = "--both" ]; then
  build i686-windows-gnu "$REPO_ROOT/Lib/Navigation.dll"
fi

python3 "$REPO_ROOT/Tools/Navigation/check_pe.py" "$REPO_ROOT/Lib/x64/Navigation.dll"
