# INT8 decoder recipe

`quantize_decoder.py` rebuilds the optional mixed-precision decoder used by the OpenVINO path. It
starts from the decoder at Terrain Diffusion ONNX revision
`ad2df557eca5645f588766101cf3bc3682455c3e`, applies ONNX Runtime's basic graph optimisation, then
uses static QDQ quantisation on the 67 convolution and matrix-multiplication nodes in the 64x64 and
128x128 decoder stages. The remaining nodes stay in FP32.

The four calibration files are the inputs to the first four decoder calls from a generated test
world. They are kept in `decoder-calibration-v1.zip`; the workflow checks the archive and the script
checks each unpacked file, so a different calibration set cannot silently produce an asset bearing
the release name.

Create a clean environment, install `requirements-quantize.txt`, then run:

```text
python quantize_decoder.py decoder_model.onnx decoder_model.int8.onnx \
  decoder-calibration-01.f32 decoder-calibration-02.f32 \
  decoder-calibration-03.f32 decoder-calibration-04.f32
```

The script checks its dependency versions, the source and calibration hashes, the selected graph
nodes, and the exact output size and SHA-256. The release output is 43,496,635 bytes with SHA-256:

```text
0ce6eb771a072a8622448c30488f0505c009e43bebccd65246a4dd58fe8e2da6
```

The generated ONNX file embeds its source URL, recipe, copyright, and full MIT licence. The upstream
model is copyright Alexander Goslin and distributed under the MIT licence in `MODEL-LICENSE.txt`.

GitHub Actions runs this recipe for the `decoder-int8-v1` tag (or a manual workflow dispatch),
checks the exact output hash above, and attaches the model, checksum, licence, and rebuild bundle to
the matching GitHub release. The generated model is not committed to the repository.
