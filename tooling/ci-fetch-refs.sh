#!/usr/bin/env bash
# Fetch every reference assembly CI needs to build and audit this mod. Nothing downloaded here is
# committed: refs/ and .ci/ are gitignored.
#
#   game assemblies  - anonymous Valheim dedicated-server depot (app 896660), same managed
#                      assemblies the client ships, so a valid compile reference
#   BepInEx core     - Thunderstore, pinned in resources/manifest.json
#   dependency mods  - Thunderstore where published, Hexium otherwise (QA/AAA/AAABuildMenu are
#                      Hexium-only or newer there); versions are pinned and asserted
#
# QI_CI_WORK can point at a reusable download cache.
set -euo pipefail
cd "$(dirname "$0")/.."

ROOT="$PWD"
WORK="${QI_CI_WORK:-$ROOT/.ci}"
DOWNLOADS="$WORK/downloads"
GAME="$WORK/game"
DEPOT_DOWNLOADER_VERSION="DepotDownloader_3.4.0"

for tool in curl unzip python3; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 1; }
done

mkdir -p "$DOWNLOADS" refs/game/valheim_Data/Managed refs/game/BepInEx/core refs/mods refs/candidates

fetch_zip() { # url, destination
  local url="$1" out="$2"
  if [ ! -f "$out" ]; then
    echo "Downloading $(basename "$out")"
    curl -fsSL --retry 3 --retry-delay 5 -o "$out" "$url"
  fi
}

# manifest dependency, by prefix (e.g. "shudnal-MyLittleUI-")
dep() {
  python3 - "$1" <<'PY'
import json, sys
prefix = sys.argv[1]
deps = json.load(open("resources/manifest.json"))["dependencies"]
print(next(d for d in deps if d.startswith(prefix)))
PY
}

# ---- 1. Game assemblies --------------------------------------------------------------------
DEPOT_DOWNLOADER="$WORK/depot/DepotDownloader"
if [ ! -x "$DEPOT_DOWNLOADER" ]; then
  fetch_zip \
    "https://github.com/SteamRE/DepotDownloader/releases/download/${DEPOT_DOWNLOADER_VERSION}/DepotDownloader-linux-x64.zip" \
    "$WORK/depot.zip"
  mkdir -p "$WORK/depot"
  unzip -q -o "$WORK/depot.zip" -d "$WORK/depot"
  chmod +x "$DEPOT_DOWNLOADER"
fi

if [ ! -f "$GAME/valheim_server_Data/Managed/assembly_valheim.dll" ]; then
  echo "Downloading Valheim dedicated server (app 896660, anonymous)"
  "$DEPOT_DOWNLOADER" -app 896660 -dir "$GAME" -validate
fi
cp -f "$GAME/valheim_server_Data/Managed/"*.dll refs/game/valheim_Data/Managed/

# ---- 2. Thunderstore ------------------------------------------------------------------------
thunderstore() { # package full name, dll file name, destination name
  local pkg="$1" dll="$2" dest="$3"
  fetch_zip "https://gcdn.thunderstore.io/live/repository/packages/${pkg}.zip" "$DOWNLOADS/${pkg}.zip"
  rm -rf "$WORK/pkg"
  mkdir -p "$WORK/pkg"
  unzip -q -o "$DOWNLOADS/${pkg}.zip" -d "$WORK/pkg"
  local found
  found="$(find "$WORK/pkg" -name "$dll" -print -quit)"
  [ -n "$found" ] || { echo "$dll not found in $pkg" >&2; exit 1; }
  cp -f "$found" "refs/mods/$dest"
}

# ---- 3. Hexium (versioned endpoint, so the pin is exact) -------------------------------------
hexium() { # namespace, name, version, dll file name
  local ns="$1" name="$2" version="$3" dll="$4"
  local meta url
  meta="$(curl -fsSL --retry 3 "https://valheim.hexium.gg/api/experimental/package/${ns}/${name}/${version}/")"
  url="$(printf '%s' "$meta" | python3 -c '
import json, sys
want = sys.argv[1]
d = json.load(sys.stdin)
if d.get("version_number") != want:
    sys.exit("Hexium version mismatch: %s != %s" % (d.get("version_number"), want))
print(d["download_url"])
' "$version")"

  fetch_zip "$url" "$DOWNLOADS/${name}-${version}.zip"
  rm -rf "$WORK/pkg"
  mkdir -p "$WORK/pkg"
  unzip -q -o "$DOWNLOADS/${name}-${version}.zip" -d "$WORK/pkg"
  local found
  found="$(find "$WORK/pkg" -name "$dll" -print -quit)"
  [ -n "$found" ] || { echo "$dll not found in Hexium ${ns}/${name}" >&2; exit 1; }
  cp -f "$found" "refs/mods/$dll"
}

# BepInEx core (needed to compile at all)
BEPINEX_PKG="$(dep 'denikson-BepInExPack_Valheim-')"
fetch_zip "https://gcdn.thunderstore.io/live/repository/packages/${BEPINEX_PKG}.zip" "$DOWNLOADS/bepinex.zip"
rm -rf "$WORK/bepinex" && mkdir -p "$WORK/bepinex"
unzip -q -o "$DOWNLOADS/bepinex.zip" -d "$WORK/bepinex"
BEPINEX_CORE="$(find "$WORK/bepinex" -type d -path '*/BepInEx/core' -print -quit)"
[ -n "$BEPINEX_CORE" ] || { echo "BepInEx core not found in package" >&2; exit 1; }
cp -f "$BEPINEX_CORE/"*.dll refs/game/BepInEx/core/

# Optional integrations the binding audit checks. They are deliberately NOT manifest dependencies,
# so their pins live here and get bumped when the audit is re-run against a newer build.
MYLITTLEUI_PKG="shudnal-MyLittleUI-1.2.33"
EXTRASLOTS_PKG="shudnal-ExtraSlots-1.2.21"
ADVENTUREBACKPACKS_PKG="Vapok-AdventureBackpacks-2.4.6"
AAA_VERSION="2.1.12"
AAABUILDMENU_VERSION="1.0.6"
EPICLOOT_PKG="RandyKnapp-EpicLoot-0.15.5"
PLANBUILD_PKG="MathiasDecrock-PlanBuild-0.20.0"
WARDISLOVE_VERSION="4.0.9"
WIZARDRY_PKG="Therzie-Wizardry-1.2.3"
DROPMORELOOT_PKG="TastyChickenLegs-DropMoreLoot-1.2.9"

# Optional dependencies the binding audit checks, at the versions the code is written against.
thunderstore "$MYLITTLEUI_PKG" MyLittleUI.dll MyLittleUI.dll
thunderstore "$EXTRASLOTS_PKG" ExtraSlots.dll ExtraSlots.dll
thunderstore "$ADVENTUREBACKPACKS_PKG" AdventureBackpacks.dll AdventureBackpacks.dll

# Quartermaster is pinned in the manifest (Hexium carries the version the manifest requires).
QM_VERSION="$(dep 'Vassteel-Quartermaster-')"; QM_VERSION="${QM_VERSION##*-}"
hexium Vassteel Quartermaster "$QM_VERSION" Quartermaster.dll

# Hexium-only mods, pinned to the builds the audit was written against.
hexium Azumatt AAA_Crafting "$AAA_VERSION" AzuAntiArthriticCrafting.dll
hexium Azumatt AAABuildMenu "$AAABUILDMENU_VERSION" AAABuildMenu.dll
hexium Azumatt WardIsLove "$WARDISLOVE_VERSION" WardIsLove.dll

# Integrations that must actually move items or filter access - audited like the rest.
thunderstore "$EPICLOOT_PKG" EpicLoot.dll EpicLoot.dll
thunderstore "$PLANBUILD_PKG" PlanBuild.dll PlanBuild.dll
thunderstore "$WIZARDRY_PKG" Wizardry.dll Wizardry.dll
thunderstore "$DROPMORELOOT_PKG" DropMoreLoot.dll DropMoreLoot.dll

# ---- 4. Fail with the missing path, not a wall of MSBuild reference errors --------------------
required=(
  refs/game/valheim_Data/Managed/assembly_valheim.dll
  refs/game/valheim_Data/Managed/UnityEngine.dll
  refs/game/valheim_Data/Managed/UnityEngine.CoreModule.dll
  refs/game/valheim_Data/Managed/UnityEngine.UI.dll
  refs/game/valheim_Data/Managed/Unity.TextMeshPro.dll
  refs/game/valheim_Data/Managed/assembly_utils.dll
  refs/game/BepInEx/core/BepInEx.dll
  refs/game/BepInEx/core/0Harmony.dll
  refs/mods/Quartermaster.dll
  refs/mods/AzuAntiArthriticCrafting.dll
  refs/mods/MyLittleUI.dll
  refs/mods/ExtraSlots.dll
  refs/mods/AdventureBackpacks.dll
  refs/mods/AAABuildMenu.dll
  refs/mods/EpicLoot.dll
  refs/mods/PlanBuild.dll
  refs/mods/WardIsLove.dll
  refs/mods/Wizardry.dll
  refs/mods/DropMoreLoot.dll
)
for file in "${required[@]}"; do
  [ -f "$file" ] || { echo "missing reference: $file" >&2; exit 1; }
done

echo "Reference assemblies ready:"
du -sh refs/game refs/mods
