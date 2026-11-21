#!/usr/bin/env bash
#
# Build self-contained release packages.
#
# Usage:
#   scripts/package-release.sh                 # all three desktop RIDs, PGO mode
#   scripts/package-release.sh win-x64         # one RID
#   MODE=r2r scripts/package-release.sh        # ReadyToRun comparison build
#   MODE=aot scripts/package-release.sh        # NativeAOT comparison build
#   PGO_VERIFY=1 scripts/package-release.sh osx-arm64
#
# MODE=pgo is the release default: the runtime's dynamic profile-guided
# optimization (TieredPGO), Server GC, and the call-counting delay of zero are
# the combination that measured fastest at steady state on every host tested.
# MODE=r2r and MODE=aot exist so the alternatives can be measured on a new host;
# both measured slower than pgo on the development host and are not default.
#
# Output:
#   publish/PawnForgeCS-<version>-<rid>.zip
#
# Each zip contains one top-level folder with the engine executable, README.md,
# the feature list, LICENSE and THIRD-PARTY-NOTICES.md.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# NativeAOT produces a native binary for the HOST only; R2R and the JIT/PGO
# build can cross-compile because the runtime pack supplies the target runtime.
HOST_RID=""
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)  HOST_RID="osx-arm64" ;;
  Darwin-x86_64) HOST_RID="osx-x64" ;;
  Linux-x86_64)  HOST_RID="linux-x64" ;;
  Linux-aarch64) HOST_RID="linux-arm64" ;;
  MINGW*|MSYS*|CYGWIN*) HOST_RID="win-x64" ;;
esac

VERSION="${VERSION:-$(sed -n 's/.*EngineVersion = "\([^"]*\)".*/\1/p' \
  src/PawnForge.UCI/UciHandler.cs | head -1)}"
VERSION="${VERSION:-2.0}"

MODE="${MODE:-pgo}"
case "$MODE" in
  pgo)
    # Dynamic PGO + Server GC + immediate call counting (the csproj also sets
    # all three; repeating them keeps the published command self-describing).
    MODE_FLAGS=(
      -p:PublishSingleFile=true
      -p:IncludeNativeLibrariesForSelfExtract=true
      -p:ServerGarbageCollection=true
      -p:TieredPGO=true
    )
    ;;
  r2r)
    # Composite ReadyToRun: faster first call, but steady-state slower than the
    # tier-1 dynamic-PGO code it replaces. Comparison build only.
    MODE_FLAGS=(
      -p:PublishSingleFile=true
      -p:IncludeNativeLibrariesForSelfExtract=true
      -p:PublishReadyToRun=true
      -p:PublishReadyToRunComposite=true
      -p:ServerGarbageCollection=true
      -p:TieredPGO=true
    )
    ;;
  aot)
    # NativeAOT: no JIT, smallest artifact and fastest start, but no dynamic
    # profile feedback. Comparison build only.
    MODE_FLAGS=(
      -p:PublishAot=true
      -p:OptimizationPreference=Speed
      -p:StripSymbols=true
      -p:ServerGarbageCollection=true
    )
    ;;
  *)
    echo "error: MODE must be pgo, r2r or aot (got '$MODE')" >&2
    exit 1
    ;;
esac

if [ "$#" -gt 0 ]; then
  RIDS=("$@")
else
  RIDS=(win-x64 osx-arm64 linux-x64)
fi

for RID in "${RIDS[@]}"; do
  if [ "$MODE" = "aot" ] && [ -n "$HOST_RID" ] && [ "$RID" != "$HOST_RID" ]; then
    echo "error: NativeAOT must be built on the target OS (host is $HOST_RID, asked for $RID)" >&2
    exit 1
  fi
  echo "==> publishing $RID (mode: $MODE)"
  OUT="publish/$RID"
  rm -rf "$OUT"
  dotnet publish src/PawnForge.UCI/PawnForge.UCI.csproj \
    -c Release -r "$RID" --self-contained true \
    "${MODE_FLAGS[@]}" \
    -o "$OUT" --nologo -v q

  cp README.md LICENSE THIRD-PARTY-NOTICES.md PawnforgeCS-feature-list.md "$OUT/"

  # Optional: prove the PGO effect on the host that can run the artifact.
  if [ "${PGO_VERIFY:-0}" = "1" ] && [ "$MODE" = "pgo" ]; then
    ENGINE="$OUT/PawnForge.UCI"
    [ -f "$ENGINE.exe" ] && ENGINE="$ENGINE.exe"
    if [ -x "$ENGINE" ]; then
      python3 scripts/pgo_verify.py --engine "$ENGINE" --depth 18 --runs 3
    fi
  fi

  STAGE="publish/PawnForgeCS-$VERSION-$RID"
  rm -rf "$STAGE"
  cp -R "$OUT" "$STAGE"

  ZIP="PawnForgeCS-$VERSION-$RID.zip"
  rm -f "publish/$ZIP"
  if command -v zip >/dev/null 2>&1; then
    (cd publish && zip -q -r "$ZIP" "PawnForgeCS-$VERSION-$RID")
  elif tar -a -c -f "publish/$ZIP" -C publish "PawnForgeCS-$VERSION-$RID" 2>/dev/null; then
    :
  else
    echo "error: neither 'zip' nor a zip-capable 'tar' is available" >&2
    exit 1
  fi
  rm -rf "$STAGE"

  echo "    publish/$ZIP"
done

echo "done."
