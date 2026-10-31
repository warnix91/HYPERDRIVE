#!/bin/zsh
# Builds Hyperdrive (Release) and makes the player archive dist/Hyperdrive-<version>.zip:
#   GameData/Hyperdrive (DLLs, config, localization, shaders, version file) + README.md + CHANGELOG.md (+ LICENSE if present).
# Never includes PluginData (player settings, caches, learned data) nor anything from _reference/.
# usage: tools/package.sh
set -e
cd "$(dirname "$0")/.."
dotnet build Hyperdrive.sln -c Release -p:DeployToKSP=false | grep -E " error |Warn|->" || true
VERSION=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' Directory.Build.props | head -1)
[ -z "$VERSION" ] && { echo "version not found"; exit 1; }
STAGE=$(mktemp -d)
mkdir -p "$STAGE/GameData"
rsync -a --exclude PluginData --exclude '.DS_Store' GameData/Hyperdrive "$STAGE/GameData/"
cp README.md CHANGELOG.md "$STAGE/"
[ -f LICENSE ] && cp LICENSE "$STAGE/"
mkdir -p dist
OUT="dist/Hyperdrive-$VERSION.zip"
rm -f "$OUT"
(cd "$STAGE" && zip -qr "$OLDPWD/$OUT" .)
rm -rf "$STAGE"
echo "$OUT ($(du -h "$OUT" | cut -f1))"
[ -f LICENSE ] || echo "warning: no LICENSE file yet (to choose before any publication)"
