#!/usr/bin/env python3
"""Mint quantized / reduced-resolution YOLO detection models for FrameFlow.

FrameFlow's detector (ADR-0050) is model-shape-aware: it runs any YOLOv8/v11
transposed detect head (``[1, 4+C, A]``) at any 32-multiple square input size,
with FP32 graph I/O. This tool *produces* such models and *validates* that they
satisfy that contract before you ship them.

It encapsulates the export/convert footguns that otherwise cost a day:

* ultralytics defaults to ONNX opset 20, whose ``Resize`` variant breaks the
  onnxconverter-common FP16 pass -- we pin opset 17.
* onnxslim bakes stale ``value_info`` into the graph; the FP16 pass does not
  update it, producing a model that fails to load -- we strip it.
* FrameFlow's preprocessor feeds FP32, so FP16 models must keep FP32 graph I/O
  (``keep_io_types=True``) with FP16 only internally. ultralytics ``half=True``
  produces FP16 *I/O*, which needs ADR-0050 Tier B -- we deliberately avoid it.

Commands::

    mint_yolo.py export   --arch yolov8n --imgsz 320 [--fp16] --out model.onnx
    mint_yolo.py convert  --from-onnx in.onnx (--fp16 | --int8-dynamic
                          | --int8-static --calib-dir DIR) --out out.onnx
    mint_yolo.py validate --model model.onnx

``validate`` mirrors the detector's ``YoloModelDescriptor.FromSession`` /
``TryDescribe`` contract: same anchor formula, same head checks. A model that
passes here is the model the detector will accept at load time.

Dependencies (lazy-imported, so you only install what a command needs):

* validate / convert --fp16 : onnx, onnxruntime, onnxconverter-common
* convert --int8-*          : + sympy
* convert --int8-static     : + pillow, numpy
* export                    : + ultralytics  (pulls torch)

Licensing: ultralytics YOLOv8/YOLO11 weights are AGPL-3.0, and models derived
from them (these exports + conversions) inherit it. Do not bundle them into a
redistributed library package -- see ADR-0051 for the acquisition strategy.
"""
from __future__ import annotations

import argparse
import os
import shutil
import sys

# YOLOv8/v11 detect heads predict at strides 8/16/32. Keep this in lockstep
# with YoloModelDescriptor.AnchorsFor / .FromSession on the C# side.
STRIDES = (8, 16, 32)


def anchors_for(size: int) -> int:
    """Total anchors for a square input side (multiple of 32)."""
    return sum((size // s) ** 2 for s in STRIDES)


# ---------------------------------------------------------------------------
# validate -- mirrors YoloModelDescriptor.FromSession (ADR-0050 §2/§5)
# ---------------------------------------------------------------------------
def describe_model(model_path: str) -> tuple[bool, str]:
    """Inspect an ONNX model and report FrameFlow compatibility.

    Returns (ok, message). ``ok`` is False with a descriptive reason when the
    model would be rejected by the detector's auto-inference.
    """
    import onnxruntime as ort

    so = ort.SessionOptions()
    so.log_severity_level = 3
    sess = ort.InferenceSession(model_path, so, providers=["CPUExecutionProvider"])
    inp, out = sess.get_inputs()[0], sess.get_outputs()[0]

    def dim(v):  # ORT reports dynamic dims as strings/None
        return v if isinstance(v, int) else -1

    in_shape = [dim(d) for d in inp.shape]
    out_shape = [dim(d) for d in out.shape]
    notes: list[str] = []

    if len(in_shape) != 4 or in_shape[1] != 3:
        return False, f"input {inp.shape} is not [1,3,S,S]; pass an explicit descriptor"
    h, w = in_shape[2], in_shape[3]
    if h <= 0 or w <= 0 or h != w:
        return False, f"input {inp.shape} is dynamic or non-square; needs a static square input"
    size = h
    if size % 32 != 0:
        return False, f"input side {size} is not a multiple of 32 (not a YOLOv8/v11 head?)"

    if len(out_shape) != 3 or out_shape[0] != 1:
        return False, (
            f"output {out.shape} is not a transposed detect head [1,4+C,A]; "
            "yolov10/NMS-free/seg/pose heads are unsupported (ADR-0050 non-goals)"
        )
    channels, anchors = out_shape[1], out_shape[2]
    class_count = channels - 4
    if class_count <= 0:
        return False, f"output channel count {channels} <= 4; not a 4-box + C-class head"
    expected = anchors_for(size)
    if anchors != expected:
        return False, (
            f"output anchors {anchors} != {expected} expected for a {size}px head; "
            "output may be [1,A,4+C] (non-transposed) or a different architecture"
        )

    # FrameFlow Tier A feeds FP32. FP16-I/O models need ADR-0050 Tier B.
    if "float16" in inp.type:
        notes.append("FP16 graph I/O -- needs ADR-0050 Tier B; not a Tier-A drop-in")
    elif "float" not in inp.type:
        notes.append(f"non-float input type {inp.type}")

    label = "COCO-80" if class_count == 80 else f"{class_count}-class"
    msg = (
        f"OK: {size}px input, {label} ({class_count} classes), {anchors} anchors, "
        f"io={inp.type.replace('tensor(', '').rstrip(')')}"
    )
    if notes:
        msg += "  | WARN: " + "; ".join(notes)
    return True, msg


# ---------------------------------------------------------------------------
# convert -- FP16 / INT8 from an existing ONNX (no torch)
# ---------------------------------------------------------------------------
def convert_fp16(src_path: str, out_path: str) -> None:
    import onnx
    from onnxconverter_common import float16

    model = onnx.load(src_path)
    # Strip onnxslim's stale value_info: the FP16 pass leaves it FP32, which
    # otherwise produces a Cast/Resize type mismatch that fails to load.
    del model.graph.value_info[:]
    # keep_io_types=True -> FP32 graph I/O, FP16 weights+compute (Tier A drop-in).
    converted = float16.convert_float_to_float16(model, keep_io_types=True)
    onnx.save(converted, out_path)


def convert_int8_dynamic(src_path: str, out_path: str) -> None:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    quantize_dynamic(src_path, out_path, weight_type=QuantType.QInt8)
    print(
        "NOTE: INT8 acceleration is hardware-dependent. Intel Gen 9 (HD 620) has "
        "no DP4a fast path -- expect parity-or-worse there; real gains need an NPU "
        "or a DP4a-capable GPU.",
        file=sys.stderr,
    )


def convert_int8_static(src_path: str, out_path: str, calib_dir: str, limit: int = 200) -> None:
    import numpy as np
    from onnxruntime.quantization import (
        CalibrationDataReader,
        QuantFormat,
        QuantType,
        quantize_static,
    )
    from onnxruntime.quantization.shape_inference import quant_pre_process
    from PIL import Image

    import onnxruntime as ort

    so = ort.SessionOptions()
    so.log_severity_level = 3
    sess = ort.InferenceSession(src_path, so, providers=["CPUExecutionProvider"])
    inp = sess.get_inputs()[0]
    size = inp.shape[2] if isinstance(inp.shape[2], int) else 640
    input_name = inp.name

    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    files = [
        os.path.join(calib_dir, f)
        for f in sorted(os.listdir(calib_dir))
        if os.path.splitext(f)[1].lower() in exts
    ][:limit]
    if not files:
        raise SystemExit(f"no calibration images found in {calib_dir}")

    def preprocess(path: str) -> "np.ndarray":
        # Mirror Yolov8Preprocessor: stretched resize to SxS, RGB, /255, CHW.
        img = Image.open(path).convert("RGB").resize((size, size))
        arr = np.asarray(img, dtype=np.float32) / 255.0  # HWC
        arr = arr.transpose(2, 0, 1)[np.newaxis, ...]  # NCHW
        return np.ascontiguousarray(arr)

    class _FolderReader(CalibrationDataReader):
        def __init__(self) -> None:
            self._it = iter({input_name: preprocess(f)} for f in files)

        def get_next(self):
            return next(self._it, None)

    prepped = out_path + ".prep.onnx"
    quant_pre_process(src_path, prepped)
    quantize_static(
        prepped,
        out_path,
        _FolderReader(),
        quant_format=QuantFormat.QDQ,
        weight_type=QuantType.QInt8,
        activation_type=QuantType.QInt8,
    )
    os.remove(prepped)
    print(f"static INT8 calibrated on {len(files)} image(s) from {calib_dir}", file=sys.stderr)


# ---------------------------------------------------------------------------
# export -- from .pt weights at a chosen input size (needs ultralytics/torch)
# ---------------------------------------------------------------------------
def export_model(arch: str, imgsz: int, out_path: str, fp16: bool) -> None:
    if imgsz % 32 != 0:
        raise SystemExit(f"--imgsz must be a multiple of 32; got {imgsz}")
    try:
        from ultralytics import YOLO
    except ImportError:
        raise SystemExit("export needs ultralytics: pip install ultralytics")

    weights = arch if arch.endswith(".pt") else f"{arch}.pt"
    model = YOLO(weights)  # auto-downloads weights on first use
    # opset=17: opset 20's Resize breaks the FP16 pass (see module docstring).
    exported = model.export(format="onnx", imgsz=imgsz, opset=17, dynamic=False)

    if fp16:
        # Export FP32, then derive a Tier-A FP16 drop-in via convert_fp16.
        convert_fp16(exported, out_path)
    else:
        shutil.copy(exported, out_path)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="mint_yolo.py", description="Mint / convert / validate FrameFlow YOLO models."
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_export = sub.add_parser("export", help="export .pt weights to ONNX at a chosen size")
    p_export.add_argument("--arch", required=True, help="e.g. yolov8n, yolo11n (or a .pt path)")
    p_export.add_argument("--imgsz", type=int, default=640, help="square input side (mult. of 32)")
    p_export.add_argument("--fp16", action="store_true", help="also produce a Tier-A FP16 drop-in")
    p_export.add_argument("--out", required=True)

    p_conv = sub.add_parser("convert", help="convert an existing ONNX to a quantized variant")
    p_conv.add_argument("--from-onnx", dest="from_onnx", required=True)
    mode = p_conv.add_mutually_exclusive_group(required=True)
    mode.add_argument("--fp16", action="store_true")
    mode.add_argument("--int8-dynamic", dest="int8_dynamic", action="store_true")
    mode.add_argument("--int8-static", dest="int8_static", action="store_true")
    p_conv.add_argument("--calib-dir", dest="calib_dir", help="image folder (required for --int8-static)")
    p_conv.add_argument("--out", required=True)

    p_val = sub.add_parser("validate", help="report FrameFlow compatibility of an ONNX")
    p_val.add_argument("--model", required=True)

    args = parser.parse_args(argv)

    if args.command == "export":
        export_model(args.arch, args.imgsz, args.out, args.fp16)
        ok, msg = describe_model(args.out)
        print(f"{args.out}: {msg}")
        return 0 if ok else 1

    if args.command == "convert":
        if args.fp16:
            convert_fp16(args.from_onnx, args.out)
        elif args.int8_dynamic:
            convert_int8_dynamic(args.from_onnx, args.out)
        else:
            if not args.calib_dir:
                raise SystemExit("--int8-static requires --calib-dir")
            convert_int8_static(args.from_onnx, args.out, args.calib_dir)
        ok, msg = describe_model(args.out)
        print(f"{args.out}: {msg}")
        return 0 if ok else 1

    if args.command == "validate":
        ok, msg = describe_model(args.model)
        print(f"{args.model}: {msg}")
        return 0 if ok else 1

    return 2


if __name__ == "__main__":
    raise SystemExit(main())
