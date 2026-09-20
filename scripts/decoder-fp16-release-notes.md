Half-precision decoder for VS Terrain Diffusion, for GPU providers.

This is a model-data prerelease, not a mod release.

- Derived from the `xandergos/terrain-diffusion-30m` teacher, exported and converted by
  [terraindistill](https://github.com/Techmo2/terraindistill); the commit and the full package list
  are recorded in `build-environment.txt` and in the workflow that built this release.
- Exported at **256x256**, which is the window the decoder stage of `WorldPipeline` decodes. A
  decoder graph's height and width are fixed at export time and it will not load at any other size,
  so the tag and the file name both carry the window. `TerrainTileSizeBlocks` changes how many of
  these windows run per tile, not their shape.
- Inputs and outputs are float32 even though the graph computes in float16, so nothing upstream of
  the model changes.
- Release model: `decoder_model.fp16.256.onnx`, 56,264,771 bytes, SHA-256
  `2429cf246bf8905b17763bb91edd0adbe5a8dc09be7c2228a62e3ad968f0fb8e`.

Measured against the float32 decoder on 99 decoder calls captured from real world generation, on an
RTX 3060 Laptop: mean 0.43% and worst 1.25% relative RMS on the TensorRT RTX provider, and 21 ms
per window against 82 ms for the FP32 decoder on CUDA.

Select it with `decoderPrecision: "fp16"` on a GPU provider. It is never selected automatically,
because changing model precision alters newly generated terrain slightly; keep it fixed for an
established world. `decoderPrecision: "int8"` remains the option for CPU and OpenVINO.

The upstream model is copyright Alexander Goslin and distributed under the MIT licence, included
here as `MODEL-LICENSE.txt`.
