# Performance

## Devices

Each world runs ONNX Runtime in a worker process of its own, which exits when the world closes. A
device change takes effect on the next world load without restarting the game. Only the runtime the
selected device needs is downloaded.

| Device         | Runs on |
| -------------- | ------- |
| `auto`, `cpu`  | Windows or Linux on x64 or arm64, and macOS on Apple silicon |
| `cuda`         | 64-bit Windows or Linux with an NVIDIA GPU. CUDA 12 needs compute 5.0+ and driver 525+. CUDA 13 needs compute 7.5+ and driver 580+. Linux needs the system CUDA toolkit and cuDNN 9. Windows downloads them (~1 GB) if missing. |
| `tensorrt-rtx` | 64-bit Windows or Linux, RTX 30xx or newer (compute 8.6/8.9/12.0/12.1), driver 580+. The runtime is 105–140 MB. Builds a cached engine per model. |
| `rocm`         | 64-bit Linux with an AMD GPU, ROCm 7 and MIGraphX, and the user in the `render` group. Compiles each model on first use (minutes), then caches it. |
| `openvino`     | 64-bit Linux with an SSE4.2 CPU. Speeds up the decoder. The rest runs on the CPU. |
| `directml`     | Windows 10 1903+ with a Direct3D 12 GPU |
| `coreml`       | macOS 10.15+ |

A world set to a device this machine can't run stops loading and names the devices that work. If a
supported device's runtime fails to start, that session falls back to what `auto` would pick, and
the log says so. The setting itself is never changed.

## Inference settings

These are world settings in the Terrain Diffusion tab. Each code starts with `terraindiffusion`.
Keep the device and both precisions fixed once a world is explored. Changing them makes new terrain
differ by a few metres from existing chunks.

| Code                    | Default | Effect |
| ----------------------- | ------- | ------ |
| `InferenceDevice`       | auto    | See [Devices](#devices). |
| `BasePrecision`         | fp32    | `fp16` (GPU only) is the biggest speed-up. New terrain differs by ~4 m on average. |
| `DecoderPrecision`      | fp32    | `fp16` for GPUs, `int8` for CPU and OpenVINO. |
| `GpuUtilizationPercent` | 100     | Share of time generation may keep the GPU busy. See [Stutter](#stutter). |
| `CpuThreads`            | auto    | Threads for CPU inference. Auto is one per physical core. |
| `OffloadModels`         | off     | Keeps one model on the GPU at a time, saving ~1 GB of VRAM at about 3x the tile time. |
| `ModelLoadMode`         | file    | `memory` reads graphs into RAM first (~1 GB more). |
| `ValidateModelHashes`   | on      | Checks the SHA-256 of model files on load. |
| `DownloadRuntime`       | on      | Downloads ONNX Runtime, OpenVINO and, on Windows, the VC++ runtime if missing. |
| `TileDatabase`          | on      | Stores generated tiles on disk. See [Servers](servers.md#tile-database). |

Only the selected models are downloaded, into `TerrainDiffusionModels/`.

| Setting                  | File                          |
| ------------------------ | ----------------------------- |
| `DecoderPrecision: int8` | `decoder_model.int8.onnx`     |
| `DecoderPrecision: fp16` | `decoder_model.fp16.256.onnx` |
| `BasePrecision: fp16`    | `base_model.fp16.onnx`        |

Recipes for these are in `scripts/`.

## Memory

These are set in the [mod config](configuration.md).

| Key                         | Default | Effect |
| --------------------------- | ------- | ------ |
| `TileCacheMegabytes`        | 256     | Decoded tensor windows across all pipeline stages. |
| `TerrainTileCacheMegabytes` | 256     | Finished terrain tiles. Raise it if the log warns of thrashing. |
| `TerrainTileSizeBlocks`     | 0       | Blocks per model run. 0 picks 128 on CPU and 256 on GPU. Larger tiles give better throughput but longer first-visit stalls. |
| `LatentBatchSize`           | 0       | Base-model batch. 0 picks 1 on CPU and 4 on GPU. |

## Stutter

In single player the model shares the GPU with the renderer. A burst of chunk generation can freeze
frames, even though the game thread isn't blocked.

`GpuUtilizationPercent` below 100 leaves the GPU idle between runs. Generation slows by the inverse.
At 50% a tile takes about twice as long. Start at 50 and lower it only as far as needed. Set it too
low and generation falls behind a walking player. `/tdiff gpulimit <percent>` changes it live and
saves it to the world.
