# VS Terrain Diffusion — a Vintage Story mod

Generates Vintage Story worlds with [Terrain Diffusion](https://github.com/xandergos/terrain-diffusion),
a neural model trained on real Earth topography and climate (SIGGRAPH 2026): continents, drainage
networks, fjords, plateaus and mountain ranges with the structure of real terrain, and a real
climatology alongside the heightmap. Terrain, temperature, rainfall, forests, the surface you walk
on and the seasons all come from the same model.

**Contents** — [Installing](#installing) · [Creating a world](#creating-a-world) ·
[What the mod changes](#what-the-mod-changes) · [How it works](#how-it-works) ·
[Commands](#commands) · [Configuration](#configuration) · [Building](#building)

## Installing

Drop the release zip in your `Mods` folder. The models download themselves on first launch.

- Vintage Story 1.22 (targets .NET 10, same as the game)
- **~2.2 GB of disk** for the selected model files, fetched once, plus the optimised-graph cache
- **~3 GB of RAM** while the models are resident
- A GPU is strongly recommended. CPU inference works but is roughly 10-20x slower.

| Platform          | Device used automatically | Notes                                            |
| ----------------- | ------------------------- | ------------------------------------------------ |
| Linux + NVIDIA    | CUDA                      | Needs a system CUDA 12 or 13 runtime and cuDNN 9  |
| Windows           | DirectML                  | Any modern GPU; no extra install                  |
| macOS (Apple)     | CoreML                    | No extra install                                  |
| Anything else     | CPU                       | Works, but slow                                   |

The matching ONNX Runtime native library is downloaded on first use too (a few MB for CPU/DirectML,
~300 MB for CUDA). Nothing is installed by hand.

Server-side. Clients may install it to keep their weather display in step with the server, but
vanilla clients can join normally.

The first world sits on the loading screen until everything is fetched; the screen reports each
download, and `Logs/server-main.log` has the detail.

## Creating a world

Settings added by this mod:

| Setting                  | Default | What it does                                                         |
| ------------------------ | ------- | -------------------------------------------------------------------- |
| Terrain diffusion        | on      | Turn off to fall back to vanilla terrain, keeping the mod installed.  |
| Diffusion resolution     | 15 m    | Real-world metres per block, horizontally *and* vertically.           |
| Vertical exaggeration    | 1x      | Multiplies terrain height. 1x is true scale.                          |
| Diffusion climate        | on      | Whether the model drives climate as well as terrain.                  |

A dedicated server has no world-creation screen, so these are also reachable from the mod config as
`worldGen.climateMode`, `worldGen.scaleOverride` and `worldGen.verticalExaggerationOverride`.

Vanilla settings, and what becomes of them:

| Setting                  | Default        | What it does here                                                |
| ------------------------ | -------------- | ----------------------------------------------------------------- |
| **World height**         | —              | **Set this to 1024.** Vanilla's default is far too short for real mountains. |
| Landcover                | 97.5%          | Honoured — it decides where the sea goes. See [Land and sea](#land-and-sea). |
| Landcover scale (`oceanscale`) | 500%     | Honoured — it decides how big the oceans are. Same section.        |
| Global temperature       | normal         | Honoured — the model draws a world that cold or that hot. See [below](#a-hotter-colder-wetter-or-drier-world). |
| Global precipitation     | normal         | Honoured — same, for rainfall.                                     |
| Polar–equator distance   | 100 000 blocks | Honoured — it puts the tropics, the deserts and the ice where the game says. See [Latitude](#latitude). |
| Starting climate         | temperate      | Honoured, two ways: it sets which latitude the map centre sits at, and the spawn search finds land there. See [below](#starting-climate). |
| Forestation & shrubs     | normal         | Honoured — vanilla applies it on top of the model's forest map, unchanged. |
| Climate distribution     | realistic      | "Patchy" turns off the latitude bands, since a patchy world has no latitude. |
| Geologic activity        | rare           | Honoured — that byte of the climate map is still vanilla's.        |
| Landform scale           | 100%           | **Almost nothing.** See below.                                     |
| Upheaval rate            | 30%            | **Nothing.** See below.                                            |

Everything not listed — ores, caves, temporal stability, world size — is untouched and behaves
exactly as it does in an unmodded world.

### Two settings the mod cannot honour

Both configure maps that only fed vanilla's terrain generator, which this mod replaces.

- **Upheaval rate**: no effect. The map is still generated and saved; nothing reads it.
- **Landform scale**: no effect on terrain. `GenDungeons` still reads the landform map to place
  dungeons needing flat ground, so one can land on what the map calls a plain and the model made a
  hillside. Two shipped tiled dungeons are affected.

`worldGen.slopeDetailStrength` and `Vertical exaggeration` are the nearest relief controls.

### Land and sea

The world's ocean map decides where the coastline is, and the model decides what it looks like.
Before generating anything the mod reads Vintage Story's ocean map — the one "Land cover" and
"Ocean scale" configure — and feeds it to the model as conditioning, so a world set to 50% land
gets 50% land, with the shelf, the fjords and the mountains behind them drawn from real terrain.

It reads whichever ocean map is installed — Continental World's, for instance — and leaves it
untouched afterwards.

- **Conditioning is soft.** The coast wanders around the one it was given rather than tracing it,
  which is what makes it look natural. Sea fraction comes out within ~4 points of the setting;
  column by column 88% agree, nearly all disagreement within one cell of a coastline.
- **Resolution floor.** One conditioning pixel spans 512 blocks at the default resolution, so
  anything smaller fills in as land. Low "Ocean scale" worlds lose their smallest islands and lakes.

Vanilla's defaults (97.5% land, 500% ocean scale) give a nearly unbroken continent. 40–60% land is
where real coastlines start to appear.

Set `worldGen.oceanMap` to `"output"` for the reverse arrangement: the model invents its own
continents from real-world terrain, ignoring both world settings, and the ocean map is rewritten to
match it.

### A hotter, colder, wetter or drier world

Vanilla multiplies these onto the climate map it drew at random. This mod hands them to the model,
so a "Semi-Arid" world is *drawn* semi-arid: the forests, soil, snow line and seasons all follow.
Rainfall is scaled outright; temperature moves the world along the real distribution instead, since
nowhere on Earth has a mean above 30 °C. What that cannot reach is applied to the output afterwards
as vanilla does.

Measured on two seeds across both settings' full range: temperature within 2 °C up to "Hot", which
overshoots ~5 °C for want of anywhere hotter to draw from; rainfall within 10% in a region of
ordinary wetness, less where it is already very wet or very dry. "Very hot" and "Scorching hot"
saturate the game's climate scale, as in an unmodded world — and leave the spawn search no temperate
land, so it settles for the coolest thing going.

Set `worldGen.globalClimateStrength` to 0 to apply both to the model's output instead.

### Latitude

Vanilla's latitude is a straight line from +40 °C to −20 °C with no rainfall gradient. This mod
conditions the model on a real zonal climate instead — equatorial rain belt, subtropical deserts at
25°, mid-latitude storm track at 50°, dry cold caps — so a tropical belt is *drawn* tropical, and
coasts, rain shadows and mountains all happen within the band.

Where each block sits between equator and pole is the game's answer, read from
`polarEquatorDistance`, so the snow line agrees with day length and the midnight sun. The phase
comes with it, which is how "Starting climate" lands the map centre at the right latitude.

Measured over equator-to-pole transects on three seeds: temperature ~1 °C warm on average, 2 °C
either way in a belt; from ~80° poleward the model runs out of world (it has never seen a mean below
−14 °C) so the last stretch is added afterwards and the poles read −20 °C. Rainfall lands on the
band in the median and swings a factor of two either side — geography, not error. A 15 000-block
polar distance still tracked the bands to 1.5 °C.

Heading north or south now changes the climate; east or west mostly does not. 200k–400k gives long
belts, 15k–25k puts tropics and ice within a day's walk. The hemispheres get opposite years, as the
game's calendar already does (`worldGen.seasonHemispheres`).

Set `worldGen.latitudeStrength` to 0 for an unrooted world; values between weaken the gradient
without moving it. Bands are off on a "Patchy" world.

### Starting climate

The bands mean what they do in an unmodded world, as annual mean temperature: hot 28–32 °C, warm
19–23 °C, temperate 6–14 °C, cool −5 to 1 °C, icy −15 to −10 °C.

With latitude bands on, the map centre already sits at the right latitude and the search only has to
find land there — usually a few hundred blocks. With `latitudeStrength` at 0 it hunts for a matching
climate wherever the model put one, and cold is found on high ground rather than far north.

It prefers to travel east or west, because distance along Z buys midnight sun past the polar
distance. It stops at the first match, so most worlds spawn within a few thousand blocks in under a
second; a distant band — usually "hot" — takes longer, and on CPU inference longer still. If the
seed has no such land in range the log says so and you spawn at the closest temperature found.

Set `worldGen.startingClimateSearch` to false to spawn on the nearest land whatever its climate.

## What the mod changes

- **Terrain pass** — `GenTerra`'s chunk handler fills columns from the diffusion heightmap. If
  another mod already replaced terrain generation, the model supplies heights to *it*; see
  [Other terrain mods](#other-terrain-mods).
- **Climate map** — sea-level temperature and annual rainfall from the model, with the game's
  altitude correction replaced by the real lapse rate. The geologic activity byte stays vanilla's.
- **Global temperature and precipitation** — conditioning rather than post-processing.
- **Latitude** — the game's own latitude, from `polarEquatorDistance`, conditioned in as a real
  zonal climate in place of vanilla's straight line from +40 °C to −20 °C.
- **Forest and shrub maps** — cover derived from the model's moisture and growing season.
- **Ocean map** — read, not written: it is what the terrain is conditioned on.
- **Surface pass** — slopes too steep for soil are scoured back to bare rock (vanilla upholsters
  cliffs in eight blocks of dirt), and ground whose warmest month stays below freezing is capped
  with glacier ice.
- **Seasons** — the year swings on the model's seasonality rather than on latitude alone, opposite
  sides of the equator included.
- **Spawn** — moved to solid ground in the world's chosen starting climate.
- **Surface block layer altitudes** — only when terrain is vertically exaggerated; at true scale
  vanilla's bands already line up.
- **Nothing else.** The landform and upheaval maps are still generated and still ignored, because
  the generator that read them is gone — see
  [Two settings the mod cannot honour](#two-settings-the-mod-cannot-honour). Rock strata, ores,
  caves, rivers, ponds, ruins, traders and temporal stability are vanilla, running unchanged on top.

## Other terrain mods

Mods that only supply a map — Continental World's ocean map, for instance — need nothing special:
the mod reads whatever map is installed and conditions the model on it.

A mod that *replaces terrain generation itself* cannot layer with this one: two generators filling
the same column give the union of both landscapes. So whoever generates terrain gets handed the
model's heights and does the filling.

**Algernon's Watersheds** is supported this way. It disables vanilla `GenTerra` and fills every
column itself, so this mod stops generating terrain and instead answers every height question
Watersheds asks: the height its analysis is built on, the height a stream's profile is laid against,
the height after a stream has cut in, and which blocks are solid. Answering *all* of them matters —
a stream's water surface and its bed are worked out separately, so one height left coming from
Watersheds' own landscape strands water in the air. Its ridge and gully erosion filter is switched
off for these worlds: it cuts valley detail into fractal noise, the model's landscape already has
erosion, and it is computed inside two of those height answers. Everything downstream — stream
water, banks, rapids, groundwater, block layers — is Watersheds' own.

- **World creation takes longer.** The analysis samples heights kilometres around spawn, all from
  the model, so the first load spends a few minutes on tiles it will not visibly use. One-time per
  area, and cached.
- **Watersheds decides where streams go.** It will not path one across terrain rougher than
  `SmallChunkRoughnessThreshold` in `ModConfig/Watersheds/TerrainAnalysisConfig.json` (2 blocks
  RMSE by default). Ordinary modelled landscape measures 0.0–0.8; genuinely broken ground gets no
  small streams. Raise the threshold if you want them anyway.
- **Streams need somewhere to drain.** At vanilla's default land cover there is almost no ocean to
  reach, so almost no streams. That is Watersheds' behaviour, not this mod's.

Stream maps live in a database beside the save, so a world explored with an older version of this
mod has streams plotted against the wrong landscape: `/watersheds clearstreammaps` and regenerate,
or start a new world.

If Watersheds updates in a way this cannot reach into, the mod says so and takes itself out of the
world rather than generating a broken one.

## How it works

### Scale, and why the world needs to be tall

By default a block is as tall as it is wide — 15 m in every direction at the default resolution —
so a 2 000 m massif is 133 blocks of climbing and every slope has its real-world grade.

Real mountains need room. The model's land runs to about 3 000 m at the 95th percentile and 5 000 m
at the extreme, which at 15 m per block is 200 and 333 blocks *above sea level*:

| World height | Blocks above sea | Terrain held at true scale |
| ------------ | ---------------- | -------------------------- |
| 256          | 145              | up to ~1 900 m             |
| 512          | 289              | up to ~3 700 m             |
| 1024         | 578              | up to ~7 400 m             |

Past that the mapping bends towards the ceiling on `u / (1 + u)` rather than clipping, so summits
round off instead of shearing into mesas — at the cost of the highest ground's faithfulness.

If a tall world is not an option, `worldGen.heightMode: "auto"` surveys the region around spawn
once and stretches the metre-to-block mapping so its peaks reach near the ceiling. The landscape
uses the full height at the cost of exaggerated relief — a gentle region might come out at 4x. The
measurement depends only on the seed and is stored in the save.

Resolution is also the main performance dial: at 30 m per block you cross a continent in an
afternoon, at 5 m the same mountain is four kilometres of walking.

### Climate

The model predicts four WorldClim bioclimatic variables everywhere it predicts elevation:

| Variable | What it is                                             |
| -------- | ------------------------------------------------------ |
| BIO1     | annual mean temperature, °C                             |
| BIO4     | temperature seasonality — the spread of monthly means   |
| BIO12    | annual precipitation, mm                                |
| BIO15    | precipitation seasonality — how unevenly it falls       |

A real climatology, with maritime coasts, continental interiors, rain shadows and altitude already
in it — but nothing in the model knows which way is north, so the latitude bands supply that axis.
`globalTemperature` and `globalPrecipitation` condition the bands themselves, so a "Snowball earth"
world is one whose every band sits a quarter of the way up the scale.

### From bioclimate to what the game reads

800 mm of rain is generous in Lapland and semi-arid in the Sahel, so the mod derives potential
evapotranspiration, an aridity index and a growing season, and keys everything off those. The
formulas are ported from the reference implementation's biome classifier.

**Rainfall** is a quantile map, not a physical conversion. Vanilla draws its 0-255 byte *uniformly*
and every threshold reading it was tuned against that spread, so a physical quantity fed straight in
makes the world read as desert. The model's tree moisture goes through its own distribution instead.
`worldGen.rainfallBasis: "precipitation"` maps raw millimetres.

**Forest and shrub cover** come from the same moisture, scaled by growing season and cut to zero on
ground too steep for soil. Vanilla's `MapLayerWobbledForest` computes `128 - rain * temp / 65025`,
a product that never exceeds 1, so its forest density is pure noise with no relation to climate;
woodland in the foothills and nothing above the treeline are new behaviour. Everything that read
that map still does, so animals and undergrowth follow the woods.

**Temperature** is stored as sea-level temperature, and the mod replaces the lapse rate applied on
read. Vanilla's flat 0.157 °C per block is only right at about 24 m per block and over-cools
mountains at anything finer; 6.5 °C/km reads back as the model predicted at any vertical scale.

### Seasons

Vanilla takes the year's amplitude from latitude alone (`|latitude| * 65` degrees), so the equator
has no seasons and nothing else about a place matters. Here it comes from BIO4: a maritime coast
and a continental interior at the same annual mean get completely different years. Precipitation
seasonality does the same for rain, giving monsoon climates a real dry season.

Neither channel fits Vintage Story's packed climate integer, whose interpolator only touches the low
three bytes, so they are map region mod data — saved with the region and, unlike its other maps,
sent to clients. A vanilla client falls back to vanilla's seasons for display.

`/tdiff season <x> <z>` walks a year at a position and prints what it does.

## Commands

`/terraindiffusion`, or `/tdiff`. Requires the `controlserver` privilege.

| Subcommand           | What it shows                                                          |
| -------------------- | ---------------------------------------------------------------------- |
| `status`             | Device, world scaling, tiles generated, average tile time, and where that time went: total model inference, its share of tile time, and a per-stage breakdown. A low inference share means something other than the GPU is the bottleneck. |
| `gpulimit [percent]` | The share of the time inference is allowed to keep the device busy, and how much has been given up to the limit so far. With a percentage, sets it there and now, and saves it. |
| `map`                | The debug map's address, and how many tiles it is holding. |
| `here`               | Elevation, slope, full bioclimate and derived cover where you stand, plus the latitude diagnostics below. |
| `season <x> <z>`     | The same diagnostics at a position, and the year's temperature and rainfall cycle there. Usable from a server console, where `here` is not. |
| `column <x> <z>`     | What actually got generated in a column, next to what the model said.   |

`here` and `season` share four climate diagnostics:

- **Latitude** and **Hemisphere** — distance from the equator, which side, and the season the game's
  calendar reports there. Check this first if foliage or crops look out of step.
- **Sea-level temperature** — the reading with altitude taken back out, and the fitted local lapse
  rate. The number to compare two places by. Slightly slower than the rest: it is a pipeline query.
- **Band temperature** and **Band precipitation** — what the latitude band asked for and how far
  this column sits from it, plus **Band offset applied** when part of the band was added after the
  model ran.

A single column scatters several degrees either side of its band, which is the model's business;
consistent drift over many columns is not.

## Configuration

`ModConfig/vsterraindiffusion.json`, written on first start. [CONFIG.md](CONFIG.md) is the whole
default file with a comment on every field; the tables below are the short version.

Optional: [ConfigLib](https://mods.vintagestory.at/configlib) gives the same settings an in-game
screen, editing this file in place rather than keeping a copy.

`gpuUtilizationPercent` and `verboseInference` take effect on save; everything else is read when the
world generator starts, so it needs a restart. **Useful range** below is where a setting does
something sensible, not where it is legal — CONFIG.md lists the hard limits.

### Inference

Machine settings. Keep `inferenceDevice` and the three precision settings fixed after exploring a world:
changing either can make newly generated terrain disagree slightly with existing chunks.

| Key                          | Default | Useful range | Meaning                                 |
| ---------------------------- | ------- | ------------ | ---------------------------------------- |
| `inferenceDevice`            | `auto`  | `auto` `cpu` `openvino` `cuda` `tensorrt-rtx` `directml` `coreml` | OpenVINO and TensorRT RTX are opt-in. OpenVINO, on 64-bit Linux, accelerates the decoder while leaving the large stages on ORT CPU. TensorRT RTX needs a GeForce RTX 30xx or newer on 64-bit Windows or Linux, fetches the NVIDIA runtime once (105 MB on Windows, 140 MB on Linux) and builds a cached engine per model; it is about 1.5x faster than CUDA on the same FP32 models and 2.4x with FP16. A provider whose runtime cannot be prepared falls back to whatever the machine would have chosen for itself - DirectML on Windows, so an AMD or Intel card is still a GPU path, CUDA on Linux with an NVIDIA driver, ORT CPU otherwise - and that provider is written back to the config so the next start comes up on it. On Windows the replacement needs a different ONNX Runtime than TensorRT RTX loaded, so the game stops once and comes up on it when restarted. All of this is logged because changing provider can alter new terrain slightly. Only what the selected provider needs is fetched: TensorRT RTX skips the CUDA provider library it never loads, and `cuda` on Windows pulls the cuBLAS/cuFFT/NVRTC/cuDNN libraries it links against (~1 GB, once) only if no CUDA toolkit and cuDNN are installed; Linux and macOS use the system CUDA install. |
| `modelLoadMode`              | `auto`  | `auto` `memory` `file` | Load model graphs from RAM or their optimised files. Auto uses files for CPU and memory-constrained hosts. |
| `offloadModels`              | false   | on / off     | Hold only one model on the GPU at a time, saving about 1 GB of VRAM. Generating a tile runs two or three of the models, so every tile then pays to rebuild a session for a graph of most of a gigabyte: measured on a 6 GB card it triples the average tile time. Turn on only if the models will not fit. |
| `gpuUtilizationPercent`      | 100     | 40 – 100     | Share of the time world generation may keep the device busy. Lower it if generating chunks makes the game stutter; see [Stuttering](#stuttering) below. World generation slows by the reciprocal. |
| `validateModelHashes`        | true    | on / off     | Verify SHA-256 of existing model files on startup. Off saves a few seconds of disk read. |
| `downloadRuntime`            | true    | on / off     | Fetch the ONNX Runtime and, when selected, OpenVINO native libraries automatically. |
| `decoderPrecision`           | `fp32`  | `fp32` `fp16` `int8` | Select and automatically fetch only the matching decoder. FP16 is for GPU providers, INT8 for CPU/OpenVINO. Both are opt-in and change newly generated terrain slightly; a missing or invalid selected decoder stops model loading instead of silently changing precision. |
| `basePrecision`              | `fp32`  | `fp32` `fp16` | The base model is most of a tile's work, so FP16 here is the biggest GPU win: on an RTX 3060, TensorRT RTX with FP16 base and decoder generated the same ten regions in 7.6 s against 19.0 s on CUDA FP32, for about 4 m mean elevation difference (CPU vs GPU is already ~2.6 m). Needs a GPU provider; on CPU it is slower than FP32 and still changes terrain. |
| `coarsePrecision`            | `fp32`  | `fp32` `fp16` | Separate because the trade is poor: the coarse sampler runs twenty steps per tile, so FP16 saved 0.3 s and moved elevation a further 2 m on the same bench. |
| `tileCacheMegabytes`         | 256     | 128 – 1024   | Total decoded tensor-window cache across all pipeline stages. |
| `latentBatchSize`            | 0       | 0 – 4        | Latent windows per base-model call. Zero chooses 1 on CPU and 4 on GPU. |
| `terrainTileCacheMegabytes`  | 256     | 128 – 1024   | Finished terrain tiles. Raise if you see thrash warnings. |
| `terrainTileSizeBlocks`      | 0       | 0, 128 – 512 | Blocks generated per model invocation, a multiple of 32. Zero chooses 128 on CPU and 256 on GPU; larger values amortise the model better but make first-visit stalls longer. |
| `debugMapPort`               | 0 (off) | 8088         | Serves the [debug map](#debug-map) on this port. 0 opens no port. |
| `debugMapBindAddress`        | `127.0.0.1` | loopback | Where the debug map listens. `0.0.0.0` publishes your world's terrain to the network. |
| `debugMapHistoryTiles`       | 2048    | 512 – 8192   | Tiles the debug map remembers, about 8 KB each. |
| `verboseInference`           | false   | on / off     | Log every terrain tile at notification level. Noisy; for diagnosing slowness. Off, those lines still go to the debug log and only a tile that stalls — a second or more, and four times the session average — reaches the main one. |

#### Stuttering

In single player the model shares the GPU with the renderer, and a submitted graph runs to
completion, so a burst of chunk generation reads as a freeze even though the game thread is not
blocked.

`gpuUtilizationPercent` below 100 idles the generator after each run, so the renderer gets regular
windows. It cannot shorten an individual run, and world generation slows by the reciprocal: at 50% a
tile takes about twice as long. Measured on a 6 GB laptop card at 40%, a tile went from 142 ms to
323 ms, and total inference time rose 14.7 s to 16.5 s because a card that keeps going idle drops
its clocks.

Start at 50 and go down only as far as the stutter needs; too low and generation cannot keep up with
a walking player. `/tdiff gpulimit <percent>` changes it without a restart. On a dedicated server
leave it at 100 unless you want the card for something else.

#### Debug map

Set `debugMapPort` and the mod serves a read-only page of what the model is producing, updating as
tiles are generated. `/tdiff map` prints the address; the default binding is loopback.

Eight layers: surface height, model elevation, slope, mean temperature, temperature seasonality,
annual precipitation, precipitation seasonality, and the 0–255 rainfall byte the game reads. Drag to
pan, wheel to zoom, hover a column for all eight.

It keeps its own record, because the generator's tile cache drops a tile as soon as it has moved on.
Each is a 32×32 thumbnail, a byte per column per layer, so the default 2048-tile history costs about
17 MB. Point it at `0.0.0.0` only to publish your world's terrain to the network; the server logs a
warning if you do.

### World generation

These decide what the world looks like. Changing one after a world has been explored will make new
chunks disagree with old ones.

**Height and scale**

| Key                              | Default       | Useful range | Meaning                                  |
| -------------------------------- | ------------- | ------------ | ----------------------------------------- |
| `heightMode`                     | `"isotropic"` | `"isotropic"` `"manual"` `"auto"` | True scale, a fixed metres-per-block, or fit the terrain to the world's height. |
| `metersPerBlockVertical`         | 0             | 5 – 30       | `"manual"` only: metres of elevation per block. 0 leaves the mode's own answer. |
| `linearKneeFraction`             | 0.85          | 0.7 – 0.95   | Fraction of the height mapped perfectly linearly before summits start compressing. Lower keeps more of the range for the compressed tail. |
| `oceanDepthFraction`             | 0.9           | 0.6 – 1      | How much of the space below sea level the abyss reaches. Lower gives shallower seas and more room for the sea bed's detail. The shore end is not scaled by it — the first column past the beach is one block of water at any world height. |
| `slopeDetailStrength`            | 1             | 0.5 – 2      | Perlin roughness added to sloped ground. 0 gives glassy hillsides; above 2 the noise starts competing with the terrain. |
| `scaleOverride`                  | 0             | 1 – 6        | Overrides the world's resolution: blocks per 30 m model pixel. 0 uses the world setting. Above 6 is settable but generation cost grows with the square. |
| `verticalExaggerationOverride`   | 0             | 0.5 – 2      | Overrides the world's height multiplier. 0 uses the world setting. |

**Height calibration** (`heightMode: "auto"` only)

| Key                              | Default       | Useful range | Meaning                                  |
| -------------------------------- | ------------- | ------------ | ----------------------------------------- |
| `targetPeakFillFraction`         | 0.92          | 0.8 – 0.95   | How much of the available height the region's peaks fill. Leave headroom: 1 puts summits against the ceiling. |
| `peakQuantile`                   | 0.995         | 0.99 – 0.999 | Which elevation quantile counts as a peak. Lower ignores the highest ground and exaggerates everything else. |
| `calibrationRadiusBlocks`        | 4096          | 2048 – 16384 | Half-width of the surveyed area. Wider is more representative and costs a few more seconds, once. |
| `calibrationProbes`              | 8             | 4 – 16       | Full-detail probes on the tallest surveyed cells. 0 falls back to `reliefFactor`. |
| `reliefFactor`                   | 1.6           | 1.3 – 2      | Assumed peak-to-survey ratio when probing is off or fails. |
| `minAutoExaggeration` / `maxAutoExaggeration` | 1 / 20 | 1 – 4 / 4 – 30 | Bounds on the vertical gain calibration may choose. Raising the minimum above 1 forbids a world flatter than true scale. |

**Climate and vegetation**

| Key                              | Default       | Useful range | Meaning                                  |
| -------------------------------- | ------------- | ------------ | ----------------------------------------- |
| `climateMode`                    | `""`          | `""` `"full"` `"off"` | Overrides the world's "Diffusion climate" setting. Empty uses it. |
| `rainfallBasis`                  | `"moisture"`  | `"moisture"` `"precipitation"` | What the game's rainfall byte is quantile-mapped from: the model's aridity-derived tree moisture, or raw millimetres. |
| `moistureMedian` / `moistureSpread` | 0.62 / 1.0 | 0.4 – 0.9 / 0.7 – 1.4 | Log-normal fit to the model's tree moisture over land. Raising the median makes the whole world read wetter to the game's biome thresholds; raising the spread pushes deserts and rainforests further apart. |
| `rainfallMedianMm` / `rainfallSpread` | 540 / 0.8 | 300 – 900 / 0.6 – 1.2 | The same for `"precipitation"` basis. |
| `rainfallBias`                   | 0.05          | -0.1 – 0.2   | Added to the rainfall byte, as a fraction. Raise for a lusher world; see the note below the tables. |
| `temperatureOffsetC`             | 0             | -5 – 5       | Degrees added to every model temperature, after the latitude band and the world's global setting. A blunt instrument; prefer the world settings. |
| `forestDensityMultiplier`        | 1             | 0.7 – 1.5    | Scales the forest cover the model's moisture implies. **Trees on the ground go as the square of this** — see below. |
| `shrubDensityMultiplier`         | 1             | 0.5 – 2      | Scales shrub cover the same way, and with the same squaring. |

**Seasons and surface**

| Key                              | Default       | Useful range | Meaning                                  |
| -------------------------------- | ------------- | ------------ | ----------------------------------------- |
| `seasonalTemperature`            | true          | on / off     | Swing temperature on the model's seasonality (BIO4) instead of on latitude alone. |
| `seasonalTemperatureStrength`    | 1             | 0.5 – 1.5    | Multiplies that swing. 0 gives a world with no seasons; above 1.5 a continental winter becomes unsurvivable. |
| `seasonalPrecipitation`          | true          | on / off     | Swing rainfall on the model's precipitation seasonality (BIO15), giving monsoon climates a real dry season. |
| `seasonalPrecipitationStrength`  | 1             | 0.5 – 1.5    | Multiplies the wet/dry contrast. Above about 1.4 the dry season clamps to no rain at all. |
| `seasonHemispheres`              | true          | leave on     | Swing the year the opposite way south of the equator. The game's calendar already does this; off, the southern hemisphere gets leaves that fall in the spring. |
| `bareSlopeRock`                  | true          | on / off     | Leave slopes too steep for soil as bare rock, instead of vanilla's eight blocks of dirt on a cliff face. |
| `glacierIce`                     | true          | on / off     | Cap ground whose warmest month stays below freezing with glacier ice. |
| `rescaleBlockLayerAltitudes`     | true          | on / off     | Stretch vanilla's altitude bands to the terrain height. No effect at true scale, where they already line up. |

**Coastlines**

| Key                              | Default   | Useful range | Meaning                                      |
| -------------------------------- | --------- | ------------ | --------------------------------------------- |
| `oceanMap`                       | `"input"` | `"input"` `"output"` | `"input"` conditions the model on the world's ocean map; `"output"` lets the model invent the continents and rewrites the map to match, ignoring Landcover and Landcover scale. |
| `landmaskStrength`               | 1         | 0.8 – 1      | How completely the ocean map overrides the model's own sense of where land belongs. Below about 0.8 the coastline stops resembling the map at all. |
| `landmaskNoiseLevel`             | 0.1       | 0.05 – 0.5   | How much noise the model is told the mask carries. **Lower binds it more tightly** — this runs the opposite way to its name in the model's config. 0.5 is the model's own value and reproduces the map over ~88% of the world; 0.1 gets that to 95%; below 0.05 the gain is under 2% and the land it keeps starts flattening. 0 uses the model's value. |

**Global climate**

| Key                              | Default | Useful range | Meaning                                        |
| -------------------------------- | ------- | ------------ | ----------------------------------------------- |
| `globalClimateStrength`          | 1       | 0.5 – 1      | How much of the world's global temperature and precipitation settings the model is conditioned on rather than having applied to its output. The world reads the same either way; what changes is whether the model knew. 0 is what vanilla does. |
| `climateNoiseLevel`              | 0       | 0, or 0.1 – 0.5 | How much noise the model is told the steered climate carries, on the same inverted scale as `landmaskNoiseLevel`. 0 uses the model's own value and is the default — unlike the landmask, the climate conditioning already tracks what it is asked for closely. |
| `latitudeStrength`               | 1       | 0, or 0.5 – 1 | How much of a north-south climate gradient the world gets. 1 puts the tropics, the subtropical deserts, the storm track and the ice where the game's `polarEquatorDistance` says. 0 is the unrooted world the mod made before 0.5. Values between the two weaken the gradient without moving it. |

**Spawn**

| Key                                  | Default | Useful range | Meaning                                    |
| ------------------------------------ | ------- | ------------ | ------------------------------------------- |
| `startingClimateSearch`              | true    | on / off     | Put the spawn on land in the world's chosen starting climate. Off spawns on the nearest land whatever its climate. |
| `startingClimateSearchRadiusBlocks`  | 65536   | 16384 – 262144 | How far to look before settling for the closest temperature it saw. The search stops at the first match, so this is only the give-up point. |
| `startingClimateNorthSouthCost`      | 2       | 1 – 4        | How much more reluctantly the search moves along Z than X, because Z is what buys midnight sun. With latitude bands on it rarely has to move far at all — the map centre already sits at the right latitude. |

**`rainfallBias`** puts back the average of Vintage Story's "higher ground is wetter" bonus, which
the climate map cancels (the model does orography properly) but vanilla's biome thresholds were
tuned with.

**`forestDensityMultiplier` is squared on its way to the ground.** Vanilla accepts each candidate
tree with probability `(byte / 255)²`, so 1.0 → 1.4 is roughly double the trees, and the setting
bites hardest where cover is already low. The byte saturates at 255, so much above 1.2 flattens the
wet end; 0 still scatters lone trees, because the acceptance probability floors at 0.0025. For a
treeless world use "Forestation & shrubs" at −100%.

The two differ: "Forestation & shrubs" is *additive*, lifting deserts as much as forests;
`forestDensityMultiplier` is *proportional*, preserving the climate pattern. Both apply.

## Building

```bash
./build.sh          # Linux and macOS
```

```bat
build.bat           :: Windows
```

Both take an optional configuration (`Release` by default) and produce
`dist/vsterraindiffusion_<version>.zip`.

Needs the .NET 10 SDK and a Vintage Story install: `/opt/vintagestory` or `~/Vintagestory` on Linux
and macOS, `%APPDATA%\Vintagestory` on Windows. Override either with `VINTAGE_STORY`.

## Credits

- Terrain Diffusion model, the reference implementation and the original Minecraft mod:
  [xandergos](https://github.com/xandergos)
- Mixed-precision decoder derived from that MIT-licensed model; its exact recipe and upstream
  copyright notice are in [`scripts/`](scripts/).
- Vintage Story integration: this mod
