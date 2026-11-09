# VS Terrain Diffusion

Generates Vintage Story worlds with [Terrain Diffusion](https://github.com/xandergos/terrain-diffusion),
a diffusion model trained on Earth's topography and climate. Terrain, temperature, rainfall, forests,
lakes, rivers and seasons all come from the model.

## Install

Put the release zip in `Mods`. The mod is server-side. Clients may install it so their weather display
matches the server. The models (~2.2 GB) and ONNX Runtime download on first launch, with progress on
the loading screen.

It needs Vintage Story 1.22 and ~3 GB of RAM. A GPU is strongly recommended.

| Platform              | Device (auto) | Needs                           |
| --------------------- | ------------- | ------------------------------- |
| Linux + NVIDIA        | CUDA          | CUDA 12 or 13 runtime, cuDNN 9  |
| Windows               | DirectML      | —                               |
| macOS (Apple silicon) | CoreML        | —                               |
| Anything else         | CPU           | 10–20x slower than a GPU        |

ROCm, OpenVINO and TensorRT RTX are opt-in. See [Performance](docs/performance.md).

## Quick start

1. In Customize, set **World height** to 1024. Shorter worlds compress the highest mountains.
2. Set **Landcover** to 40–60% for real coastlines. Vanilla's 97.5% gives one unbroken continent.
3. Create the world. The **Terrain Diffusion** tab's defaults work as they are.

## What it changes

| Part of world generation | Now comes from                                               |
| ------------------------ | ------------------------------------------------------------ |
| Terrain                  | The model's heightmap, conditioned on the world's ocean map   |
| Climate map              | Model temperature and rainfall, real lapse rate, latitude bands |
| Forest and shrub maps    | Model moisture and growing season                             |
| Seasons                  | Each place's own seasonality, including rain                  |
| Lakes and rivers         | A drainage graph solved on the model's heights                |
| Surface                  | Bare rock on steep slopes, glacier ice where summers freeze   |
| Spawn                    | Land in the world's starting climate                          |

Ores, caves, rock strata, structures and temporal stability are untouched. The Landform scale and
Upheaval rate settings have no effect, because the generator that read them is replaced.

## Documentation

| Page                                        | Covers                                                       |
| ------------------------------------------- | ------------------------------------------------------------ |
| [World settings](docs/world-settings.md)    | The Terrain Diffusion tab, and how vanilla's settings apply  |
| [How it works](docs/how-it-works.md)        | Scale, climate, seasons, lakes and rivers                    |
| [Other mods](docs/other-mods.md)            | Rivers, Terrain Sampler, Watersheds and the climate API      |
| [Performance](docs/performance.md)          | Devices, precisions, stutter, memory                         |
| [Servers](docs/servers.md)                  | Tile database, and pregenerating on another machine          |
| [Commands](docs/commands.md)                | `/tdiff` and the debug map                                   |
| [Configuration](docs/configuration.md)      | Every field of `ModConfig/vsterraindiffusion.json`           |

## Building

```bash
./build.sh            # Linux, macOS
build.bat             # Windows
```

Both take an optional configuration (default `Release`) and write
`dist/vsterraindiffusion_<version>.zip`. They need the .NET 10 SDK and a Vintage Story install, found
at `/opt/vintagestory`, `~/Vintagestory` or `%APPDATA%\Vintagestory`. Set `VINTAGE_STORY` to use
another.

`src/TerrainDiffusion.Water` is a copy of terrain-diffusion's `csharp/TerrainDiffusion.Water`, refreshed
by `scripts/sync-water.sh`. Bump `CurrentWaterVersion` when a refresh changes the water.

`doxygen` from the repository root builds the code documentation into `docs/api/html`.

## Credits

- [xandergos](https://github.com/xandergos) made the Terrain Diffusion model, its reference
  implementation and the original Minecraft mod.
- The mixed-precision decoders are derived from that MIT-licensed model. Their recipes and the
  upstream copyright notice are in [`scripts/`](scripts/).
