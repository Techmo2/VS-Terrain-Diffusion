Half-precision base (latent) model for VS Terrain Diffusion, for GPU providers.

This is a model-data prerelease, not a mod release.

- Derived from the `xandergos/terrain-diffusion-30m` teacher, exported and converted by
  [terraindistill](https://github.com/Techmo2/terraindistill); the commit and the full package list
  are recorded in `build-environment.txt` and in the workflow that built this release.
- Exported at 64x64 with a dynamic batch axis, which is how `WorldPipeline` invokes this model.
  Inputs and outputs are float32 even though the graph computes in float16, so nothing upstream of
  the model changes.
- Release model: 507,810,847 bytes, SHA-256
  `fcb4ddd9a9f4b6aebcfc9663d40173128c2c0f1a105bf10a286321cf4a521647`.
- The graph contains terraindistill's overflow-free rewrite of `UNetBlock`'s conditioning RMS
  normalisation. It is algebraically identical to the upstream model; without it this graph scores
  0.956 relative L2 in float16 instead of 0.0045.

Measured against the float32 model on 128 base-model calls captured from real world generation, on
an RTX 3060 Laptop: mean 0.51% and worst 0.97% relative RMS on the TensorRT RTX provider, with no
outliers. Generating ten 128x128-pixel regions with this model and the FP16 decoder took 7.5 s
against 19.2 s for the FP32 models on CUDA, and moved elevation about 4 m on average - for scale,
generating the same world on a CPU instead of that GPU already differs by about 2.6 m.

Select it with `basePrecision: "fp16"`. It is never selected automatically, because changing model
precision alters newly generated terrain slightly; keep it fixed for an established world.

The upstream model is copyright Alexander Goslin and distributed under the MIT licence, included
here as `MODEL-LICENSE.txt`.
