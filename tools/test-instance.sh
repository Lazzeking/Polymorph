#!/usr/bin/env bash
# Starts Vintage Story with its own data folder and only Polymorph, the double-recipe test mod and the mods you name,
# to check a problem report without touching your normal mods, settings or saves.
#
#   tools/test-instance.sh <instance> [mod ...] [--fresh] [--no-polymorph]
#
#   instance  name of the test folder, for example insanemonkey
#   mod       a mod id (overhaullib), optionally with a version (ardynleveling@1.0.17), or a path to a mod zip or folder.
#             A mod id comes from your normal Mods folder, or is downloaded from the Mod DB when it is not there:
#             the version you asked for, else the newest release for your game version. Dependencies too.
#   --fresh   delete the instance's world first
#   --no-polymorph  leave Polymorph and the test mod out, to see whether a problem happens without them
#
# Example: tools/test-instance.sh insanemonkey overhaullib overhaulliblegacycompat ardynleveling@1.0.17
#
# The instance lives inside the Flatpak's own folder, because the Flatpak cannot read anywhere else. Its logs are in
# <instance>/Logs. The first start copies your clientsettings.json so you stay logged in.

set -euo pipefail

APP_ID=at.vintagestory.VintageStory
APP_DATA="$HOME/.var/app/$APP_ID"
MAIN_DATA="$APP_DATA/config/VintagestoryData"
WORLD=polymorph-test
# The survival preset, the same one the official mod template opens its test world with. Survival has the crafting grid.
PLAY_STYLE=preset-surviveandbuild

if [[ $# -lt 1 || $1 == -* ]]; then
    sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
fi

NAME=$1
shift
INSTANCE="$APP_DATA/test-instances/$NAME"
FRESH=false
POLYMORPH=true
MODS=()
for arg in "$@"; do
    if [[ $arg == --fresh ]]; then
        FRESH=true
    elif [[ $arg == --no-polymorph ]]; then
        POLYMORPH=false
    else
        MODS+=("$arg")
    fi
done

# Work out every mod before changing anything, so a typo or a missing mod stops the run. Named mods and their
# dependencies come from your normal Mods folder, or are downloaded from the Mod DB into test-instances/.moddb.
# Captured first: under set -e a failing command substitution stops the script, a process substitution would not.
SOURCES=()
if [[ ${#MODS[@]} -gt 0 ]]; then
    resolved=$(python3 "$(dirname "$(realpath "$0")")/resolve_mods.py" "$MAIN_DATA/Mods" "$APP_DATA/test-instances/.moddb" "${MODS[@]}")
    mapfile -t SOURCES <<< "$resolved"
fi

mkdir -p "$INSTANCE"
rm -rf "$INSTANCE/Mods"
mkdir -p "$INSTANCE/Mods"
if $FRESH; then
    rm -rf "$INSTANCE/Saves"
fi

if [[ ! -e $INSTANCE/clientsettings.json && -e $MAIN_DATA/clientsettings.json ]]; then
    cp "$MAIN_DATA/clientsettings.json" "$INSTANCE/"
fi

# The settings list the folders mods load from, and the copied ones point at your normal Mods folder. Keep the game's
# own "Mods" folder, which holds the vanilla mods, and swap the rest for this instance's Mods folder.
if [[ -e $INSTANCE/clientsettings.json ]]; then
    python3 - "$INSTANCE/clientsettings.json" "$INSTANCE/Mods" <<'EOF'
import json
import sys

path, mods = sys.argv[1], sys.argv[2]
with open(path) as file:
    settings = json.load(file)
settings.setdefault("stringListSettings", {})["modPaths"] = ["Mods", mods]
with open(path, "w") as file:
    json.dump(settings, file, indent=2)
EOF
fi

# The build's own packaging step installs Polymorph and the test mod into the folder it is given.
if $POLYMORPH; then
    PROJECT="$(dirname "$(realpath "$0")")/.."
    dotnet build "$PROJECT/Polymorph.csproj" -c Debug -p:ModsFolder="$INSTANCE/Mods" -nologo -v:q
fi

for source in "${SOURCES[@]}"; do
    cp -r "$source" "$INSTANCE/Mods/"
done

echo "Mods in $NAME:"
ls -1 "$INSTANCE/Mods" | sed 's/^/  /'
echo "Logs: $INSTANCE/Logs"

flatpak run "$APP_ID" --dataPath "$INSTANCE" --openWorld "$WORLD" --playStyle "$PLAY_STYLE" --tracelog
