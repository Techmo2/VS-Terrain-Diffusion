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

`Landform scale` and `Upheaval rate` both configure maps that exist only to feed vanilla's terrain
generator, and vanilla's terrain generator is the one thing this mod replaces outright.

- **Upheaval rate** has no effect at all. It scales the geological upheaval map, which nothing but
  `GenTerra` ever reads. The map is still generated and still saved into every region; nothing
  looks at it.
- **Landform scale** has no effect on terrain, and one small effect elsewhere: `GenDungeons` reads
  the landform map to place dungeons that require flat ground. Since that map no longer describes
  the terrain that actually got generated, such a dungeon can be sited on what the map calls a
  plain and the model made a hillside. Two of the shipped tiled dungeons are affected.

These are not oversights. Both settings describe how vanilla's landform palette should shape the
ground, and there is no landform palette here — the relief comes out of the model. There is no
equivalent knob to map them onto. `worldGen.slopeDetailStrength` and `Vertical exaggeration` are
the nearest things to a relief control.

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

### Latitude

Vanilla's latitude is a straight line: +40 °C at the equator, −20 °C at the pole, nothing in
between but noise, and no rainfall gradient at all. This mod hands the model a real one instead —
the equatorial rain belt, the subtropical deserts at 25°, the mid-latitude storm track at 50°, the
dry cold caps — as conditioning, so a tropical belt is *drawn* tropical and a polar one is drawn
polar, and everything the model knows about coasts, rain shadows and mountains happens **within**
the band it is in.

Where each block sits between the equator and the pole is Vintage Story's answer, not this mod's.
It reads the game's own latitude, which is what `polarEquatorDistance` configures and what the game
already uses for day length, midnight sun and which hemisphere has its summer when — so the snow
line and the sun agree with each other for free. It also inherits the phase, which is how vanilla
honours "Starting climate": the map centre lands on the latitude whose climate you asked for.

Measured over full equator-to-pole transects on three seeds:

- **Temperature** lands about 1 °C warm of the profile on average, 2 °C out either way in a typical
  belt. From roughly 80° poleward the model runs out of world — it has never seen a climate colder
  than about −14 °C mean annual — so the last stretch to the ice cap is added to its output, and
  the poles read −20 °C, which is the bottom of the game's own climate scale.
- **Rainfall** lands on the band in the median and swings by a factor of two either side of it.
  That is not error so much as geography: within one latitude the model puts a rain shadow behind
  every range, and Earth does the same — the Atacama and the Amazon are the same latitude.
- **A short world still works.** At `polarEquatorDistance` of 15 000 blocks — pole to equator in
  fifteen kilometres — the model tracked the bands to within 1.5 °C. The gradient being far steeper
  than any on Earth does not appear to trouble it.

Two things follow from turning this on:

- Heading north or south now changes the climate, and heading east or west mostly does not. A large
  `polarEquatorDistance` (200k, 400k) gives long belts and gentle travel; a small one (15k, 25k)
  puts the tropics and the ice within a day's walk of each other.
- The two hemispheres get opposite years, which they always should have: Vintage Story's calendar
  already flips the season south of the equator, and until 0.5 the mod's temperature curve did not
  follow it. `worldGen.seasonHemispheres` now defaults to on.

Set `worldGen.latitudeStrength` to 0 for the unrooted world the mod made before 0.5, where the cold
places are wherever the model drew them. Values in between walk from one to the other. Bands are
also off on a "Patchy" world, which has no latitude to speak of.

### Starting climate

Vanilla honours this setting by sliding its climate map until the band you picked covers the map
centre. With latitude bands on, so does this mod: the phase it reads from the game is that same
slide, so the map centre already sits at the right latitude and the spawn search only has to find
land there. It then surveys outward for land whose temperature is in the band, checks the likeliest
spots at full resolution, and puts the spawn on a column that really is in range — usually a few
hundred blocks away rather than a few thousand.

With `worldGen.latitudeStrength` at 0 there is no latitude to slide, and the search does all the
work: the model predicts one particular world rather than a climate field that can be shifted
about, so it hunts for a matching climate wherever the model happened to put one.

The five bands mean what they do in an unmodded world: hot 28–32 °C, warm 19–23 °C, temperate
6–14 °C, cool −5 to 1 °C, icy −15 to −10 °C, measured as annual mean temperature.

- Without latitude bands, cold is found on high ground rather than far north, so "icy" is often a
  nearby mountain rather than a long trek. With them, it is both.
- The search prefers to travel east or west. Distance along Z is what sets latitude in Vintage
  Story, and past the world's polar distance that buys midnight sun and polar night; distance along
  X costs nothing. With latitude bands on it rarely has to go far in either direction — on the test
  worlds it settled a few hundred blocks from the map centre.
- It stops at the first matching land it finds, so most worlds spawn within a few thousand blocks
  and the search takes under a second. A band that is genuinely far away — usually "hot" — can take
  a few seconds on a GPU and rather longer on CPU inference.
- If the seed has no such land within range, the server log says so and you spawn at the closest
  temperature it found.

Set `worldGen.startingClimateSearch` to false to spawn on the nearest land whatever its climate.

## What the mod changes

- **Terrain pass** — vanilla `GenTerra`'s chunk handler is swapped for one that fills columns from
  the diffusion heightmap. When another mod has already replaced terrain generation, the model
  supplies heights to *it* instead; see [Other terrain mods](#other-terrain-mods).
- **Climate map** — temperature and rainfall from the model, pre-compensated for the altitude
  corrections the game applies on read. The geologic activity byte is still vanilla's.
- **Global temperature and precipitation** — conditioning rather than post-processing, so the world
  is drawn at the climate asked for instead of being drawn temperate and rescaled.
- **Latitude** — the game's own latitude, from `polarEquatorDistance`, is conditioned into the model
  as a real zonal climate: equatorial rain belt, subtropical deserts, mid-latitude storm track, dry
  cold caps. Vanilla's straight line from +40 °C to −20 °C is replaced, but where the line runs is
  still the game's business.
- **Forest and shrub maps** — replaced with cover derived from the model's moisture and growing
  season.
- **Ocean map** — read, not written. It is what the terrain is conditioned on, so the world's land
  cover and ocean scale settings, or another mod's ocean map, decide where the sea goes.
- **Surface pass** — after vanilla's block layers, two things it cannot know about are fixed up:
  slopes too steep to hold soil are scoured back to bare rock (vanilla upholsters cliff faces in
  eight blocks of dirt), and ground whose warmest month never rises above freezing is capped with
  glacier ice.
- **Seasons** — temperature and rainfall swing through the year on the model's seasonality rather
  than on latitude alone, so a maritime coast and a continental interior in the same band get
  completely different years. Latitude still shows through, because a polar climate is a seasonal
  one: the model's own seasonality rises as the mean temperature falls. The two hemispheres get
  opposite years, matching the game's own calendar.
- **Nothing else.** In particular the landform and geological upheaval maps are still generated, and
  still ignored, because the generator that read them is gone. See
  [Two settings the mod cannot honour](#two-settings-the-mod-cannot-honour).
- **Spawn** — moved to solid ground in the world's chosen starting climate. Vanilla forces land at
  the map centre through the ocean map, and the terrain follows that, so on most worlds the search
  does not have to go far.
- **Surface block layer altitudes** — only when terrain is vertically exaggerated. Vanilla's bands
  are fractions of world height (bare mountain gravel above 0.66 of it) and assume a block is about
  a metre; at true scale that already lines up, so nothing is touched.

Everything else — rock strata, ores, caves, rivers, ponds, ruins, traders, temporal stability — is
vanilla, running unchanged on top.

## Other terrain mods

Mods that only supply a map — Continental World's ocean map, for instance — need nothing special:
the mod reads whatever map is installed and conditions the model on it.

A mod that *replaces terrain generation itself* is a different matter. Two generators filling the
same chunk column do not layer; the world comes out as the union of both landscapes with only one
mod's heightmaps recorded, and the surface block layers get buried under the other mod's stone.
There is only one arrangement that works, so that is the one the mod uses: whoever is generating
terrain gets handed the model's heights and does the filling.

**Algernon's Watersheds** is supported this way. Watersheds disables vanilla `GenTerra` and fills
every column itself, so with both mods installed this mod stops generating terrain and instead
answers every question Watersheds asks about the height of the ground: the height its whole
watershed analysis is built on, the height a stream's profile is laid out against, the height after
a stream has cut into it — which is what decides where the water surface and the banks go — and
which blocks of a column are solid. Its drainage basins are then solved on the model's continents,
its streams run down the valleys that are really there, and the carve depth it computed for a column
is applied to the modelled hillside. Everything downstream of that — stream water, banks, rapids,
groundwater, its block layer pass — is Watersheds' own, unchanged.

Answering *all* of those from the model is the whole trick, not a nicety. A stream's water surface
and the bed it lies in are worked out separately, so a single height left coming from Watersheds'
own landscape strands water in the air where that landscape stood higher and leaves the channel dry
below it. One consequence: Watersheds' ridge and gully erosion filter is switched off for these
worlds. It exists to cut valley detail into fractal noise, the model's landscape already has erosion
in it, and it is computed privately inside two of those height answers — so keeping it would put the
water and the bed back out of step.

Three things to expect:

- **World creation takes longer.** The watershed analysis samples heights over a far wider area than
  the chunks being generated — several kilometres around spawn — and every one of those samples has
  to come from the model. Expect the first load to spend a few minutes generating terrain tiles it
  will not visibly use yet. It is a one-time cost per area, and the tiles are cached.
- **Watersheds decides where streams go, on its own terms.** In particular it refuses to path a
  stream across terrain rougher than `SmallChunkRoughnessThreshold` in its
  `ModConfig/Watersheds/TerrainAnalysisConfig.json` (2 blocks of RMSE from a plane across a chunk,
  by default). Ordinary modelled landscape sits well inside that — a sample of chunks around a
  460 m plateau measured 0.0 to 0.8 — but genuinely broken ground will not get small streams, the
  same way it would not in an unmodified Watersheds world. Raise the threshold if you want them
  anyway.
- **Streams need somewhere to drain.** They path towards the sea, so a world generated at vanilla's
  default land cover has almost no ocean for them to reach and produces almost no streams. That is
  Watersheds' behaviour rather than this mod's, but it is worth knowing before concluding the two
  are not working together.

Watersheds keeps its stream maps in a database beside the save, so a world explored with an older
version of this mod has streams in it that were plotted against the wrong landscape. Clear them with
`/watersheds clearstreammaps` and regenerate the affected chunks, or start a new world.

If Watersheds updates in a way this cannot reach into, the mod says so in the log and on the loading
screen and takes itself out of the world entirely, leaving Watersheds' own terrain intact rather
than generating a broken one.

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
and altitude already in them — but nothing in the model knows which way is north, so left alone it
puts the cold places wherever its noise put them. The latitude bands supply the missing axis: the
model is conditioned on the zonal climate of the latitude the game says each row is at, and
everything above happens inside that. `globalTemperature` and `globalPrecipitation` are conditioning
on the same terms, applied to the bands themselves, so a "Snowball earth" world is one whose every
band is a quarter of the way up the scale, tropics and ice caps alike.

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
above the treeline are all new behaviour. The map goes on being read by everything that read it
before — trees, shrubs, ground patches, structure placement, creature spawning — so the animals and
the undergrowth follow the woods around.

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
| `status`             | Device, world scaling, tiles generated, average tile time, and where that time went: total model inference, its share of tile time, and a per-stage breakdown. A low inference share means something other than the GPU is the bottleneck. |
| `gpulimit [percent]` | The share of the time inference is allowed to keep the device busy, and how much has been given up to the limit so far. With a percentage, sets it there and now, and saves it. |
| `map`                | The debug map's address, and how many tiles it is holding. |
| `here`               | Elevation, slope, full bioclimate and derived cover where you stand, plus the latitude diagnostics below. |
| `season <x> <z>`     | The same diagnostics at a position, and the year's temperature and rainfall cycle there. Usable from a server console, where `here` is not. |
| `column <x> <z>`     | What actually got generated in a column, next to what the model said.   |

Every command prints one field per line. `here` and `season` share four for diagnosing the climate:

- **Latitude** and **Hemisphere** — how far from the equator the game puts that Z, which side of it,
  and the season the game's own calendar reports there. That season is the one every other system
  will think it is, so it is the thing to check if foliage or crops look out of step.
- **Sea-level temperature** — the same reading with the altitude taken back out, and the local lapse
  rate the model fitted. This is the number to compare two places by, because it has the mountain
  out of it. It costs a pipeline query rather than a tile lookup, so it is a little slower than the
  rest of the readout.
- **Band temperature** and **Band precipitation** — what the latitude band asked for here and how
  far this column sits from it, plus a **Band offset applied** line when some of the band had to be
  added after the model ran rather than conditioned into it.

A single column is expected to scatter several degrees either side of its band: the band is a
median over all the land in the belt, and everything that makes one place differ from another is
the model's business. Consistent drift over many columns is what would indicate something wrong.

## Configuration

`ModConfig/vsterraindiffusion.json`, written on first start. [CONFIG.md](CONFIG.md) is the whole
default file with a comment on every field; the tables below are the short version.

Install [ConfigLib](https://mods.vintagestory.at/configlib) and the same settings get an in-game
screen, every field below on it with its explanation. Nothing else changes: ConfigLib edits this
mod's own config file in place rather than keeping a copy, so the file and the screen are two views
of one thing and you can go on editing the file if you would rather. It is not a dependency — with
ConfigLib absent the mod neither needs nor notices it.

`gpuUtilizationPercent` and `verboseInference` take effect the moment they are saved. Everything
else is read when the world generator starts, so it takes a server restart, which is what the
screen's hover text says for each one.

**Useful range** is where the setting does something sensible, not where it is legal. Everything is
clamped to a wider range than this (CONFIG.md lists the hard limits) and nothing outside the useful
range is *forbidden* — it is just where the results stop being worth having.

### Inference

Machine settings. Safe to change at any time.

| Key                          | Default | Useful range | Meaning                                 |
| ---------------------------- | ------- | ------------ | ---------------------------------------- |
| `inferenceDevice`            | `auto`  | `auto` `cpu` `cuda` `directml` `coreml` | Leave on `auto` unless it picks wrong. |
| `offloadModels`              | false   | on / off     | Hold only one model on the GPU at a time, saving about 1 GB of VRAM. Generating a tile runs two or three of the models, so every tile then pays to rebuild a session for a graph of most of a gigabyte: measured on a 6 GB card it triples the average tile time. Turn on only if the models will not fit. |
| `gpuUtilizationPercent`      | 100     | 40 – 100     | Share of the time world generation may keep the device busy. Lower it if generating chunks makes the game stutter; see [Stuttering](#stuttering) below. World generation slows by the reciprocal. |
| `validateModelHashes`        | true    | on / off     | Verify SHA-256 of existing model files on startup. Off saves a few seconds of disk read. |
| `downloadRuntime`            | true    | on / off     | Fetch the ONNX Runtime native library automatically. |
| `tileCacheMegabytes`         | 256     | 128 – 1024   | Decoded tensor windows per pipeline stage. |
| `terrainTileCacheMegabytes`  | 256     | 128 – 1024   | Finished terrain tiles. Raise if you see thrash warnings. |
| `terrainTileSizeBlocks`      | 256     | 128 – 512    | Blocks generated per model invocation, a multiple of 32. Larger amortises the model better but wastes more work at the edges of what is being generated. |
| `debugMapPort`               | 0 (off) | 8088         | Serves the [debug map](#debug-map) on this port. 0 opens no port. |
| `debugMapBindAddress`        | `127.0.0.1` | loopback | Where the debug map listens. `0.0.0.0` publishes your world's terrain to the network. |
| `debugMapHistoryTiles`       | 2048    | 512 – 8192   | Tiles the debug map remembers, about 8 KB each. |
| `verboseInference`           | false   | on / off     | Log every terrain tile at notification level. Noisy; for diagnosing slowness. Off, those lines still go to the debug log and only a tile that stalls — a second or more, and four times the session average — reaches the main one. |

#### Stuttering

In single player the model runs on the same GPU the game renders with, and world generation submits
work to it in long unbroken stretches. A graph that has been submitted runs to completion — nothing
can preempt it — so the renderer's own work queues behind it and a burst of chunk generation reads
as a freeze, even though the game thread is not blocked at all. It is worst on a card that is only
just fast enough for both jobs.

`gpuUtilizationPercent` is the lever. Below 100 the generator idles after each model run for long
enough to hold the device to that share, so the pattern becomes run, wait, run, wait instead of one
solid block of compute, and the renderer gets regular windows to put a frame out. It cannot make an
individual model run shorter, so it reduces stutter rather than removing it, and world generation
slows by the reciprocal: at 50% a terrain tile takes about twice as long, at 25% about four times.

Measured on a 6 GB laptop card at 40%: the device came out at exactly 40% busy and a terrain tile
went from 142 ms to 323 ms. Total inference time in `/tdiff status` also rises — 14.7 s to 16.5 s
here, almost all of it on the shortest of the three models — because a card that keeps going idle
drops its clocks between runs. That is the cost of the idle windows, not a sign of anything wrong.

Start at 50 and go down only as far as the stutter actually needs — a value too low leaves world
generation unable to keep up with a walking player, which is its own kind of stutter. `/tdiff
gpulimit <percent>` changes it without a restart, so you can watch your frame rate and tune it in
place. On a dedicated server there are no frames to protect and the setting is only a way of leaving
the card to something else; leave it at 100 unless you have a reason.

#### Debug map

Set `debugMapPort` and the mod serves a small read-only page showing what the model is actually
producing, updating as tiles are generated. `/tdiff map` prints the address; the default binding is
loopback, so only the machine running the server can reach it.

Eight layers, switchable without refetching: surface height in blocks, model elevation in metres,
slope, mean temperature, temperature seasonality, annual precipitation, precipitation seasonality,
and the 0–255 rainfall byte the game itself reads. Drag to pan, wheel to zoom, and hover any column
for all eight values at once. The colour scale fits whatever is loaded and is shown with its ends.

It keeps its own record rather than reading the generator's tile cache, because that cache is an
LRU sized for world generation and drops a tile as soon as the generator has moved on — which is
exactly when you want to look at it. Each tile is stored as a 32×32 thumbnail quantised to a byte
per column per layer, about 8 KB, so the default 2048-tile history costs some 17 MB.

The port is off by default and nothing on it accepts input. Point it at `0.0.0.0` only if you mean
to publish your world's terrain and climate to the network; the server logs a warning if you do.

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

Two notes on the tables above.

**`rainfallBias`** exists because the climate map cancels Vintage Story's own "higher ground is
wetter" bonus — the model already does orography properly — while vanilla's biome thresholds were
tuned with that bonus present. The default puts its average back.

**`forestDensityMultiplier` is squared on its way to the ground.** The mod writes a 0–255 forest
byte; vanilla then draws a pool of candidate tree positions from the *climate*, and accepts each one
with probability `(byte / 255)²`. So 1.0 → 1.4 is roughly double the trees, not 40% more, and the
setting bites hardest where cover is already low — it turns scrub into open woodland long before it
turns forest into rainforest. Two other things follow: the byte saturates at 255, so anything much
above 1.2 mostly flattens the wet end; and 0 does not give a bare world, because the acceptance
probability has a floor of 0.0025 that still scatters the odd lone tree. For a genuinely treeless
world use the world's own "Forestation & shrubs" setting at −100%.

The two forest controls are worth telling apart. "Forestation & shrubs" is *additive* — it shifts
the byte up or down everywhere, lifting deserts as much as forests. `forestDensityMultiplier` is
*proportional* — it preserves the climate pattern and scales the contrast. Both still apply, the
world setting on top of the mod's byte.

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
