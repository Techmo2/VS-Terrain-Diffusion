# World settings

Set in the Customize screen's **Terrain Diffusion** tab and saved with the world. On a dedicated
server, set them under `WorldConfig.WorldConfiguration` in `serverconfig.json`. To change one on an
existing world, use `/worldconfig <code> <value>`.

Every code below starts with `terraindiffusion`, which the tables leave out.

## Terrain

| Code                   | Default | Effect |
| ---------------------- | ------- | ------ |
| `Enabled`              | on      | Off falls back to vanilla terrain. |
| `Scale`                | 15 m    | Metres per block, horizontally and vertically. `1`–`6` = 30, 15, 10, 7.5, 6, 5 m. |
| `VerticalExaggeration` | 1x      | Multiplies terrain height. |
| `SeaLevel`             | vanilla | `128` fixes the sea at Y 128, so a taller world adds height above it. |
| `CoarsePooling`        | off     | `2` or `4` packs that many times the landscape into the same distance. |
| `CoarsePoolMode`       | average | `extreme` keeps each cell's highest peak and deepest valley. |
| `DetailRedraw`         | 35      | Hundredths of the detail model's first pass that is redrawn. |
| `HeightNoise`          | 0       | Random height per ~8 km cell, in hundredths of √m (200 ≈ ±100 m at 600 m). |
| `RiverValleyDepth`     | 20%     | How far land is lowered along the Rivers mod's rivers. |

## Climate

| Code                    | Default  | Effect |
| ----------------------- | -------- | ------ |
| `Climate`               | full     | `off` makes terrain only. |
| `ClimateModel`          | seasonal | `annual` is the older model, with no seasonal shape or Köppen classes. The two draw different land from one seed. |
| `AltitudeCooling`       | 100%     | Each 100% above 100% takes another 6.5 °C/km off. |
| `WinterAmplification`   | 80       | Deepens winters with latitude, as vanilla does. Can change any time. |
| `BandedRainfallBias`    | off      | The older latitude rainfall plan. |
| `ForestDensity`         | 100%     | Scales forest cover. Trees go as its square. |
| `ShrubDensity`          | 100%     | Scales shrub cover. |

## Water

See [Lakes and rivers](how-it-works.md#lakes-and-rivers).

| Code                     | Default | Effect |
| ------------------------ | ------- | ------ |
| `WaterFill`              | on      | Lakes and rivers. |
| `WetlandDepth`           | 1       | Blocks lakes stand below their spill. 0 fills them to the brim. |
| `RiverMode`              | trunk   | How much drainage makes a river, from `off` through `creek` and `river` to `trunk`. |
| `RiverTrim`              | 30%     | Share of peak discharge a river needs. Trims tributaries. |
| `RiverWidth`             | x20     | Multiplies width and depth. x1 is true scale. |
| `MeanderSinuosity`       | 1.9     | Path length over straight distance on open plains. |
| `MeanderWavelength`      | 600 m   | Distance between bends. |
| `MeanderSlope`           | 0.015   | Gradient that halves the meander. |
| `MeanderRelief`          | 4 m     | Local relief that halves the meander. |
| `MeanderConfinement`     | 240 m   | Distance over which relief holds a river in. |
| `MeanderFreeRelief`      | x2      | Relief above this multiple of `MeanderRelief` runs straight downhill. |
| `WaterThreads`           | auto    | CPU threads. Auto is half the logical processors, at most 8. |

Inference settings (device, precision, GPU limit) are in the same tab. See
[Performance](performance.md).

### Land and sea

The model is conditioned on the world's ocean map (vanilla's or a mod's, such as Continental World),
so a 50% world gets about 50% land. The coast follows the map to about one map cell (512
blocks at 15 m). Islands and lakes smaller than a cell may be lost.

`worldGen.oceanMap: "output"` reverses this. The model invents the continents and the ocean map is
rewritten to match.

### Global temperature and precipitation

These are given to the model as conditioning, so forests, soil, snow line and seasons follow. Whatever
the model can't reach (above "Hot") is applied to its output, as vanilla does.
`worldGen.globalClimateStrength: 0` applies everything to the output.

### Latitude

Latitude comes from the game's `polarEquatorDistance`. The model is conditioned on a real zonal
climate: an equatorial rain belt, deserts at 25°, a storm track at 50° and cold dry poles. Poleward
of ~80° the last few degrees of cold are added after the model runs. Hemispheres get opposite years.

`worldGen.latitudeStrength: 0` removes the gradient.

### Starting climate

The bands are annual means: hot 28–32 °C, warm 19–23 °C, temperate 6–14 °C, cool −5 to 1 °C,
icy −15 to −10 °C. The search prefers east–west travel and stops at the first match. If none is in
range, you spawn at the closest temperature found.

`worldGen.startingClimateSearch: false` spawns on the nearest land.
