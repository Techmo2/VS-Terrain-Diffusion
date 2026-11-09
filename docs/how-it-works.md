# How it works

## Scale and world height

A block is as tall as it is wide, 15 m at the default resolution, so every slope has its real grade.
Model land reaches ~3 000 m at the 95th percentile and ~5 000 m at the extreme.

| World height | Blocks above sea | True scale up to (15 m) |
| ------------ | ---------------- | ----------------------- |
| 256          | 128              | ~1 550 m                |
| **512**      | 384              | ~4 800 m                |
| 1024         | 896              | ~11 300 m               |

512 is recommended. The table assumes the default sea level of Y 128. Above the true-scale limit,
heights bend towards the ceiling (`u / (1 + u)`) instead of clipping.
`worldGen.heightMode: "auto"` instead stretches the terrain once per world so the peaks around spawn
fill the world height.

With sea level at 128, vanilla data set as a fraction of world height (block layers, lake and ocean
beds, tree altitude bands) is moved to keep its height above the sea. Clouds sit 146 blocks above the
sea unless the world sets `cloudypos`.

## Climate

The model predicts four WorldClim variables alongside elevation, and on `seasonal` worlds the shape
of the year as well.

| Variable | Meaning |
| -------- | ------- |
| BIO1     | Annual mean temperature, °C |
| BIO4     | Temperature seasonality |
| BIO12    | Annual precipitation, mm |
| BIO15    | Precipitation seasonality |

The game's climate map is built from them:

- **Temperature** is stored at sea level. The game's altitude correction is replaced by 6.5 °C/km.
- **Rainfall** is quantile-mapped from the model's tree moisture (precipitation against
  evapotranspiration), because vanilla's thresholds expect a uniform 0–255 byte.
  `worldGen.rainfallBasis: "precipitation"` maps raw millimetres instead.
- **Forest and shrub cover** come from moisture and growing season, are zero on slopes too steep for
  soil, and are thinned into clearings by vanilla's forest and shrub noise.
- **Geologic activity** stays vanilla's.

## Seasons

The year's temperature swing comes from BIO4 rather than latitude alone, so a coast and an interior
at the same mean temperature get different years. BIO15 gives monsoon climates a dry season.

On `seasonal` worlds the year also gets the model's summer length, timing of the wet seasons and
day–night range (BIO2). These are stored as map region data and sent to clients. A client
without the mod shows vanilla's seasons.

`/tdiff season <x> <z>` prints the year at a position.

## Lakes and rivers

Water is solved from the model's heights by `TerrainDiffusion.Water`, on 256-block tiles. The result
depends only on terrain and settings, so chunks generated at any time meet without seams.

- **Lakes.** Every closed basin above sea level fills to its spill. A basin of 65 536 columns or more
  keeps a terminal lake of about that size.
- **Wetlands.** Lakes stand `WetlandDepth` below their spill. The shallow ground this exposes gets
  one-block pools and waterside plants.
- **Rivers.** A column is river once enough land drains through it (`trunk` 41 943 columns, `river`
  20 972, `creek` 10 486) and it carries `RiverTrim` of the peak discharge. Width is 5 Q^0.5 m and
  depth 0.4 Q^0.4 m, times `RiverWidth`, clamped to 1–57 wide and 1–8 deep. Discharge comes from Turc
  runoff. Below 0.01 m³/s the channel is a dry bed.
- **Blocks.** Water is still and fresh, with lake ice below −15 °C. Each river column's current is
  saved in map chunk mod data `vsterraindiffusion:riverflow`.
- **Waiting.** A chunk waits until its water is final, which can mean generating the tiles around
  and upstream of it first.
- **Memory.** Each tile held takes ~1.75 MB, up to 512 MB in all. Far tiles are dropped and read
  back from the [tile database](servers.md#tile-database).
- **Older worlds.** A world made before lakes stays dry. A world made with the earlier lakes gets the
  new lakes, but no rivers or wetlands.
