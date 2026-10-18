#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN_PROJECT="$ROOT_DIR/src/Jellyfin.Plugin.Edl/Jellyfin.Plugin.Edl.csproj"
BUILD_OUT="$ROOT_DIR/src/Jellyfin.Plugin.Edl/bin/Release/net10.0"
STAGE_DIR="$ROOT_DIR/artifacts/plugins/Jellyfin.Plugin.Edl"
PKG_DIR="$ROOT_DIR/artifacts/packages"
PKG_FILE="$PKG_DIR/Jellyfin.Plugin.Edl.zip"

mkdir -p "$STAGE_DIR" "$PKG_DIR"

if command -v dotnet >/dev/null 2>&1; then
  dotnet build "$PLUGIN_PROJECT" -c Release
else
  docker run --rm -v "$ROOT_DIR:/workspace" -w /workspace \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build ./src/Jellyfin.Plugin.Edl/Jellyfin.Plugin.Edl.csproj -c Release
fi

rm -f "$STAGE_DIR"/Jellyfin.Plugin.Edl.dll \
  "$STAGE_DIR"/Jellyfin.Plugin.Edl.deps.json \
  "$STAGE_DIR"/Jellyfin.Plugin.Edl.pdb \
  "$STAGE_DIR"/Jellyfin.Plugin.Edl.xml
cp "$BUILD_OUT"/Jellyfin.Plugin.Edl.dll "$STAGE_DIR"/
cp "$BUILD_OUT"/Jellyfin.Plugin.Edl.deps.json "$STAGE_DIR"/
cp "$BUILD_OUT"/Jellyfin.Plugin.Edl.pdb "$STAGE_DIR"/
cp "$BUILD_OUT"/Jellyfin.Plugin.Edl.xml "$STAGE_DIR"/

rm -f "$PKG_FILE"
(
  cd "$STAGE_DIR"
  zip -r "$PKG_FILE" .
)

echo "Created package: $PKG_FILE"
