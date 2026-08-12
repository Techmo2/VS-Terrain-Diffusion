# VS Terrain Diffusion — a Vintage Story mod

Generates Vintage Story worlds with [Terrain Diffusion](https://github.com/xandergos/terrain-diffusion),
a neural model trained on real Earth topography and climate (SIGGRAPH 2026). Instead of stacking
simplex octaves, the world comes out of a diffusion pipeline that produces continents, drainage
networks, fjords, plateaus and mountain ranges with the structure of real terrain — and, alongside
the heightmap, a real climatology to go with it.

The mod uses all of it. Terrain, temperature, rainfall, forests, the surface you walk on and the
seasons all come from the same model, so the landscape and the life on it agree with each other.

## Requirements

- Vintage Story 1.22 (targets .NET 10, same as the game)
- A running Terrain Diffusion API — see below
- A GPU is strongly recommended for the model. CPU inference works but is roughly 10-20x slower.

### Terrain source

The mod can get terrain from either of two places, set by `terrain.source` in the mod config.

**`"api"` (default)** talks to the reference implementation's HTTP server:

```bash
git clone https://github.com/xandergos/terrain-diffusion
cd terrain-diffusion
pip install -r requirements.txt
python -m terrain_diffusion api xandergos/terrain-diffusion-30m --port 8000
```

It must be run from the repository root (some of its data paths are relative), and the first launch
downloads the model from Hugging Face. Point `terrain.url` at it; the default is
`http://localhost:8000`. If the model you serve is not the 30 m one, set
`terrain.nativeResolutionMeters` to match — the API does not report it.

**`"local"`** runs an ONNX port of the same pipeline inside the server process, so no Python service
is needed. It downloads ~2.3 GB of model files once, needs ~3 GB of RAM while resident, and picks
CUDA, DirectML, CoreML or CPU automatically. It also exposes the model's coarse map, which makes the
spawn search and the terrain height survey far quicker than the API's full-detail probing.

## World settings

| Setting                  | Default | What it does                                                     |
| ------------------------ | ------- | ---------------------------------------------------------------- |
| Terrain diffusion        | on      | Turn off to fall back to vanilla terrain, keeping the mod installed. |
| Diffusion resolution     | 15 m    | Real-world metres per block, horizontally *and* vertically.       |
| Vertical exaggeration    | 1x      | Multiplies terrain height. 1x is true scale.                      |
| Diffusion climate        | on      | Whether the model drives climate as well as terrain.              |

These are also settable from the mod config (`worldGen.scaleOverride`,
`worldGen.verticalExaggerationOverride`, `worldGen.climateMode`), which is the only way to reach
them on a dedicated server.

**Set the world height to 1024 when creating the world.** See below for why.

## Scale, and why the world needs to be tall

By default a block is exactly as tall as it is wide — the same geometry the Terrain Diffusion
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

Above that the mapping bends over towards the ceiling instead of clipping — the curve is
`u / (1 + u)`, which has slope 1 where it joins the linear part so there is no crease, and never
quite flattens, so summits round off rather than shearing into mesas. It still costs you the
faithfulness of the highest ground, so a taller world is better.

If a tall world is not an option, set `worldGen.heightMode` to `"auto"`. That surveys the region
around spawn once, measures how tall its peaks actually get, and stretches the metre-to-block
mapping so they reach near the ceiling of whatever world you have. The landscape then uses the full
height available at the cost of being vertically exaggerated — a gentle region might come out at
4x real relief. The measurement depends only on the seed and is stored in the save.

Lower resolutions are also *cheaper*, because one model pixel covers more blocks. 30 m per block
crosses a continent in an afternoon; 5 m per block makes the same mountain four kilometres of
walking, and needs a very tall world to stay true to scale.

## Climate

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
saved with the region and — unlike the region's other maps — sent to clients, so **a client with
this mod installed swings its weather in step with the server**. The mod is not required on
clients; a vanilla client just keeps vanilla's seasons for display purposes.

`/tdiff season <x> <z>` walks a year at a position and prints what it does.

## What the mod changes

- **Terrain pass** — vanilla `GenTerra`'s chunk handler is swapped for one that fills columns from
  the diffusion heightmap. Rock strata, caves, block layers, ponds, vegetation, structures and
  everything else in later passes run unchanged on top of it.
- **Climate map** — temperature and rainfall from the model, pre-compensated for the altitude
  corrections the game applies on read. The geologic activity byte is still vanilla's.
- **Forest and shrub maps** — replaced with model-driven cover.
- **Ocean map** — fed from the model's elevation, so systems that avoid the sea agree with the
  coastline that actually got generated.
- **Surface pass** — after vanilla's block layers, two things it cannot know about are fixed up:
  slopes too steep to hold soil are scoured back to bare rock (vanilla upholsters cliff faces in
  eight blocks of dirt), and ground whose warmest month never rises above freezing is capped with
  glacier ice.
- **Seasons** — temperature and rainfall swing through the year on the model's seasonality instead
  of latitude.
- **Spawn** — the model decides where continents are, so the world centre is as likely to be open
  ocean as land. The spawn is searched for and moved to solid ground.
- **Surface block layer altitudes** — only when terrain is vertically exaggerated. Vanilla's bands
  are fractions of world height (bare mountain gravel above 0.66 of it) and assume a block is about
  a metre; at true scale that already lines up, so nothing is touched.

Everything else — rock strata, ores, caves, rivers, ruins, traders, temporal stability — is vanilla.

## Commands

`/terraindiffusion`, or `/tdiff`. Requires the `controlserver` privilege.

| Subcommand           | What it shows                                                          |
| -------------------- | ---------------------------------------------------------------------- |
| `status`             | Terrain source, world scaling, tiles generated and average tile time.   |
| `here`               | The model's elevation, slope, full bioclimate and derived cover at you. |
| `season <x> <z>`     | The seasonal temperature and rainfall cycle at a position.              |
| `column <x> <z>`     | What actually got generated in a column, next to what the model said.   |

## Configuration

`ModConfig/vsterraindiffusion.json`, written on first start.

### Terrain source

| Key                            | Default                  | Meaning                                        |
| ------------------------------ | ------------------------ | ---------------------------------------------- |
| `terrain.source`               | `"api"`                  | `"api"` or `"local"`.                           |
| `terrain.url`                  | `http://localhost:8000`  | Base URL of the Terrain Diffusion API.          |
| `terrain.timeoutSeconds`       | 600                      | Per-request timeout. Uncached regions are slow. |
| `terrain.retries`              | 2                        | Retries before a chunk gives up.                |
| `terrain.nativeResolutionMeters` | 30                     | Metres per model pixel; must match the model.   |

### Machine

| Key                          | Default | Meaning                                                |
| ---------------------------- | ------- | ------------------------------------------------------ |
| `inferenceDevice`            | `auto`  | `auto`, `cpu`, `cuda`, `directml`, `coreml`. Local only. |
| `offloadModels`              | true    | One model on the GPU at a time. Local only.             |
| `tileCacheMegabytes`         | 256     | Decoded tensor windows per pipeline stage. Local only.  |
| `terrainTileCacheMegabytes`  | 256     | Finished terrain tiles. Raise if you see thrash warnings. |
| `terrainTileSizeBlocks`      | 256     | Blocks per model query. Multiple of 32.                 |
| `verboseInference`           | false   | Log every model window.                                 |

### World generation

Changing anything here after a world has been explored will make new chunks disagree with old ones.

| Key                              | Default       | Meaning                                                     |
| -------------------------------- | ------------- | ----------------------------------------------------------- |
| `heightMode`                     | `"isotropic"` | `"isotropic"`, `"manual"` or `"auto"`.                        |
| `metersPerBlockVertical`         | 0             | manual: metres of elevation per block.                        |
| `targetPeakFillFraction`         | 0.92          | auto: how much of the height the region's peaks should fill.  |
| `peakQuantile`                   | 0.995         | auto: which elevation quantile counts as a peak.              |
| `calibrationRadiusBlocks`        | 4096          | auto: half-width of the surveyed area.                        |
| `calibrationProbes`              | 8             | auto: full-detail probes. Over the API these are the survey.  |
| `minAutoExaggeration` / `max`    | 1 / 20        | auto: bounds on the chosen vertical gain.                     |
| `linearKneeFraction`             | 0.85          | Fraction of the height mapped perfectly linearly.             |
| `oceanDepthFraction`             | 0.9           | How much of the space below sea level the abyss reaches.      |
| `slopeDetailStrength`            | 1             | Perlin roughness added to sloped ground.                      |
| `spawnSearchProbes`              | 64            | Probes the spawn search may run. API only.                    |
| `spawnSearchStrideBlocks`        | 8192          | Roughly how far apart those probes sit. API only.             |
| `rainfallBasis`                  | `"moisture"`  | `"moisture"` (aridity) or `"precipitation"` (raw mm).         |
| `moistureMedian` / `moistureSpread` | 0.62 / 1.0 | Log-normal fit to the model's tree moisture over land.        |
| `rainfallMedianMm` / `rainfallSpread` | 540 / 0.8 | The same for raw precipitation.                              |
| `rainfallBias`                   | 0.05          | Added to rainfall; see below. Raise for a lusher world.       |
| `temperatureOffsetC`             | 0             | Degrees added to every model temperature.                     |
| `forestDensityMultiplier`        | 1             | Scales forest cover.                                          |
| `shrubDensityMultiplier`         | 1             | Scales shrub cover.                                           |
| `seasonalTemperature`            | true          | Swing temperature on the model's seasonality.                 |
| `seasonalTemperatureStrength`    | 1             | Multiplies that swing. 0 gives a world with no seasons.       |
| `seasonalPrecipitation`          | true          | Swing rainfall on the model's precipitation seasonality.      |
| `seasonalPrecipitationStrength`  | 1             | Multiplies the wet/dry contrast.                              |
| `seasonHemispheres`              | false         | Opposite seasons north and south of the map's middle.         |
| `rescaleBlockLayerAltitudes`     | true          | Stretch vanilla's altitude bands. No effect at true scale.    |
| `bareSlopeRock`                  | true          | Leave slopes too steep for soil as bare rock.                 |
| `glacierIce`                     | true          | Cap permanently frozen ground with glacier ice.               |
| `climateMode`                    | `""`          | `"full"` or `"off"` to override the world setting.            |
| `scaleOverride`                  | 0             | Overrides the resolution. Values above 6 are only settable here. |
| `verticalExaggerationOverride`   | 0             | Overrides the height multiplier.                              |

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
