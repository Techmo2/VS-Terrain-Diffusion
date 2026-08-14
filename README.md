# VS Terrain Diffusion — a Vintage Story mod

Generates Vintage Story worlds with [Terrain Diffusion](https://github.com/xandergos/terrain-diffusion),
a neural model trained on real Earth topography and climate (SIGGRAPH 2026). Instead of stacking
simplex octaves, the world comes out of a diffusion pipeline that produces continents, drainage
networks, fjords, plateaus and mountain ranges with the structure of real terrain — and, alongside
the heightmap, a real climatology to go with it.

The mod uses all of it. Terrain, temperature, rainfall, forests, the surface you walk on and the
seasons all come from the same model, so the landscape and the life on it agree with each other.

**Contents** — [Installing](#installing) · [Creating a world](#creating-a-world) ·
[What the mod changes](#what-the-mod-changes) · [How it works](#how-it-works) ·
[Commands](#commands) · [Configuration](#configuration) · [Building](#building)

## Installing

Drop the release zip in your `Mods` folder. The models download themselves on first launch.

- Vintage Story 1.22 (targets .NET 10, same as the game)
- **~2.2 GB of disk** for the model files, fetched once
- **~3 GB of RAM** while the models are resident
- A GPU is strongly recommended. CPU inference works but is roughly 10-20x slower.

| Platform          | Device used automatically | Notes                                            |
| ----------------- | ------------------------- | ------------------------------------------------ |
| Linux + NVIDIA    | CUDA                      | Needs a system CUDA 12 or 13 runtime and cuDNN 9  |
| Windows           | DirectML                  | Any modern GPU; no extra install                  |
| macOS (Apple)     | CoreML                    | No extra install                                  |
| Anything else     | CPU                       | Works, but slow                                   |

The matching ONNX Runtime native library is downloaded on first use too (a few MB for
CPU/DirectML, ~300 MB for CUDA). Nothing has to be installed by hand.

The mod is server-side. Clients may install it as well, which keeps their weather display in step
with what the server is simulating, but it is not required and vanilla clients can join normally.

The first world you create sits on the loading screen until all of that has been fetched, which on
a slow connection is a long time. The loading screen says when each download starts and when they
are finished, so you can tell the wait apart from a hang. `Logs/server-main.log` has the detail.

## Creating a world

| Setting                  | Default | What it does                                                         |
| ------------------------ | ------- | -------------------------------------------------------------------- |
| Terrain diffusion        | on      | Turn off to fall back to vanilla terrain, keeping the mod installed.  |
| Diffusion resolution     | 15 m    | Real-world metres per block, horizontally *and* vertically.           |
| Vertical exaggeration    | 1x      | Multiplies terrain height. 1x is true scale.                          |
| Diffusion climate        | on      | Whether the model drives climate as well as terrain.                  |
| **World height**         | —       | **Set this to 1024.** Vanilla's default is far too short for real mountains. |
| Starting climate         | temperate | Honoured by moving the spawn, not the climate. See below.           |

A dedicated server has no world-creation screen, so the first four are also reachable from the mod
config as `worldGen.climateMode`, `worldGen.scaleOverride` and
`worldGen.verticalExaggerationOverride`.

### Starting climate

Vanilla honours this setting by sliding its climate map until the band you picked covers the map
centre. That is not available here — the model predicts one particular world rather than a climate
field that can be shifted about — so the mod moves you instead: it surveys outward from the map
centre for land whose temperature is in the band, then checks the likeliest spots at full
resolution and puts the spawn on a column that really is in range.

The five bands mean what they do in an unmodded world: hot 28–32 °C, warm 19–23 °C, temperate
6–14 °C, cool −5 to 1 °C, icy −15 to −10 °C, measured as annual mean temperature.

- Cold bands are usually found on high ground rather than far north, so "icy" is often a nearby
  mountain rather than a long trek.
- The search prefers to travel east or west. Distance along Z is what sets latitude in Vintage
  Story, and past the world's polar distance that buys midnight sun and polar night; distance along
  X costs nothing.
- It stops at the first matching land it finds, so most worlds spawn within a few thousand blocks
  and the search takes under a second. A band that is genuinely far away — usually "hot" — can take
  a few seconds on a GPU and rather longer on CPU inference.
- If the seed has no such land within range, the server log says so and you spawn at the closest
  temperature it found.

Set `worldGen.startingClimateSearch` to false to spawn on the nearest land whatever its climate.

## What the mod changes

- **Terrain pass** — vanilla `GenTerra`'s chunk handler is swapped for one that fills columns from
  the diffusion heightmap.
- **Climate map** — temperature and rainfall from the model, pre-compensated for the altitude
  corrections the game applies on read. The geologic activity byte is still vanilla's.
- **Forest and shrub maps** — replaced with cover derived from the model's moisture and growing
  season.
- **Ocean map** — fed from the model's elevation, so systems that avoid the sea agree with the
  coastline that actually got generated.
- **Surface pass** — after vanilla's block layers, two things it cannot know about are fixed up:
  slopes too steep to hold soil are scoured back to bare rock (vanilla upholsters cliff faces in
  eight blocks of dirt), and ground whose warmest month never rises above freezing is capped with
  glacier ice.
- **Seasons** — temperature and rainfall swing through the year on the model's seasonality instead
  of latitude.
- **Spawn** — the model decides where continents are, so the world centre is as likely to be open
  ocean as land. The spawn is searched for and moved to solid ground, in the world's chosen
  starting climate.
- **Surface block layer altitudes** — only when terrain is vertically exaggerated. Vanilla's bands
  are fractions of world height (bare mountain gravel above 0.66 of it) and assume a block is about
  a metre; at true scale that already lines up, so nothing is touched.

Everything else — rock strata, ores, caves, rivers, ponds, ruins, traders, temporal stability — is
vanilla, running unchanged on top.

## How it works

### Scale, and why the world needs to be tall

By default a block is exactly as tall as it is wide, the same geometry the Terrain Diffusion
Minecraft mod uses. At the default resolution one block is 15 m in every direction, so a 2 000 m
massif is 133 blocks of climbing spread over however many kilometres the model gave it, and every
slope has the grade it would have in the real world.

The catch is that real mountains need real room. The model's land runs to about 3 000 m at the 95th
percentile and 5 000 m at the extreme, which at 15 m per block is 200 and 333 blocks *above sea
level*:

| World height | Blocks above sea | Terrain held at true scale |
| ------------ | ---------------- | -------------------------- |
| 256          | 145              | up to ~1 900 m             |
| 512          | 289              | up to ~3 700 m             |
| 1024         | 578              | up to ~7 400 m             |

Past that the mapping bends towards the ceiling rather than clipping. The curve is `u / (1 + u)`,
which has slope 1 where it meets the linear part so there is no crease, and never quite flattens,
so summits round off instead of shearing into mesas. It still costs you the faithfulness of the
highest ground, which is why a taller world is better.

If a tall world is not an option, set `worldGen.heightMode` to `"auto"`. That surveys the region
around spawn once, measures how tall its peaks actually get, and stretches the metre-to-block
mapping so they reach near the ceiling of whatever world you have. The landscape then uses the full
height available at the cost of exaggerated relief — a gentle region might come out at 4x. The
measurement depends only on the seed and is stored in the save.

Resolution is also the main performance dial, because a coarser one covers more blocks per model
pixel. At 30 m per block you cross a continent in an afternoon; at 5 m per block the same mountain
is four kilometres of walking, and only a very tall world keeps it true to scale.

### Climate

The model predicts four WorldClim bioclimatic variables everywhere it predicts elevation:

| Variable | What it is                                             |
| -------- | ------------------------------------------------------ |
| BIO1     | annual mean temperature, °C                             |
| BIO4     | temperature seasonality — the spread of monthly means   |
| BIO12    | annual precipitation, mm                                |
| BIO15    | precipitation seasonality — how unevenly it falls       |

These are a real climatology, with continents, maritime coasts, continental interiors, rain shadows
and altitude already in them. There is no latitude gradient layered on top: heading north does not
get colder, because *where the model put the cold places* is what gets colder. `startingClimate`
and `polarEquatorDistance` therefore do nothing; `globalTemperature` and `globalPrecipitation` still
scale everything.

### From bioclimate to what the game reads

None of those four is directly what Vintage Story wants, and none of them is directly what a plant
wants either. 800 mm of rain is generous in Lapland and semi-arid in the Sahel. A mean of 5 °C is a
pleasant montane climate if it holds all year and a brutal one if it swings forty degrees. So the
mod derives the quantities climatologists use — potential evapotranspiration, an aridity index, a
growing season — and keys everything off those. The formulas are ported from the reference
implementation's own biome classifier, so a place that reads as savanna there reads as savanna here.

**Rainfall** is a quantile map, not a physical conversion. Vanilla draws its 0-255 rainfall byte
*uniformly*, and every threshold that reads it — the level above which ground stops being bare
gravel, the fertility curve that decides whether soil forms, the rainfall bands on every tree and
block patch — was tuned against that uniform spread. Feeding a physical quantity straight in makes
the whole world read as desert. So the model's tree moisture (aridity, discounted for a dry season)
goes through its own distribution, which comes out uniform, which is what the game expects. Set
`worldGen.rainfallBasis` to `"precipitation"` to map raw millimetres instead.

**Forest and shrub cover** come from the same moisture, scaled by the growing season and cut to zero
on ground too steep to hold soil. This replaces vanilla's forest map outright, and it is worth
knowing why: vanilla's `MapLayerWobbledForest` computes `128 - rain * temp / 65025`, and that
product never exceeds 1, so forest density in an unmodified world is pure noise with no
relationship to climate at all. Woodland in the foothills, scrub on the dry plateau and nothing
above the treeline are all new behaviour.

**Temperature** is written pre-compensated. The game re-applies its own lapse rate whenever it reads
the climate map, and the model has already accounted for altitude, so the stored value is chosen to
make the game's answer *at the surface* the one the model predicted. Without that, every mountain
would come out twice as cold as it should be.

### Seasons

Vanilla decides how hard a place swings through the year from latitude alone: `ModTemperature`
takes an amplitude of `|latitude| * 65` degrees, so the equator has no seasons and the poles have
enormous ones, and nothing else about the location matters.

Here it comes from BIO4. A maritime coast and a continental interior at the same annual mean get
completely different years — the coast stays mild, the interior freezes solid every winter and
bakes every summer. Precipitation seasonality does the same for rain, so a monsoon climate gets a
real wet and dry season instead of drizzling evenly all year.

The two seasonality channels have nowhere to live in Vintage Story's packed climate integer, whose
interpolator only touches the low three bytes, so they are stored as map region mod data. That is
saved with the region and, unlike the region's other maps, sent to clients — which is why a client
running the mod swings its weather in step with the server, and a vanilla client falls back to
vanilla's seasons for display.

`/tdiff season <x> <z>` walks a year at a position and prints what it does.

## Commands

`/terraindiffusion`, or `/tdiff`. Requires the `controlserver` privilege.

| Subcommand           | What it shows                                                          |
| -------------------- | ---------------------------------------------------------------------- |
| `status`             | Device, world scaling, tiles generated and average tile time.           |
| `here`               | The model's elevation, slope, full bioclimate and derived cover at you. |
| `season <x> <z>`     | The seasonal temperature and rainfall cycle at a position.              |
| `column <x> <z>`     | What actually got generated in a column, next to what the model said.   |

## Configuration

`ModConfig/vsterraindiffusion.json`, written on first start. [CONFIG.md](CONFIG.md) is the whole
default file with a comment on every field and the range each one is clamped to; the tables below
are the short version.

### Inference

Machine settings. Safe to change at any time.

| Key                          | Default | Meaning                                                |
| ---------------------------- | ------- | ------------------------------------------------------ |
| `inferenceDevice`            | `auto`  | `auto`, `cpu`, `cuda`, `directml`, `coreml`.            |
| `offloadModels`              | true    | One model on the GPU at a time. Costs a little time per stage switch, saves ~1 GB of VRAM. |
| `validateModelHashes`        | true    | Verify SHA-256 of existing model files on startup.      |
| `downloadRuntime`            | true    | Fetch the ONNX Runtime native library automatically.    |
| `tileCacheMegabytes`         | 256     | Decoded tensor windows per pipeline stage.              |
| `terrainTileCacheMegabytes`  | 256     | Finished terrain tiles. Raise if you see thrash warnings. |
| `terrainTileSizeBlocks`      | 256     | Blocks generated per model invocation. Multiple of 32.  |
| `verboseInference`           | false   | Log every model window.                                 |

### World generation

These decide what the world looks like. Changing one after a world has been explored will make new
chunks disagree with old ones.

**Height and scale**

| Key                              | Default       | Meaning                                                     |
| -------------------------------- | ------------- | ----------------------------------------------------------- |
| `heightMode`                     | `"isotropic"` | `"isotropic"`, `"manual"` or `"auto"`.                        |
| `metersPerBlockVertical`         | 0             | manual: metres of elevation per block.                        |
| `linearKneeFraction`             | 0.85          | Fraction of the height mapped perfectly linearly.             |
| `oceanDepthFraction`             | 0.9           | How much of the space below sea level the abyss reaches.      |
| `slopeDetailStrength`            | 1             | Perlin roughness added to sloped ground.                      |
| `scaleOverride`                  | 0             | Overrides the resolution. Values above 6 are only settable here. |
| `verticalExaggerationOverride`   | 0             | Overrides the height multiplier.                              |

**Height calibration** (`heightMode: "auto"` only)

| Key                              | Default       | Meaning                                                     |
| -------------------------------- | ------------- | ----------------------------------------------------------- |
| `targetPeakFillFraction`         | 0.92          | How much of the available height the region's peaks fill.     |
| `peakQuantile`                   | 0.995         | Which elevation quantile counts as a peak.                    |
| `calibrationRadiusBlocks`        | 4096          | Half-width of the surveyed area.                              |
| `calibrationProbes`              | 8             | Full-detail probes on the tallest surveyed cells.             |
| `reliefFactor`                   | 1.6           | Assumed peak-to-survey ratio when probing is off or fails.    |
| `minAutoExaggeration` / `maxAutoExaggeration` | 1 / 20 | Bounds on the vertical gain calibration may choose.  |

**Climate and vegetation**

| Key                              | Default       | Meaning                                                     |
| -------------------------------- | ------------- | ----------------------------------------------------------- |
| `climateMode`                    | `""`          | `"full"` or `"off"` to override the world setting.            |
| `rainfallBasis`                  | `"moisture"`  | `"moisture"` (aridity) or `"precipitation"` (raw mm).         |
| `moistureMedian` / `moistureSpread` | 0.62 / 1.0 | Log-normal fit to the model's tree moisture over land.        |
| `rainfallMedianMm` / `rainfallSpread` | 540 / 0.8 | The same for raw precipitation.                              |
| `rainfallBias`                   | 0.05          | Added to rainfall. Raise for a lusher world; see below.       |
| `temperatureOffsetC`             | 0             | Degrees added to every model temperature.                     |
| `forestDensityMultiplier`        | 1             | Scales forest cover.                                          |
| `shrubDensityMultiplier`         | 1             | Scales shrub cover.                                           |

**Seasons and surface**

| Key                              | Default       | Meaning                                                     |
| -------------------------------- | ------------- | ----------------------------------------------------------- |
| `seasonalTemperature`            | true          | Swing temperature on the model's seasonality.                 |
| `seasonalTemperatureStrength`    | 1             | Multiplies that swing. 0 gives a world with no seasons.       |
| `seasonalPrecipitation`          | true          | Swing rainfall on the model's precipitation seasonality.      |
| `seasonalPrecipitationStrength`  | 1             | Multiplies the wet/dry contrast.                              |
| `seasonHemispheres`              | false         | Opposite seasons north and south of the map's middle.         |
| `bareSlopeRock`                  | true          | Leave slopes too steep for soil as bare rock.                 |
| `glacierIce`                     | true          | Cap permanently frozen ground with glacier ice.               |
| `rescaleBlockLayerAltitudes`     | true          | Stretch vanilla's altitude bands. No effect at true scale.    |

**Spawn**

| Key                                  | Default | Meaning                                                    |
| ------------------------------------ | ------- | ----------------------------------------------------------- |
| `startingClimateSearch`              | true    | Put the spawn in the world's chosen starting climate.         |
| `startingClimateSearchRadiusBlocks`  | 65536   | How far to look before settling for the closest temperature.  |
| `startingClimateNorthSouthCost`      | 2       | How much more reluctantly the search moves along Z than X.    |

`rainfallBias` exists because the climate map cancels Vintage Story's own "higher ground is wetter"
bonus — the model already does orography properly — while vanilla's biome thresholds were tuned with
that bonus present. The default puts its average back.

## Building

```bash
./build.sh
```

Needs the .NET 10 SDK and a Vintage Story install at `/opt/vintagestory` (override with
`VINTAGE_STORY`). Produces `dist/vsterraindiffusion_<version>.zip`.

## Credits

- Terrain Diffusion model, the reference implementation and the original Minecraft mod:
  [xandergos](https://github.com/xandergos)
- Vintage Story integration: this mod
