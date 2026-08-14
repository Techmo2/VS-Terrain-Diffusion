# Mod configuration reference

The mod writes `ModConfig/vsterraindiffusion.json` inside your Vintage Story data folder the first
time it runs, and rewrites it on every start with any missing keys filled in and any out-of-range
values pulled back into range. Editing the file is the only way to reach these settings on a
dedicated server; a single player world exposes the four world settings on the creation screen as
well.

The listing below is the file exactly as the mod generates it, with a comment on every field. **JSON
does not allow comments** — copy values out of it, do not paste the whole thing over your config.

Settings under `WorldGen` change what the world looks like. Editing them after a world has been
explored makes new chunks disagree with the ones already on disk.

```jsonc
{
  // Which execution provider runs the model: "auto", "cpu", "cuda", "directml" or "coreml".
  // "auto" picks CoreML on macOS, DirectML on 64-bit Windows, CUDA on Linux with an NVIDIA
  // driver present, and CPU everywhere else.
  "InferenceDevice": "auto",

  // Keep only one of the three models resident on the GPU at a time. Costs a little time on each
  // stage switch, and holds peak VRAM near 1.5 GB instead of about 2.5 GB.
  "OffloadModels": true,

  // Check the SHA-256 of model files that are already on disk at every startup. Turning this off
  // saves a few seconds of hashing per start and gives up detection of a truncated download.
  "ValidateModelHashes": true,

  // Download the matching ONNX Runtime native library automatically. Turn off to supply your own
  // in TerrainDiffusionModels/onnxruntime/<version>/<flavour>/<rid>/.
  "DownloadRuntime": true,

  // Megabytes of decoded tensor windows kept per pipeline stage.
  "TileCacheMegabytes": 256,

  // Megabytes of finished terrain tiles to keep. This has to cover everything world generation
  // touches at once - a spawn area alone can span a hundred tiles - or tiles get evicted while
  // still in use and have to be rebuilt from scratch.
  "TerrainTileCacheMegabytes": 256,

  // Side length, in blocks, of the terrain generated per model query. Larger values spread the
  // model's latency over more chunks at the cost of a longer stall on first visit. Rounded down
  // to a multiple of 32, and clamped to 64-1024.
  "TerrainTileSizeBlocks": 256,

  // Log a line for every window the model computes. Very noisy; useful when profiling.
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

    // Fraction of the space below sea level that the deepest ocean reaches.
    "OceanDepthFraction": 0.9,

    // Multiplies the Perlin detail added to sloped ground. The model resolves features down to
    // one native pixel, so hillsides need roughness of their own; raise for craggier slopes.
    "SlopeDetailStrength": 1.0,

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

    // Added to the final rainfall as a fraction of full scale. The climate map cancels Vintage
    // Story's own "higher ground is wetter" bonus, because the model already handles orography
    // properly, and vanilla's thresholds were tuned with that bonus present; this puts its
    // average back. Raise for a lusher world, drop to zero for the model's unmodified answer.
    "RainfallBias": 0.05,

    // Degrees Celsius added to every model temperature, for a warmer or colder world.
    "TemperatureOffsetC": 0.0,

    // Scales the forest cover the model's moisture implies. Vintage Story's own forest map is
    // noise with no climate signal at all, so this replaces it outright; raise for denser woods.
    "ForestDensityMultiplier": 1.0,

    // Scales shrub cover the same way.
    "ShrubDensityMultiplier": 1.0,

    // ---- Seasons -------------------------------------------------------------------------------

    // Swing temperature through the year using the model's temperature seasonality instead of
    // Vintage Story's latitude bands. Continental interiors then get hard winters and hot summers
    // while maritime and tropical climates stay even.
    "SeasonalTemperature": true,

    // Multiplies the modelled seasonal temperature swing. Zero gives a world with no seasons.
    "SeasonalTemperatureStrength": 1.0,

    // Swing rainfall through the year using the model's precipitation seasonality, so monsoon
    // climates get a real wet and dry season rather than drizzling evenly all year.
    "SeasonalPrecipitation": true,

    // Multiplies the modelled wet/dry season contrast.
    "SeasonalPrecipitationStrength": 1.0,

    // Give the world two hemispheres with opposite seasons, split at the middle of the map. Off
    // by default: without a latitude temperature gradient to go with it, crossing the line just
    // makes the calendar disagree with itself.
    "SeasonHemispheres": false,

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
    // world setting; values above 6 are only reachable from here. Finer than 15 m runs the model
    // harder and overruns the one-byte climate map on high warm ground.
    "ScaleOverride": 0,

    // Overrides the world's "Vertical exaggeration" setting. Zero uses the world setting. In auto
    // height mode this multiplies the calibrated height rather than setting it outright.
    "VerticalExaggerationOverride": 0.0
  }
}
```

## Ranges

Anything outside these is clamped on load, and a value that is not a number at all is replaced with
the default.

| Field | Range |
| --- | --- |
| `TileCacheMegabytes`, `TerrainTileCacheMegabytes` | 32 – 4096 |
| `TerrainTileSizeBlocks` | 64 – 1024, rounded down to a multiple of 32 |
| `TargetPeakFillFraction` | 0.2 – 1 |
| `PeakQuantile` | 0.5 – 1 |
| `CalibrationRadiusBlocks` | 512 – 4 000 000 |
| `CalibrationProbes` | 0 – 64 |
| `ReliefFactor` | 1 – 5 |
| `MinAutoExaggeration`, `MaxAutoExaggeration` | 0.05 – 100 (max is raised to min if lower) |
| `MetersPerBlockVertical` | 0 or greater |
| `LinearKneeFraction` | 0.1 – 0.99 |
| `OceanDepthFraction` | 0.05 – 1 |
| `SlopeDetailStrength` | 0 – 8 |
| `MoistureMedian` | 0.01 – 100 |
| `MoistureSpread`, `RainfallSpread` | 0.1 – 4 |
| `RainfallMedianMm` | 10 – 10 000 |
| `RainfallBias` | -1 – 1 |
| `TemperatureOffsetC` | -40 – 40 |
| `ForestDensityMultiplier`, `ShrubDensityMultiplier` | 0 – 4 |
| `SeasonalTemperatureStrength`, `SeasonalPrecipitationStrength` | 0 – 4 |
| `StartingClimateSearchRadiusBlocks` | 512 – 4 000 000 |
| `StartingClimateNorthSouthCost` | 1 – 100 |
| `ScaleOverride` | 0, or 1 – 16 |
| `VerticalExaggerationOverride` | 0, or 0.05 – 20 |

An unrecognised `InferenceDevice`, `HeightMode`, `RainfallBasis` or `ClimateMode` falls back to its
default rather than failing to load.
