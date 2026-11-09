# Servers

Paths below are relative to the data folder. That is `~/.config/VintagestoryData` on Linux,
`%AppData%\VintagestoryData` on Windows, or the server's `--dataPath`.

## Tile database

Every finished terrain tile is written to `TerrainDiffusionTiles/<world id>.db`. A tile missing from
memory is read from there before the model is run. That covers chunks, other mods' height queries,
the translocator search and the debug map. Stored tiles are identical to freshly generated ones.

|                 | 256-block tile (GPU) |
| --------------- | -------------------- |
| On disk         | ~510 KB              |
| Read            | 5–10 ms              |
| Generate        | 150 ms – 3 s         |

The database is emptied on load when any of these change, and the log names which one:

- this mod's terrain version, or the models
- the seed, this mod's world settings, or vanilla's climate, landcover and ocean scale settings
- `WorldGen` in the mod config
- Rivers' version or config
- the ocean map near the world centre

Device, precision, GPU limit and other speed-only settings keep the tiles. A world keeps the tile
size its database holds. If you change a mod that reshapes the ocean map far from the centre, run
`/tdiff tiles clear`. Deleting a world doesn't delete its database.

## Pregenerating

A server without a fast GPU can serve terrain generated on another machine.
`/tdiff pregen <radius> [x z]` stores every tile needed for chunks within `radius` blocks. It reports
progress every 10 s and resumes after a restart.

Both machines must have the same mod version, the same worldgen mods and configs (copying the
server's `Mods` and `ModConfig` is simplest), and the same `WorldGen` section. Device, precision and
tile size may differ.

### New world

1. On the GPU machine, create the world in single player with the server's settings.
2. Run `/tdiff pregen <radius>` and wait for `Pregen finished`. Note the counts from `/tdiff tiles`.
3. Quit the world.
4. Get the world ID with `/info ident`. Copy `Saves/<world>.vcdbs` and
   `TerrainDiffusionTiles/<world id>.db` to the same folders on the server. Point
   `WorldConfig.SaveFileLocation` in `serverconfig.json` at the save.
5. Continue with [On the server](#on-the-server).

### Existing world

Players can keep playing during steps 1–4.

1. On the server, run `/info ident`, then `/genbackup`. The backup is written to `Backups/`.
2. Copy the backup into `Saves/` on the GPU machine and open it.
3. If needed, run `/worldconfig terraindiffusionTileDatabase true`. If the world names a device, also
   run `/worldconfig terraindiffusionInferenceDevice auto`. Reopen the world after either.
4. Run `/tdiff pregen <radius> [x z]`, wait for `Pregen finished`, note the `/tdiff tiles` counts,
   and quit.
5. On the server, run `/worldconfig terraindiffusionTileDatabase true` if it's off, then stop the
   server.
6. Copy `TerrainDiffusionTiles/<world id>.db` over the server's copy and start the server.

### On the server

1. In `ModConfig/vsterraindiffusion.json`, set `TerrainSamplerHeight` to `"stored"` and
   `WorldGen.TranslocatorStoredTerrainOnly` to `true`.
2. Check that `/tdiff tiles`, or the `Tile database:` line in `Logs/server-main.log`, shows the counts
   you noted. `Emptied the tile database ... generated under different <inputs>` means those inputs
   differ between the machines.

Inside the pregenerated area, chunks never run the model. Translocator searches never do, anywhere.
If the game didn't close cleanly, open and quit the world once so the `.db-wal` file is folded in
before copying.
