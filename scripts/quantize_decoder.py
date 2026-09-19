#!/usr/bin/env python3
"""Rebuild the mixed-precision decoder distributed for CPU inference."""

from __future__ import annotations

import argparse
import gc
import hashlib
import math
import tempfile
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.quantization import (
    CalibrationDataReader,
    CalibrationMethod,
    QuantFormat,
    QuantType,
    quantize_static,
)


SOURCE_SHA256 = "6473ae47ca6ec4d743d30fe4f5d381fe4158899714eff09b762005bdbdef68c1"
CAPTURE_SHA256 = (
    "218f6726d34500117bd0274ab27e4e2eb6679419f272f8b3995d7996d2c69425",
    "d3998e28cee00a17221594b46238c6e33cce4a2da60ea718543d899824616e8f",
    "b5e306a18a8ec8ab6e244364359a7c46640b4f096281b6bbc81b2ab64fce9be9",
    "caabd102bb57059ae9a35110b0848887d1f2b7a1366c8bf6b564b9921a326c17",
)
OUTPUT_SHA256 = "0ae464c884593b3016a19365caf3ae43a7e26743c8ef1234814e10bbbd9b5b74"
OUTPUT_SIZE = 43_497_445
INPUT_SHAPE = (1, 5, 256, 256)
SOURCE_URL = (
    "https://huggingface.co/xandergos/terrain-diffusion-30m-onnx/resolve/"
    "ad2df557eca5645f588766101cf3bc3682455c3e/decoder_model.onnx"
)
MODEL_LICENSE = """MIT License

Copyright (c) 2025 Alexander Goslin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE."""
EXPECTED_VERSIONS = {
    "numpy": "2.2.6",
    "onnx": "1.19.0",
    "onnxruntime": "1.24.1",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


class DecoderCalibrationData(CalibrationDataReader):
    def __init__(self, paths: list[Path]) -> None:
        noise_label = np.array([math.atan(80.0 / 0.5)], dtype=np.float32)
        self._samples = [
            {
                "x": np.fromfile(path, dtype=np.float32).reshape(INPUT_SHAPE),
                "noise_labels": noise_label.copy(),
            }
            for path in paths
        ]
        self.rewind()

    def get_next(self):
        return next(self._iterator, None)

    def rewind(self) -> None:
        self._iterator = iter(self._samples)


def check_versions() -> None:
    actual = {
        "numpy": np.__version__,
        "onnx": onnx.__version__,
        "onnxruntime": ort.__version__,
    }
    mismatches = [
        f"{name} {actual[name]} (expected {version})"
        for name, version in EXPECTED_VERSIONS.items()
        if actual[name] != version
    ]
    if mismatches:
        raise RuntimeError("Package versions do not match the release recipe: " + ", ".join(mismatches))


def verify_inputs(source: Path, captures: list[Path]) -> None:
    if sha256(source) != SOURCE_SHA256:
        raise RuntimeError("The source decoder does not match the pinned upstream model")
    if len(captures) != len(CAPTURE_SHA256):
        raise RuntimeError(f"Expected {len(CAPTURE_SHA256)} calibration captures")
    for index, (path, expected) in enumerate(zip(captures, CAPTURE_SHA256), 1):
        if sha256(path) != expected:
            raise RuntimeError(f"Calibration capture {index} does not match the release recipe")


def optimize_source(source: Path, target: Path) -> None:
    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_BASIC
    options.optimized_model_filepath = str(target)
    session = ort.InferenceSession(str(source), options, providers=["CPUExecutionProvider"])
    del session
    gc.collect()


def quantize(source: Path, target: Path, captures: list[Path]) -> None:
    graph = onnx.load(str(source), load_external_data=False).graph
    nodes = [
        node.name
        for node in graph.node
        if node.op_type in ("Conv", "MatMul")
        and ("64x64" in node.name or "128x128" in node.name)
    ]
    if len(nodes) != 67:
        raise RuntimeError(f"Expected to quantize 67 nodes, found {len(nodes)}")

    quantize_static(
        model_input=str(source),
        model_output=str(target),
        calibration_data_reader=DecoderCalibrationData(captures),
        quant_format=QuantFormat.QDQ,
        activation_type=QuantType.QUInt8,
        weight_type=QuantType.QInt8,
        calibrate_method=CalibrationMethod.MinMax,
        per_channel=True,
        reduce_range=False,
        op_types_to_quantize=["Conv", "MatMul"],
        nodes_to_quantize=nodes,
        extra_options={
            "ActivationSymmetric": False,
            "WeightSymmetric": True,
            "CalibMovingAverage": False,
            "CalibTensorRangeSymmetric": False,
        },
    )


def stamp_metadata(target: Path) -> None:
    model = onnx.load(str(target), load_external_data=False)
    properties = {entry.key: entry.value for entry in model.metadata_props}
    properties.update(
        {
            "copyright": "Copyright (c) 2025 Alexander Goslin",
            "license": "MIT",
            "license_text": MODEL_LICENSE,
            "source": SOURCE_URL,
            "quantization_recipe": "scripts/quantize_decoder.py",
        }
    )
    onnx.helper.set_model_props(model, properties)
    model.doc_string = (
        "Mixed-precision INT8 derivative of the Terrain Diffusion decoder. "
        "The upstream model and this derivative are licensed under MIT."
    )
    onnx.save_model(model, str(target))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Pinned upstream decoder_model.onnx")
    parser.add_argument("target", type=Path, help="Output decoder_model.int8.onnx")
    parser.add_argument("captures", nargs=4, type=Path, help="Release calibration .f32 files")
    args = parser.parse_args()

    check_versions()
    verify_inputs(args.source, args.captures)
    args.target.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="terrain-diffusion-quantize-") as temporary:
        optimized = Path(temporary) / "decoder_model.basic.onnx"
        optimize_source(args.source, optimized)
        quantize(optimized, args.target, args.captures)
        stamp_metadata(args.target)

    actual_hash = sha256(args.target)
    actual_size = args.target.stat().st_size
    if actual_size != OUTPUT_SIZE or actual_hash != OUTPUT_SHA256:
        raise RuntimeError(
            f"Output does not match the release artifact: {actual_size} bytes, SHA-256 {actual_hash}"
        )
    print(f"{args.target}: {actual_size} bytes, SHA-256 {actual_hash}")


if __name__ == "__main__":
    main()
