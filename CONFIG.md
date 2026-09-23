# Mod configuration reference

The mod writes `ModConfig/vsterraindiffusion.json` inside your Vintage Story data folder the first
time it runs, and rewrites it on every start with any missing keys filled in and any out-of-range
values pulled back into range. A single player world exposes the four world settings on the creation
screen as well; everything else lives in this file.

With [ConfigLib](https://mods.vintagestory.at/configlib) installed the same file gets an in-game
settings screen, with every field below on it. ConfigLib edits this file in place rather than
keeping one of its own, so the two ways of setting things stay the same thing. Only
`GpuUtilizationPercent` and `VerboseInference` take effect the moment they are saved; the rest are
read when the world generator starts. `InferenceDevice` is the exception: the native runtime loads
once per process, so it needs the game restarted, not just a new world.

The listing below is the file exactly as the mod generates it, with a comment on every field. **JSON
does not allow comments** — copy values out of it, do not paste the whole thing over your config.
[Ranges](#ranges) at the end gives every numeric field's hard limit, the narrower range worth
staying inside, and its default.

Settings under `WorldGen`, the inference device, and the three model precisions can change what the
world looks like. OpenVINO, TensorRT RTX and every precision other than FP32 must be selected
explicitly; keep those machine settings fixed after exploration so new chunks do not disagree
slightly with the ones already on disk.

At startup the mod checks which inference devices and precisions this machine can run and logs the
result. The ConfigLib screen offers only those. A config naming one this machine cannot run, or an
unrecognised value, stops the game with the reason in the log. The mod never changes the device or a
precision itself; all three precisions default to `fp32`.

| device | runs when |
|---|---|
| `auto`, `cpu` | always (Windows or Linux on x64 or arm64, macOS on Apple silicon; ONNX Runtime 1.24.4 has no Intel Mac build) |
| `openvino` | 64-bit Linux, CPU with SSE4.2 |
| `cuda` | 64-bit Windows or Linux, NVIDIA GPU and driver supporting the CUDA build in use (12: compute 5.0+, driver 525+; 13: compute 7.5+, driver 580+). Linux also needs the CUDA toolkit and cuDNN 9 installed |
| `tensorrt-rtx` | 64-bit Windows or Linux, compute capability 8.6, 8.9, 12.0 or 12.1, driver 580+ |
| `directml` | Windows 10 1903+, Direct3D 12 GPU |
| `coreml` | macOS 10.15+ |

| precision | runs when |
|---|---|
| `fp32` | always |
| `fp16` | at least one GPU device above can run |
| `int8` (decoder) | always |

```jsonc
{
  // Which execution provider runs the model: "auto", "cpu", "openvino", "cuda", "tensorrt-rtx",
  // "directml" or "coreml". OpenVINO can accelerate the decoder on 64-bit Linux CPUs; the coarse
  // and base stages remain on ONNX Runtime CPU to keep memory use predictable. It runs in an
  // isolated helper and falls back to ONNX Runtime CPU if the native compiler is not usable on the
  // host. TensorRT RTX needs a GeForce RTX 30xx or newer on 64-bit Windows or Linux, downloads the
  // NVIDIA runtime once (105 MB on Windows, 140 MB on Linux), builds an engine per model on first
  // start (seconds, then cached under TerrainDiffusionModels/onnx-cache/tensorrt-rtx). A device
  // this machine cannot run stops the game at startup. A compatible device whose runtime cannot be
  // prepared (a failed download, a missing file) runs that session on what "auto" would pick, or
  // the CPU, and is logged; this file is not changed, so the next start tries the selected device
  // again. When that replacement needs a different ONNX Runtime than the session already loaded,
  // the game stops instead. Only what the selected provider actually needs is downloaded:
  // TensorRT RTX does not fetch the CUDA provider library it never loads, and CUDA on Windows fetches the cuBLAS, cuFFT,
  // NVRTC and cuDNN libraries it links against (about 1 GB, once) only when the machine does not
  // already have them, which it will if a CUDA toolkit and cuDNN are installed. Linux and macOS
  // use the system CUDA install. OpenVINO and TensorRT RTX are opt-in: "auto" never selects them.
  // Auto picks CoreML on macOS, DirectML on Windows, CUDA on Linux when this machine can run it,
  // and CPU everywhere else.
  "InferenceDevice": "auto",

  // Where ONNX Runtime loads model graphs from: "memory", "file", or "auto". Memory makes GPU
  // model switching faster. File uses about 1 GB less RAM with the current optimised models.
  // Auto uses files for CPU inference, resident GPU sessions, and GPU hosts with less than 8 GB
  // available; otherwise it keeps the graphs in memory.
  "ModelLoadMode": "auto",

  // Keep only one of the three models resident on the GPU at a time, holding peak VRAM near
  // 1.5 GB instead of about 2.5 GB. Generating a single terrain tile runs the latent model and the
  // decoder, so with this on every tile pays to rebuild a session for a graph of most of a
  // gigabyte: measured on a 6 GB card it triples the average tile time, 66 ms to 197 ms. Turn it
  // on only if the models will not fit on the card alongside everything else.
  "OffloadModels": false,

  // Share of the time, as a percentage, that world generation may keep the inference device busy.
  // 100 is unlimited. Lower this if generating chunks makes the game stutter: the model runs on the
  // same GPU the game renders with, and a graph that has been submitted runs to completion, so the
  // only lever is how often one is submitted. After each model run the generator idles for long
  // enough to hold the device to this share, which leaves the renderer regular windows to get a
  // frame out. World generation slows by the reciprocal - at 50% a terrain tile takes about twice
  // as long. Changeable while the server runs, with /tdiff gpulimit.
  "GpuUtilizationPercent": 100,

  // Check the SHA-256 of model files that are already on disk at every startup. Turning this off
  // saves a few seconds of hashing per start; file sizes are still checked.
  "ValidateModelHashes": true,

  // Download the matching ONNX Runtime and, when selected, OpenVINO native libraries
  // automatically. On 64-bit Windows this includes the Visual C++ runtime (6.8 MB, once, from
  // Microsoft) when the machine's own is missing or older than 14.39. Turn off to supply them
  // yourself under TerrainDiffusionModels/onnxruntime/, and install the Visual C++ Redistributable.
  "DownloadRuntime": true,

  // Decoder model precision: "fp32", "fp16" or "int8". The matching decoder is downloaded
  // automatically; the others are not required. FP16 is for GPU providers, INT8 for the CPU and
  // OpenVINO. Neither is ever selected automatically because both change newly generated terrain
  // slightly. If the selected decoder cannot be downloaded or verified, loading stops instead of
  // silently changing precision.
  "DecoderPrecision": "fp32",

  // Base (latent) model precision: "fp32" or "fp16". The base model is most of a tile's work, so
  // this is the GPU speed setting. On an RTX 3060 over ten 128x128 regions: CUDA FP32 19.0 s,
  // TensorRT RTX FP32 12.5 s (~0.3 m elevation difference), TensorRT RTX with FP16 base and
  // decoder 7.6 s (~4 m mean, 23 m worst) — against ~2.6 m for the same world on a CPU rather than
  // this GPU. Needs a GPU provider: ORT's CPU kernels widen fp16 back to float, which is slower
  // than FP32 and still changes the terrain. The mod warns if you ask for that.
  "BasePrecision": "fp32",

  // Coarse model precision: "fp32" or "fp16". Worth its own setting because the trade is poor:
  // the coarse sampler runs twenty steps per tile and compounds small differences, so on the same
  // bench FP16 here saved 0.3 s and moved elevation a further 2 m. FP32 unless you measure better.
  "CoarsePrecision": "fp32",

  // Total megabytes of decoded tensor windows kept across all pipeline stages.
  "TileCacheMegabytes": 256,

  // Number of latent windows sent through the base model together. Zero chooses one on CPU and
  // four on GPU. Larger batches improve GPU utilisation but need more working memory.
  "LatentBatchSize": 0,

  // Megabytes of finished terrain tiles to keep. This has to cover everything world generation
  // touches at once - a spawn area alone can span a hundred tiles - or tiles get evicted while
  // still in use and have to be rebuilt from scratch.
  "TerrainTileCacheMegabytes": 256,

  // Side length, in blocks, of the terrain generated per model query. Zero chooses 128 on CPU and
  // 256 on GPU. Larger values spread the model's latency over more chunks at the cost of a longer
  // stall on first visit. Explicit values are rounded down to a multiple of 32 and clamped to
  // 64-1024.
  "TerrainTileSizeBlocks": 0,

  // Port for the debug map: a small read-only web page showing the model's heightmap and climate
  // maps as tiles are generated, with a layer picker, a pannable view and a per-column readout.
  // 0, the default, opens no port at all. See /tdiff map for the address once it is running.
  "DebugMapPort": 0,

  // Address the debug map listens on. Loopback by default, so only this machine can reach it.
  // "0.0.0.0" exposes the world's terrain and climate to anything that can reach the port; the
  // server logs a warning if you do.
  "DebugMapBindAddress": "127.0.0.1",

  // How many generated tiles the debug map remembers, at about 8 KB each; the oldest are dropped
  // past this. It keeps its own record because the generator's tile cache drops a tile as soon as
  // it has moved on, which is exactly when you want to look at it.
  "DebugMapHistoryTiles": 2048,

  // Log a line at notification level for every terrain tile generated. Very noisy; useful when
  // profiling. With this off those lines still go to the debug log, and the main log gets one only
  // when a tile takes at least a second and at least four times the session's average - a stall
  // worth explaining, rather than the model doing its job.
  "VerboseInference": false,

  "WorldGen": {

    // ---- Height and scale -------------------------------------------------------------------

    // How elevation in metres becomes blocks of height.
    //   "isotropic" - a block is as tall as it is wide, so the landscape is at true scale in
    //                 every direction. This is the default and what the Minecraft mod does.
    //   "manual"    - use MetersPerBlockVertical below.
    //   "auto"      - measure the region's peaks once per world and stretch the terrain to fill
    //                 the world height. Suits short worlds, at the cost of exaggerated relief.
    "HeightMode": "isotropic",

    // manual: metres of elevation per block of height. Zero falls back to isotropic.
    "MetersPerBlockVertical": 0.0,

    // ---- Height calibration (only used when HeightMode is "auto") ---------------------------

    // How much of the space between sea level and the world ceiling the region's tall peaks
    // should occupy. Leave a little room, or the summits flatten against the ceiling.
    "TargetPeakFillFraction": 0.92,

    // Which elevation quantile counts as a "tall peak". 0.995 means the top half percent of the
    // surveyed area reaches the ceiling; lowering it makes the whole landscape taller and clips
    // more summits.
    "PeakQuantile": 0.995,

    // Half-width, in blocks, of the area surveyed around spawn. This should cover the part of the
    // world you expect to explore: surveying a whole continent lets a distant mountain range
    // decide the scale and leaves your own surroundings flat.
    "CalibrationRadiusBlocks": 4096,

    // How many full-detail probes to run on the tallest surveyed cells. The survey itself only
    // sees terrain averaged over several kilometres, so peaks need measuring at full resolution.
    // Each probe costs about as much as one terrain tile, once per world. Zero skips probing and
    // falls back to ReliefFactor.
    "CalibrationProbes": 8,

    // Assumed ratio of true peak height to the coarse survey's value, used when probing is
    // disabled or fails.
    "ReliefFactor": 1.6,

    // Bounds on the vertical exaggeration calibration is allowed to choose.
    "MinAutoExaggeration": 1.0,
    "MaxAutoExaggeration": 20.0,

    // ---- Terrain shape ----------------------------------------------------------------------

    // Fraction of the available height mapped perfectly linearly. Above the knee the curve bends
    // over so that arbitrarily tall model peaks still fit under the ceiling; the closer this is
    // to 1 the more faithful the summits and the harder they clip.
    "LinearKneeFraction": 0.85,

    // Fraction of the space below sea level that the deepest ocean reaches. It scales the whole
    // sea-floor curve except its shallow end, which is pinned to the waterline so that the first
    // column past the shore is one block of water at any world height.
    "OceanDepthFraction": 0.9,

    // Multiplies the Perlin detail added to sloped ground. The model resolves features down to
    // one native pixel, so hillsides need roughness of their own; raise for craggier slopes.
    "SlopeDetailStrength": 1.0,

    // How many times finer the vertical scale is at sea level than at the top of the world. At 3
    // and 15 m a block, a block is 5 m tall at the waterline, rising linearly to 15 m at the
    // ceiling, so low plains keep their relief instead of collapsing into one row at the water.
    // Mountains keep the coarse scale, but the world holds less height: at 512 tall, terrain is
    // to scale up to 2272 m instead of 3685 m. 1 is a uniform scale. Recorded in a world when it
    // is created; existing worlds keep the scale they were generated with.
    "LowlandDetail": 3.0,

    // ---- Climate and vegetation --------------------------------------------------------------

    // What the game's 0-255 rainfall byte is built from.
    //   "moisture"      - the model's aridity: precipitation measured against how much the
    //                     climate can evaporate, discounted for a dry season. This is what
    //                     actually decides whether ground is bare, and it stops warm-but-rainy
    //                     and cold-but-dry places from reading as the same.
    //   "precipitation" - annual millimetres alone.
    "RainfallBasis": "moisture",

    // moisture: the tree-moisture value that maps to the middle of the rainfall scale. The
    // default is the measured median over the model's land.
    "MoistureMedian": 0.62,

    // moisture: spread of log tree-moisture across the model's land.
    "MoistureSpread": 1.0,

    // precipitation: annual millimetres that map to the middle of the rainfall scale.
    "RainfallMedianMm": 540.0,

    // precipitation: spread of the model's log precipitation over land.
    "RainfallSpread": 0.8,

    // Added to the final rainfall as a fraction of full scale. Zero leaves the model's own answer
    // alone; raise for a wetter world, lower for a drier one.
    //
    // The climate map cancels Vintage Story's own "higher ground is wetter" bonus, because the
    // model already handles orography properly, and this used to default to 0.05 to put that
    // bonus's average back. It read as a world too lush everywhere, so the compensation is now
    // opt-in.
    //
    // Reach for this before "MoistureMedian": it moves every column by the same amount, where the
    // moisture calibration shifts a log-normal and so bites hardest where it is already dry.
    "RainfallBias": 0.0,

    // Degrees Celsius added to every model temperature, for a warmer or colder world.
    "TemperatureOffsetC": 0.0,

    // Scales the forest cover the model's moisture implies. Vintage Story's own forest map is
    // noise with no climate signal at all, so this replaces it outright; raise for denser woods.
    //
    // TREES ON THE GROUND GO AS THE SQUARE OF THIS. The mod writes a 0-255 forest byte; vanilla
    // draws candidate tree positions from the climate and accepts each with probability
    // (byte / 255) squared. So 1.0 -> 1.4 is roughly double the trees, not 40% more. Two corollaries:
    // the byte saturates at 255, so much above 1.2 only flattens the wet end while still lifting
    // dry ground; and 0 does not give a bare world, because that acceptance probability has a floor
    // of 0.0025 which still scatters the odd lone tree. For no trees at all, use the world's own
    // "Forestation & shrubs" setting at -100%.
    //
    // That world setting is additive where this is proportional: it shifts every place by the same
    // amount, deserts included, while this preserves the climate pattern and scales the contrast.
    // Both apply, the world setting on top of this one.
    //
    // Water and ground too steep to hold soil are cut to zero before this is applied, so it cannot
    // put woods on a cliff or the sea.
    "ForestDensityMultiplier": 1.0,

    // Scales shrub cover the same way, with the same squaring.
    "ShrubDensityMultiplier": 1.0,

    // ---- Seasons -------------------------------------------------------------------------------

    // Swing temperature through the year using the model's temperature seasonality (BIO4) rather
    // than from latitude alone, which is all vanilla has to go on. Continental interiors then get
    // hard winters and hot summers while maritime and tropical climates at the same latitude stay
    // even. Latitude still shows through, because a polar climate is a strongly seasonal one.
    "SeasonalTemperature": true,

    // Multiplies the modelled seasonal temperature swing. Zero gives a world with no seasons.
    "SeasonalTemperatureStrength": 1.0,

    // Swing rainfall through the year using the model's precipitation seasonality, so monsoon
    // climates get a real wet and dry season rather than drizzling evenly all year.
    "SeasonalPrecipitation": true,

    // Multiplies the modelled wet/dry season contrast.
    "SeasonalPrecipitationStrength": 1.0,

    // Least share of a place's yearly rainfall the dry season falls to. A safety rail rather
    // than a tuning knob: at the default strength it only binds where the model's seasonality is
    // extreme (BIO15 above roughly 100%). 0 removes it, and those places then go months with no
    // rain at all, which stalls unirrigated crops until the rains return.
    "SeasonalPrecipitationFloor": 0.4,

    // How far the land is lowered where the Rivers mod runs a river, from 0 to 1. Does nothing
    // without that mod.
    //
    // Rivers are routed from the world's ocean map rather than from terrain height, so they can be
    // worked out before the landscape and the model told where they will be - it then puts a
    // valley there instead of a ridge for one to be cut through afterwards.
    //
    // Measured against ~505 m either side of a river corridor: 0.3 brings it to 334 m (a 28%
    // drop), 0.5 to 251 m (45%), 1.0 to 28 m but drowns a fifth of the corridor and turns rivers
    // into sea inlets.
    //
    // The basin reaches a full coarse cell either side of a river, which at Rivers' own default
    // density covers roughly a third of the world - hence 0.3 rather than 0.5, so that ground
    // reads as river valleys instead of a plain. For fewer, deeper valleys, raise this and lower
    // "riverSpawnChance" in Rivers' own config.
    "RiverBasinDepth": 0.3,

    // ---- Translocators -------------------------------------------------------------------------

    // How long a translocator may wait for a chunk peek that never came back before starting its
    // search again, in seconds. 0 turns the watchdog off.
    //
    // A repaired translocator hunts for its partner four times a second by generating nine chunk
    // columns somewhere up to 8000 blocks away and throwing them away again. The server abandons
    // one of those if it cannot pause every worldgen thread within 3.6 seconds - which a thread
    // waiting on the model can easily exceed - and the translocator has already cleared the flag
    // that would make it try again, so it stays on "Warping spacetime..." until its chunk is
    // unloaded and read back from disk.
    "TranslocatorSearchTimeoutSeconds": 120,

    // How long the server may wait for terrain generation to stop before inspecting a candidate
    // location, in seconds. 0 keeps the game's own 3.6.
    //
    // Worldgen threads only come to rest between chunk columns, and a column here waits on the
    // model behind a single inference gate, so several queued threads routinely take longer than
    // 3.6 s. The server then discards the peek - after generating its terrain - and the search
    // starts over. Nothing can cut a column short, so waiting is the only option left.
    "TranslocatorPeekPauseSeconds": 30,

    // Caps how far a translocator looks for its partner, in blocks. 0 keeps the game's own 8000.
    //
    // A smaller ring searches ground that is more likely to be generated and still cached, so it
    // links sooner, but its hops are shorter and it changes which translocator links to which.
    // Links a world has already made are kept.
    "TranslocatorMaxRangeBlocks": 0,

    // Swing the year the opposite way south of the equator. On by default, and not really
    // optional: Vintage Story already does this. Its calendar takes the hemisphere from the sign of
    // the same latitude this mod reads, and shifts the year half a turn for the southern one, which
    // is what decides foliage, crop growth and everything else that asks what season it is. Off,
    // the southern hemisphere gets leaves that fall in the spring, because only the temperature
    // curve stayed northern. There is no world it is right to turn off: the game's hemisphere does
    // not depend on LatitudeStrength, and on a "Patchy" world it reports one hemisphere everywhere
    // so the setting does nothing anyway.
    "SeasonHemispheres": true,

    // ---- Surface ---------------------------------------------------------------------------

    // Stretch the altitude bands of vanilla's surface block layers to match the terrain height,
    // so that hills which are only tall because of vertical exaggeration are not surfaced as bare
    // alpine gravel. Has no effect at isotropic scale, where the bands already line up.
    "RescaleBlockLayerAltitudes": true,

    // Leave slopes too steep to hold soil as bare rock. The threshold comes from the model's own
    // moisture, because roots are what keep a hillside from shedding its soil.
    "BareSlopeRock": true,

    // Cap ground whose warmest month never rises above freezing with glacier ice, so ice fields
    // look permanent rather than like a winter that has not melted yet.
    "GlacierIce": true,

    // ---- Coastlines ----------------------------------------------------------------------------

    // Which way the world's ocean map and the model's terrain are made to agree.
    //
    // "input" conditions the model on the ocean map, so the world's "Land cover" and "Ocean scale"
    // settings decide where the sea is and the model decides what the coast, the shelf and the
    // mountains behind them look like. Because it reads whatever ocean map is installed rather than
    // vanilla's in particular, a mod that supplies its own — Continental World, say — is honoured
    // on the same terms, and the map itself is left alone for everything else that reads it.
    //
    // "output" is the reverse: the model invents its own continents from real-world terrain and the
    // ocean map is rewritten to match, ignoring the world settings and overwriting any other mod's.
    "OceanMap": "input",

    // input: how completely the ocean map overrides the model's own sense of where land belongs,
    // from 0 (ignored) to 1. Below 1 the map biases the coastline rather than setting it.
    "LandmaskStrength": 1.0,

    // input: how much noise the model is told the landmask carries. LOWER BINDS IT MORE TIGHTLY —
    // it is mixed as cos(atan(n)) conditioning against sin(atan(n)) noise, so it runs the opposite
    // way to the name cond_snr it has in the model's own config. The model ships 0.5, which
    // reproduces the ocean map over about 88% of the world; 0.1 gets that to 95%. Below 0.1 the
    // gain is under 2% and the conditioning starts flattening the land it does keep. Zero uses the
    // model's own value.
    "LandmaskNoiseLevel": 0.1,

    // ---- Global climate --------------------------------------------------------------------------

    // How much of the world's "Global temperature" and "Global precipitation" settings is built into
    // the climate the model is conditioned on, from 0 to 1. The rest is applied to the model's output
    // afterwards, so the world reads the same either way; what changes is whether the model knew. At
    // 1 an arid world is drawn as an arid world, with the drainage, vegetation and soils to match; at
    // 0 it is a temperate world with its rainfall scaled down on the way out, which is what this mod
    // used to do and what vanilla does.
    "GlobalClimateStrength": 1.0,

    // How much noise the model is told the shifted climate carries. LOWER BINDS IT MORE TIGHTLY, on
    // the same inverted scale as LandmaskNoiseLevel. Only consulted when one of the two settings is
    // off its default, so an ordinary world keeps the model's own climate character. Zero uses the
    // model's own value for every world, and is the default: unlike the landmask, the climate
    // conditioning already tracks what it is asked for closely at the model's own setting.
    "ClimateNoiseLevel": 0.0,

    // ---- Latitude ------------------------------------------------------------------------------

    // How much of a north-south climate gradient the world gets, from 0 to 1.
    //
    // The model's climate is a real climatology - continents, maritime coasts, rain shadows,
    // altitude - but nothing in it knows which way is north, so left alone it puts the cold places
    // wherever its noise put them and the world's "polarEquatorDistance" means nothing. At 1 the
    // equator, the subtropical deserts, the mid-latitude storm track and the ice caps are
    // conditioned into the model at the latitudes the game says they belong, and everything the
    // model knows about coasts and mountains happens WITHIN those bands. At 0 the world is
    // unrooted, which is what the mod did before 0.5.
    //
    // Which block is at which latitude is read from Vintage Story, not invented here, so day
    // length, midnight sun and the hemispheres all agree with the snow line for free.
    "LatitudeStrength": 1.0,

    // ---- Spawn ---------------------------------------------------------------------------------

    // Honour the world's "Starting climate" setting by placing the spawn on land whose modelled
    // temperature falls in the chosen band (hot 28-32 C, warm 19-23, temperate 6-14, cool -5 to 1,
    // icy -15 to -10). Vanilla implements that setting by shifting its own climate map, which
    // cannot be done to a model that predicts a specific world, so the player moves instead.
    // False spawns you on the nearest land whatever its climate.
    "StartingClimateSearch": true,

    // How far from the middle of the map the search may look, in blocks. It stops as soon as it
    // finds matching land, so this is only the point at which it gives up and takes the closest
    // temperature it saw. Searching the full radius costs a few seconds once per world.
    "StartingClimateSearchRadiusBlocks": 65536,

    // How much more reluctant the search is to move the spawn north or south than east or west.
    // Distance along Z decides latitude in Vintage Story, and past the world's polar distance it
    // buys midnight sun and polar night; distance along X costs nothing at all. At 2 the search
    // will go twice as far east for the same climate before it heads for a pole.
    "StartingClimateNorthSouthCost": 2.0,

    // ---- World creation overrides ------------------------------------------------------------
    // These three mirror settings on the world creation screen. A dedicated server has no such
    // screen, so this is where you set them.

    // How much of the climate the model drives: "full" for model temperature, rainfall and
    // vegetation with no latitude bands, "off" to leave Vintage Story's climate alone. Empty uses
    // the world's own setting, which also defaults to full.
    "ClimateMode": "",

    // Overrides the world's "Diffusion resolution" setting. It is a divisor of the model's native
    // 30 m pixel, so 1 is 30 m per block, 2 (the default) is 15 m, 4 is 7.5 m. Zero uses the
    // world setting; values above 6 are only reachable from here. Finer costs generation time and
    // shrinks the world you can walk across; the climate map no longer limits it.
    "ScaleOverride": 0,

    // Overrides the world's "Vertical exaggeration" setting. Zero uses the world setting. In auto
    // height mode this multiplies the calibrated height rather than setting it outright.
    "VerticalExaggerationOverride": 0.0
  }
}
```

## Ranges

**Clamped to** is the hard limit: anything outside it is pulled back on load, and a value that is
not a number at all is replaced with the default. **Useful** is the narrower range where the setting
does something worth having — nothing outside it is forbidden, and the two differ because the clamp
only has to stop the mod breaking, not stop the world looking silly.

| Field | Clamped to | Useful | Default |
| --- | --- | --- | --- |
| `GpuUtilizationPercent` | 5 – 100 | 40 – 100 | 100 |
| `DebugMapPort` | 0, or 1024 – 65535 | 8088 | 0 (off) |
| `DebugMapHistoryTiles` | 64 – 65536 | 512 – 8192 | 2048 |
| `TileCacheMegabytes`, `TerrainTileCacheMegabytes` | 32 – 4096 | 128 – 1024 | 256 |
| `LatentBatchSize` | 0 – 16 | 0 – 4 | 0 |
| `TerrainTileSizeBlocks` | 0, or 64 – 1024 rounded down to a multiple of 32 | 0, or 128 – 512 | 0 |
| `TargetPeakFillFraction` | 0.2 – 1 | 0.8 – 0.95 | 0.92 |
| `PeakQuantile` | 0.5 – 1 | 0.99 – 0.999 | 0.995 |
| `CalibrationRadiusBlocks` | 512 – 4 000 000 | 2048 – 16384 | 4096 |
| `CalibrationProbes` | 0 – 64 | 4 – 16 | 8 |
| `ReliefFactor` | 1 – 5 | 1.3 – 2 | 1.6 |
| `MinAutoExaggeration`, `MaxAutoExaggeration` | 0.05 – 100 (max is raised to min if lower) | 1 – 4, 4 – 30 | 1, 20 |
| `MetersPerBlockVertical` | 0 or greater | 5 – 30 | 0 |
| `LinearKneeFraction` | 0.1 – 0.99 | 0.7 – 0.95 | 0.85 |
| `OceanDepthFraction` | 0.05 – 1 | 0.6 – 1 | 0.9 |
| `SlopeDetailStrength` | 0 – 8 | 0.5 – 2 | 1 |
| `LowlandDetail` | 1 – 8 | 1 – 4 | 3 |
| `MoistureMedian` | 0.01 – 100 | 0.4 – 0.9 | 0.62 |
| `MoistureSpread`, `RainfallSpread` | 0.1 – 4 | 0.7 – 1.4, 0.6 – 1.2 | 1, 0.8 |
| `RainfallMedianMm` | 10 – 10 000 | 300 – 900 | 540 |
| `RainfallBias` | -1 – 1 | -0.1 – 0.1 | 0 |
| `TemperatureOffsetC` | -40 – 40 | -5 – 5 | 0 |
| `ForestDensityMultiplier` | 0 – 4 | 0.7 – 1.5 (squared on the ground) | 1 |
| `ShrubDensityMultiplier` | 0 – 4 | 0.5 – 2 (squared on the ground) | 1 |
| `SeasonalTemperatureStrength`, `SeasonalPrecipitationStrength` | 0 – 4 | 0.5 – 1.5 | 1 |
| `SeasonalPrecipitationFloor` | 0 – 1 | 0.3 – 0.6 | 0.4 |
| `RiverBasinDepth` | 0 – 1 | 0.2 – 0.5 | 0.3 |
| `TranslocatorSearchTimeoutSeconds` | 0 – 1800 | 90 – 300 | 120 |
| `TranslocatorPeekPauseSeconds` | 0 – 120 | 20 – 60 | 30 |
| `TranslocatorMaxRangeBlocks` | 0, or up to 8000 | 0, or 1500 – 3000 | 0 |
| `LandmaskStrength` | 0 – 1 | 0.8 – 1 | 1 |
| `LandmaskNoiseLevel` | 0, or 0.01 – 8 | 0.05 – 0.5 | 0.1 |
| `GlobalClimateStrength` | 0 – 1 | 0.5 – 1 | 1 |
| `ClimateNoiseLevel` | 0, or 0.01 – 8 | 0, or 0.1 – 0.5 | 0 |
| `LatitudeStrength` | 0 – 1 | 0, or 0.5 – 1 | 1 |
| `StartingClimateSearchRadiusBlocks` | 512 – 4 000 000 | 16384 – 262144 | 65536 |
| `StartingClimateNorthSouthCost` | 1 – 100 | 1 – 4 | 2 |
| `ScaleOverride` | 0, or 1 – 16 | 0, or 1 – 6 | 0 |
| `VerticalExaggerationOverride` | 0, or 0.05 – 20 | 0, or 0.5 – 2 | 0 |

An unrecognised `ModelLoadMode`, `HeightMode`, `RainfallBasis`, `OceanMap` or `ClimateMode` falls
back to its default. An unrecognised or incompatible `InferenceDevice`, `CoarsePrecision`,
`BasePrecision` or `DecoderPrecision` stops the game.

Only the selected models are downloaded, into `TerrainDiffusionModels/`:

| setting | file |
|---|---|
| `decoderPrecision: "int8"` | `decoder_model.int8.onnx` |
| `decoderPrecision: "fp16"` | `decoder_model.fp16.256.onnx` |
| `basePrecision: "fp16"` | `base_model.fp16.onnx` |
| `coarsePrecision: "fp16"` | `coarse_model.fp16.onnx` |

The decoder's half-precision file carries a window size because a decoder graph is exported for one
height and width and loads at no other. 256x256 is fixed in the pipeline; `TerrainTileSizeBlocks`
changes how many windows run per tile, not their shape.

Recipes are under `scripts/`; each model is built and published by a workflow in
`.github/workflows/` that verifies its exact size and SHA-256. Keep
`InferenceDevice` and the three precision settings fixed for an established world: changing any of
them can introduce small numerical differences in newly generated terrain at chunk boundaries.
