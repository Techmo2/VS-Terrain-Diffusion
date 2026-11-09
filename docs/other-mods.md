# Other mods

## Map mods

Mods that only replace a map (for example Continental World's ocean map) need nothing special. The
model is conditioned on whatever map is installed.

## sneeze's Rivers

Rivers keeps its own rivers, and this mod adds only its lakes. Rivers routes from the coast the model
draws, not from the ocean map. It caches each world's zones under `RiverCache/`, so a world keeps the
coast its rivers were first routed from.

## Algernon's Terrain Sampler

Other mods' height queries are answered from the model. `TerrainSamplerHeight` in the
[mod config](configuration.md) picks the source.

| Mode     | Speed      | Source |
| -------- | ---------- | ------ |
| `full`   | Slowest    | Full-resolution decoder output |
| `coarse` | Fastest    | Coarse model output |
| `stored` | Compromise | Full resolution if the tile is in the [tile database](servers.md#tile-database), coarse otherwise |

## Algernon's Watersheds

Watersheds replaces terrain generation, so this mod gives it the model's heights instead of generating
terrain itself. Streams come from Watersheds, and this mod's lakes and rivers are turned off. The first
load of a new world takes a few minutes while Watersheds surveys the area around spawn.

## Climate API

On the server, `api.ModLoader.GetModSystem<TerrainDiffusionModSystem>().Climate` answers for any block
column, generating its tile if needed. It is null on worlds the mod isn't generating. Call it from
world generation or a background thread.

| Member | Returns |
| ------ | ------- |
| `TryGetClimate(x, z, out ClimateRecord)` | Annual temperature, BIO4, rainfall and BIO15 as the game uses them, plus ground elevation, the seasonal-cycle channels and the diurnal range. |
| `ClimateRecord.Months()` | Twelve monthly temperatures and rainfalls. Month 0 is the first after midsummer. |
| `ClimateRecord.KoppenClass` | Köppen–Geiger class, an index into `Koppen.Classes`, or `Koppen.Water` at sea. |
| `ClimateRecord.KoppenMemberships(softness)` | Class weights summing to 1, blended across boundaries. |
| `TryGetCoarseChannels(x, z, span)` | The coarse model's raw output (`TerrainClimate.ChannelNames`). |

On `annual` worlds, `HasSeasonalCycle` is false, the cycle is approximated, and the diurnal range is
NaN. `/tdiff climate [x z]` prints what the API returns.
