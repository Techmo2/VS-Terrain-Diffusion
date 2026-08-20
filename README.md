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
| Land cover               | 97.5%   | Honoured — it decides where the sea goes. See below.                  |
| Ocean scale              | 500%    | Honoured — it decides how big the oceans are. See below.              |
| Global temperature       | normal  | Honoured — the model draws a world that cold or that hot. See below.  |
| Global precipitation     | normal  | Honoured — same, for rainfall.                                        |

A dedicated server has no world-creation screen, so the first four are also reachable from the mod
config as `worldGen.climateMode`, `worldGen.scaleOverride` and
`worldGen.verticalExaggerationOverride`.

### Land and sea

The world's ocean map decides where the coastline is, and the model decides what it looks like.
Before generating anything the mod reads Vintage Story's ocean map — the one "Land cover" and
"Ocean scale" configure — and feeds it to the model as conditioning, so a world set to 50% land
gets 50% land, with the shelf, the fjords and the mountains behind them drawn from real terrain.

It reads *whichever* ocean map is installed rather than vanilla's in particular, so a mod that
supplies its own — Continental World, for instance — is honoured on exactly the same terms, and
the map is left untouched afterwards for everything else that reads it.

Two things are worth knowing:

- **Conditioning is soft.** The model is steered, not clamped, so the coast it draws wanders around
  the one it was given rather than tracing it exactly, which is what gives it a natural shape
  instead of the ocean map's blobs. Measured against vanilla's own map over 131 000 blocks, how
  much of the world is sea comes out within about four points of what the map asked for, always
  very slightly wetter; column by column, 88% of the world is on the side of the water it was
  asked to be, and the disagreement is nearly all within one cell of a coastline.
- **There is a resolution floor.** One conditioning pixel spans 512 blocks at the default diffusion
  resolution, so a sea much smaller than that cannot be expressed and the model will fill it in as
  land. Low "Ocean scale" settings lose their smallest islands and lakes for this reason — though
  they hold up better than that suggests, still placing 91% of columns correctly at 100% scale,
  where an ocean cell is only 1024 blocks across.

Vanilla's defaults — 97.5% land, 500% ocean scale — give a nearly unbroken continent, which is a
fine world but not what most people install this mod for. Around 40–60% land is where real
coastlines start to appear.

Set `worldGen.oceanMap` to `"output"` for the reverse arrangement: the model invents its own
continents from real-world terrain, ignoring both world settings, and the ocean map is rewritten to
match it.

### A hotter, colder, wetter or drier world

"Global temperature" and "Global precipitation" are settings vanilla applies to the climate map it
draws at random: it generates its world and multiplies the numbers. This mod hands them to the
model instead, so that a world set to "Semi-Arid" is *drawn* semi-arid — the model puts the forests
where a drier world would have forests, and the soil, the snow line and the seasons follow, rather
than a temperate world having its rainfall divided by two on the way to the screen.

The two settings work differently underneath, because the quantities do. Rainfall runs from nothing
to six metres a year, so it is scaled outright: four times the rain is four times the rain. Mean
annual temperature occupies about forty degrees and has nothing above 30 °C anywhere on Earth, so
instead of scaling it the mod moves the world along the real distribution towards its hot or cold
end. What that cannot reach — the top two or three notches of the temperature setting ask for
climates that do not exist — is scaled onto the model's output afterwards, as vanilla does, and
lands in the same place vanilla does: the climate map saturates and the world reads 40 °C.

Measured on two seeds across the whole range of both settings, against what vanilla's arithmetic
would have produced for the same world:

- **Temperature** lands within 2 °C of the setting at every notch up to "Hot", which overshoots by
  about 5 °C for want of anywhere hotter to draw from. "Very hot" and "Scorching hot" saturate the
  game's climate scale, exactly as they do in an unmodded world.
- **Rainfall** lands within 10% in a region of ordinary wetness. Somewhere already very wet or very
  dry moves perhaps half as far as asked: the model will not draw the Sahara four times wetter, and
  vanilla's own rainfall byte saturates for much the same reason.

The far settings interact with "Starting climate", which is a band of real temperatures: turn the
world up to "Scorching hot" and there is no temperate land left anywhere for the spawn search to
find, so it will scan its whole radius, say so in the log, and put you on the coolest thing going —
usually a mountain top.

Set `worldGen.globalClimateStrength` to 0 for the old arrangement, where the model knows nothing
and both settings are applied to its output.

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
- **Global temperature and precipitation** — conditioning rather than post-processing, so the world
  is drawn at the climate asked for instead of being drawn temperate and rescaled.
- **Forest and shrub maps** — replaced with cover derived from the model's moisture and growing
  season.
- **Ocean map** — read, not written. It is what the terrain is conditioned on, so the world's land
  cover and ocean scale settings, or another mod's ocean map, decide where the sea goes.
- **Surface pass** — after vanilla's block layers, two things it cannot know about are fixed up:
  slopes too steep to hold soil are scoured back to bare rock (vanilla upholsters cliff faces in
  eight blocks of dirt), and ground whose warmest month never rises above freezing is capped with
  glacier ice.
- **Seasons** — temperature and rainfall swing through the year on the model's seasonality instead
  of latitude.
- **Spawn** — moved to solid ground in the world's chosen starting climate. Vanilla forces land at
  the map centre through the ocean map, and the terrain follows that, so on most worlds the search
  does not have to go far.
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
get colder, because *where the model put the cold places* is what gets colder.
`polarEquatorDistance` therefore does nothing, and `startingClimate` is honoured by moving the
player rather than the climate. `globalTemperature` and `globalPrecipitation` are conditioning: the
model draws the world at the climate they ask for.

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

**Coastlines**

| Key                              | Default   | Meaning                                                       |
| -------------------------------- | --------- | ------------------------------------------------------------- |
| `oceanMap`                       | `"input"` | `"input"` conditions the model on the world's ocean map; `"output"` lets the model invent the continents and rewrites the map to match. |
| `landmaskStrength`               | 1         | How completely the ocean map overrides the model's own sense of where land belongs. |
| `landmaskNoiseLevel`             | 0.1       | How much noise the model is told the mask carries. **Lower binds it more tightly**; the model's own value is 0.5. |

**Global climate**

| Key                              | Default | Meaning                                                         |
| -------------------------------- | ------- | ---------------------------------------------------------------- |
| `globalClimateStrength`          | 1       | How much of the world's global temperature and precipitation settings the model is conditioned on rather than having applied to its output. |
| `climateNoiseLevel`              | 0       | How much noise the model is told the shifted climate carries, on the same inverted scale as `landmaskNoiseLevel`. Zero uses the model's own. |

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
