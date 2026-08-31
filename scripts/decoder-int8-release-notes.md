Optional mixed-precision decoder for the Linux CPU/OpenVINO path in VS Terrain Diffusion.

This is a model-data prerelease, not a mod release.

- Derived from `xandergos/terrain-diffusion-30m-onnx` revision `ad2df557eca5645f588766101cf3bc3682455c3e`.
- Source decoder SHA-256: `6473ae47ca6ec4d743d30fe4f5d381fe4158899714eff09b762005bdbdef68c1`.
- ONNX Runtime static QDQ quantisation of 67 Conv/MatMul nodes in the 64x64 and 128x128 decoder stages; other nodes remain FP32.
- Release decoder: 43,496,635 bytes, SHA-256 `0ce6eb771a072a8622448c30488f0505c009e43bebccd65246a4dd58fe8e2da6`.
- On four captured decoder inputs, MAE against FP32 ranged from 0.00365 to 0.00647 and RMSE from 0.00511 to 0.00848.

`decoder-int8-v1-calibration.zip` contains the four calibration inputs, pinned Python dependencies,
exact rebuild script, and upstream licence. The GitHub Actions build verifies every input and the
exact output before creating or updating this release.

The upstream model and this derivative are MIT-licensed: Copyright (c) 2025 Alexander Goslin. The
full notice is embedded in the ONNX metadata and included in the calibration bundle and
`MODEL-LICENSE.txt` asset.
