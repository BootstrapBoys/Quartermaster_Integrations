#!/usr/bin/env bash
# Dev build: compile the mod and drop the DLL in artifacts/debug for in-game testing.
#
# Release packaging is deliberately NOT done here - that only happens on an explicit release
# request, and produces the zip in artifacts/release (see resources/CHANGELOG.md for the versioning rules).
set -euo pipefail
cd "$(dirname "$0")/.."          # project root, one level up from tooling/

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export VALHEIM_DIR="${VALHEIM_DIR:-$PWD/refs/game}"
export BEPINEX_DIR="${BEPINEX_DIR:-$PWD/refs/game/BepInEx}"

dotnet build QuarterMaster_Integrations.csproj -c Release \
  -p:TreatWarningsAsErrors=true -p:WarningLevel=9999 -p:NoWarn=

echo "=== binding audit ==="
dotnet run --project tooling/build-check/Inspect -c Release

OUT="artifacts/debug"
mkdir -p "$OUT"
cp bin/Release/net48/QuarterMaster_Integrations.dll "$OUT/QuarterMaster_Integrations.dll"

VERSION=$(grep -oP 'PluginVersion = "\K[0-9.]+' src/qi_main.cs)
SHA=$(python3 -c "import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())" \
      "$OUT/QuarterMaster_Integrations.dll")

cat > "$OUT/build-info.txt" <<EOF
QuarterMaster_Integrations $VERSION
built   $(date -u '+%Y-%m-%d %H:%M UTC')
sha256  $SHA
source  $(pwd)
EOF

# Build outputs are regenerable and get in the way of reading the tree; the candidate in artifacts/
# is the deliverable, so everything msbuild produced is removed once it has been copied.
rm -rf bin obj tooling/build-check/bin tooling/build-check/obj \
       tooling/build-check/Inspect/bin tooling/build-check/Inspect/obj

echo "artifacts/debug/QuarterMaster_Integrations.dll  ($VERSION)"
