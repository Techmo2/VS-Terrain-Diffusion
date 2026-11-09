#!/usr/bin/env bash
# Copies the water library (lakes, rivers, meanders) from the terrain-diffusion repo into
# src/TerrainDiffusion.Water. Keep the copy identical to the source: a change to the library can
# change the water, so it is part of the world (see terraindiffusionWaterVersion).
#
# Usage: scripts/sync-water.sh [path/to/terrain-diffusion/csharp]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source="${1:-$HOME/Desktop/terrain-diffusion/csharp}/TerrainDiffusion.Water"
target="$root/src/TerrainDiffusion.Water"

if [ ! -f "$source/TerrainDiffusion.Water.csproj" ]; then
    echo "No TerrainDiffusion.Water project at $source" >&2
    exit 1
fi

rsync -a --delete --exclude bin/ --exclude obj/ "$source/" "$target/"
echo "Synced $source -> $target"
