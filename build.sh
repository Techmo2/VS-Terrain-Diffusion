#!/usr/bin/env bash
# Builds the mod and packs it into dist/vsterraindiffusion_<version>.zip
#
# Set VINTAGE_STORY to your Vintage Story install if it is not at /opt/vintagestory.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$root/src/VSTerrainDiffusion"
configuration="${1:-Release}"

version="$(python3 -c "import json;print(json.load(open('$project/modinfo.json'))['version'])")"
output="$project/bin/$configuration"
dist="$root/dist"
archive="$dist/vsterraindiffusion_$version.zip"

echo "Building $configuration..."
dotnet build "$project" -c "$configuration" -v minimal -nologo

echo "Packing $archive"
mkdir -p "$dist"
rm -f "$archive"

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

cp "$output/VSTerrainDiffusion.dll" "$staging/"
cp "$output/Microsoft.ML.OnnxRuntime.dll" "$staging/"
cp "$output/System.Numerics.Tensors.dll" "$staging/"
cp "$project/modinfo.json" "$staging/"
[ -f "$project/modicon.png" ] && cp "$project/modicon.png" "$staging/"
cp -r "$project/assets" "$staging/"

(cd "$staging" && zip -qr "$archive" .)

echo "Done: $archive"
unzip -l "$archive"
