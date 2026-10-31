#!/bin/zsh
# Builds GameData/Hyperdrive/Shaders/hyperdrive-<target>.bundle from unity/HyperdriveShaders with Unity 2019.4.18f1 (KSP's).
# usage: tools/build_shaders.sh [mac|windows|linux ...]   (linux needs Unity's Linux Build Support module)   env: UNITY_EDITOR (default: /Applications/*Unity-2019.4.18f1/Unity.app)
ROOT="${0:A:h:h}"
EDITOR="${UNITY_EDITOR:-$(ls -d /Applications/*2019.4.18f1*/Unity.app/Contents/MacOS/Unity 2>/dev/null | head -1)}"
[ -x "$EDITOR" ] || { echo "Unity 2019.4.18f1 not found: set UNITY_EDITOR"; exit 1; }
OUT="$ROOT/GameData/Hyperdrive/Shaders"; mkdir -p "$OUT"
for T in ${@:-mac}; do
  TMP="$ROOT/unity/HyperdriveShaders/Build/bundles-$T"; rm -rf "$TMP"
  HYPERDRIVE_BUNDLE_TARGET=$T HYPERDRIVE_BUNDLE_OUTPUT="$TMP" "$EDITOR" -batchmode -projectPath "$ROOT/unity/HyperdriveShaders" \
    -executeMethod BuildShaders.Build -logFile "$ROOT/unity/HyperdriveShaders/Logs/build-$T.log" -force-glcore
  if grep -q "HYPERDRIVE_BUNDLE_OK $T" "$ROOT/unity/HyperdriveShaders/Logs/build-$T.log"; then
    cp "$TMP/hyperdrive-$T.bundle" "$OUT/" && echo "OK $T: $(wc -c < "$OUT/hyperdrive-$T.bundle") bytes"
  else
    echo "FAILED $T:"; grep -E "HYPERDRIVE_BUNDLE_FAILED|error|Error" "$ROOT/unity/HyperdriveShaders/Logs/build-$T.log" | head -20
  fi
done
