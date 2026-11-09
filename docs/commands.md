# Commands

`/terraindiffusion` or `/tdiff`. Requires the `controlserver` privilege.

| Subcommand              | Shows or does |
| ----------------------- | ------------- |
| `status`                | Device, world scaling, tiles generated, and where tile time went, stage by stage. |
| `gpulimit [percent]`    | The GPU limit and time given up to it. With a percentage, sets it and saves it to the world. |
| `here`                  | Elevation, slope, bioclimate, cover and latitude diagnostics where you stand. |
| `season <x> <z>`        | The same as `/tdiff here` at a position, plus the year's temperature and rainfall. Works from the console. |
| `climate [x z]`         | What the [climate API](other-mods.md#climate-api) returns, including the Köppen class. |
| `column <x> <z>`        | What was generated in a column, next to what the model said. |
| `tiles [clear]`         | The [tile database](servers.md#tile-database): file, tiles stored, size, reads and writes. `clear` empties it. |
| `pregen <radius> [x z]` | Stores every tile within `radius` blocks of `x z` (spawn by default). See [Pregenerating](servers.md#pregenerating). |
| `pregen [stop]`         | Progress of the running pregeneration, or stops it after the current tile. |
| `map`                   | The debug map's address. |

`here` and `season` also show these.

- **Latitude and hemisphere**, with the season the calendar reports there.
- **Sea-level temperature**, with altitude taken out, and the local lapse rate.
- **Band temperature and precipitation.** What the latitude band asked for, and how far this column
  is from it. Single columns scatter several degrees. Consistent drift across many columns is a bug.

## Debug map

Set `DebugMapPort` in the [mod config](configuration.md) to serve a live, read-only map of what the
model produces. It listens on loopback by default. `DebugMapBindAddress: "0.0.0.0"` publishes your
world's terrain to the network. Drag to pan, scroll to zoom, hover for every layer at a column.

| Group               | Layers |
| ------------------- | ------ |
| Coarse model input  | Requested elevation, temperature, precipitation, their seasonality and river basin conditioning |
| Coarse model output | Elevation, temperature, precipitation and their seasonality |
| Full resolution     | Elevation, slope, temperature, precipitation, seasonality, Köppen class, diurnal range, rainfall byte, forest and shrub cover |
| Vanilla             | Surface height as built, ocean map, forest map, shrub map |

The map keeps its own history of `DebugMapHistoryTiles` tiles at ~28 KB each.
