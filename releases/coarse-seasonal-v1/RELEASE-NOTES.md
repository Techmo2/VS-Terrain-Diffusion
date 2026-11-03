Seasonal coarse model for VS Terrain Diffusion's `seasonal` climate model (the default for new worlds).

This is a model-data prerelease, not a mod release.

- `td30m_seasonal_ft`: the 30 m pipeline's coarse stage retrained to also predict the seasonal cycle. 18 input channels (13 noisy + 5 conditioning), 13 output channels: elevation, its 5th percentile, mean temperature, temperature seasonality, annual precipitation, precipitation seasonality, the six seasonal-cycle harmonics (temperature shape, wet-season timing, second rainy season) and the diurnal temperature range. Enough to derive monthly climate and Köppen classes.
- Trained from `diffusion_coarse_30m_seasonal` (150 epochs, bf16) for 100 more epochs in fp32, cosine learning rate 1e-3 to 0; validation loss -1.957. Exported with EMA σ_rel 0.05.
- The base model and decoder are unchanged: the released 30 m ones from `xandergos/terrain-diffusion-30m-onnx`.
- Exported with the terrain-diffusion repository's ONNX exporter (opset 17); max |difference| from PyTorch 8.6e-6.
- Release model: 22,626,150 bytes, SHA-256 `7aa8783fa496b5c742014a5a0f17f6d39246b9d490cc9e62aa90f241206079ef`. The mod pins both and rejects any other file.

MIT-licensed: the weights are Copyright (c) 2026 Bradly Tillmann; the architecture and training code are Copyright (c) 2025 Alexander Goslin. Trained on ETOPO 2022 (NOAA NCEI) and WorldClim 2.1 (Fick and Hijmans, 2017). The full notice is in the `MODEL-LICENSE.txt` asset.
