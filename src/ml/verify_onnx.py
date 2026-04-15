#!/usr/bin/env python3
"""
Verify an ONNX model is compatible with RunInference/Function.cs before deploying.

Checks:
  1. Output tensor shape is [1, 4+num_classes, 8400]
  2. num_classes is 2 (my_dog, other_dog) as expected by the Lambda
  3. Input is [1, 3, 640, 640] float32
  4. Preprocessing matches Function.cs (640x640 resize, RGB /255, no ImageNet norm)
  5. Detection parsing produces valid bounding boxes in pixel coords
  6. Runs end-to-end on a synthetic image and real keyframes if available

Exit code 0 = all checks passed, 1 = one or more failures.

Usage:
    # Verify the active deployed model (downloads from S3)
    python scripts/verify_onnx.py

    # Verify a local model before uploading
    python scripts/verify_onnx.py --model-path runs/detect/train/weights/best.onnx

    # Also run against specific keyframe images
    python scripts/verify_onnx.py --model-path best.onnx image1.jpg image2.jpg

    # Verify against a sample of S3 keyframes
    python scripts/verify_onnx.py --sample-count 5

Requirements:
    pip install onnxruntime numpy pillow boto3
"""

import argparse
import os
import sys
import tempfile
from pathlib import Path

import boto3
import numpy as np
import onnxruntime as ort
from PIL import Image

REGION = "eu-west-1"
MODEL_KEY = "models/dog-classifier/best.onnx"
CLASS_MAP_KEY = "models/dog-classifier/class_map.json"
KEYFRAMES_PREFIX = "keyframes/"

# Must match RunInference/Function.cs
YOLO_INPUT_SIZE = 640
CONFIDENCE_THRESHOLD = 0.4
FALLBACK_CLASS_NAMES = ["my_dog", "other_dog"]
CLASS_NAMES = FALLBACK_CLASS_NAMES  # will be overridden if class_map.json exists
DETECTION_PRIORITY = {"none": 0, "no_dog": 1, "other_dog": 2, "my_dog": 3}
# pet-* labels get priority 3 (same as my_dog) — handled dynamically in upgrade_detection_type

PASS = "PASS"
FAIL = "FAIL"
WARN = "WARN"


def check(label: str, ok: bool, detail: str = "") -> bool:
    status = PASS if ok else FAIL
    line = f"  [{status}] {label}"
    if detail:
        line += f" — {detail}"
    print(line)
    return ok


def get_bucket(s3) -> str:
    for b in s3.list_buckets()["Buckets"]:
        if b["Name"].startswith("snout-spotter-"):
            return b["Name"]
    raise RuntimeError("Could not find snout-spotter-* bucket")


def download_model(s3, bucket: str) -> str:
    cache_dir = Path(tempfile.gettempdir()) / "snoutspotter-models"
    cache_dir.mkdir(exist_ok=True)
    local_path = cache_dir / "verify_best.onnx"
    print(f"Downloading s3://{bucket}/{MODEL_KEY} ...")
    s3.download_file(bucket, MODEL_KEY, str(local_path))
    return str(local_path)


def load_class_map(s3=None, bucket: str = "", local_path: str = "") -> list[str] | None:
    """Load class_map.json from S3 or local path. Returns None if not found."""
    import json
    if local_path:
        p = Path(local_path).parent / "class_map.json"
        if p.exists():
            print(f"Loading class map from {p}")
            return json.loads(p.read_text())
    if s3 and bucket:
        try:
            import io
            obj = s3.get_object(Bucket=bucket, Key=CLASS_MAP_KEY)
            data = json.loads(obj["Body"].read())
            print(f"Loaded class map from S3: {data}")
            return data
        except Exception:
            pass
    return None


def preprocess(image_path: str) -> tuple[np.ndarray, int, int]:
    """
    Mirrors RunInference/Function.cs preprocessing exactly:
      - Load as RGB
      - Resize to 640x640 (bilinear)
      - Convert to float32, divide by 255
      - Reshape to NCHW [1, 3, 640, 640]
    No ImageNet normalisation — that was the old classifier approach.
    """
    img = Image.open(image_path).convert("RGB")
    orig_w, orig_h = img.size
    resized = img.resize((YOLO_INPUT_SIZE, YOLO_INPUT_SIZE), Image.BILINEAR)
    arr = np.array(resized, dtype=np.float32) / 255.0
    arr = arr.transpose(2, 0, 1)
    tensor = np.expand_dims(arr, axis=0)
    return tensor, orig_w, orig_h


def parse_detections(output: np.ndarray, orig_w: int, orig_h: int) -> list[dict]:
    """
    Mirrors RunInference/Function.cs detection parsing exactly.
    output: [1, 4+num_classes, 8400]
    """
    scale_x = orig_w / YOLO_INPUT_SIZE
    scale_y = orig_h / YOLO_INPUT_SIZE

    num_classes = output.shape[1] - 4
    num_anchors = output.shape[2]

    detections = []
    for i in range(num_anchors):
        best_class = 0
        best_conf = output[0, 4, i]
        for c in range(1, num_classes):
            conf = output[0, 4 + c, i]
            if conf > best_conf:
                best_class = c
                best_conf = conf

        if best_conf < CONFIDENCE_THRESHOLD:
            continue

        cx = output[0, 0, i] * scale_x
        cy = output[0, 1, i] * scale_y
        w  = output[0, 2, i] * scale_x
        h  = output[0, 3, i] * scale_y

        label = CLASS_NAMES[best_class] if best_class < len(CLASS_NAMES) else f"class_{best_class}"
        detections.append({
            "label":      label,
            "confidence": float(best_conf),
            "x":          max(0.0, float(cx - w / 2)),
            "y":          max(0.0, float(cy - h / 2)),
            "width":      float(w),
            "height":     float(h),
        })
    return detections


def get_detection_priority(label: str) -> int:
    if label.startswith("pet-"):
        return 3
    return DETECTION_PRIORITY.get(label, 0)


def upgrade_detection_type(current: str, candidate: str) -> str:
    if get_detection_priority(candidate) > get_detection_priority(current):
        return candidate
    return current


def keyframe_label(detections: list[dict]) -> str:
    label = "no_dog"
    for d in detections:
        label = upgrade_detection_type(label, d["label"])
    return label


def verify_model_shape(session: ort.InferenceSession) -> bool:
    """Check 1–4: input/output shapes and types."""
    print("\n--- Shape and format checks ---")
    all_ok = True

    inputs = session.get_inputs()
    all_ok &= check("Single input node", len(inputs) == 1,
                    f"found {len(inputs)}")

    if inputs:
        inp = inputs[0]
        shape = inp.shape
        type_ok = inp.type == "tensor(float)"
        all_ok &= check("Input type is float32", type_ok, inp.type)

        shape_ok = (
            len(shape) == 4
            and (shape[0] in (1, "batch_size", None))
            and shape[1] == 3
            and (shape[2] in (YOLO_INPUT_SIZE, -1, None))
            and (shape[3] in (YOLO_INPUT_SIZE, -1, None))
        )
        all_ok &= check(
            f"Input shape is [1, 3, {YOLO_INPUT_SIZE}, {YOLO_INPUT_SIZE}]",
            shape_ok, str(shape),
        )

    outputs = session.get_outputs()
    all_ok &= check("Single output node", len(outputs) == 1,
                    f"found {len(outputs)}")

    if outputs:
        out = outputs[0]
        out_shape = out.shape
        shape_ok = (
            len(out_shape) == 3
            and (out_shape[0] in (1, "batch_size", None))
            and out_shape[2] == 8400
        )
        all_ok &= check("Output shape is [1, ?, 8400]", shape_ok, str(out_shape))

        if shape_ok:
            num_classes = out_shape[1] - 4
            expected = len(CLASS_NAMES)
            classes_ok = num_classes == expected
            all_ok &= check(
                f"num_classes == {expected} ({', '.join(CLASS_NAMES)})",
                classes_ok,
                f"got {num_classes} class(es) — output dim 1 = {out_shape[1]}",
            )
        else:
            all_ok = False

    return all_ok


def verify_synthetic_image(session: ort.InferenceSession) -> bool:
    """Check 5: end-to-end run on a blank synthetic image."""
    print("\n--- Synthetic image (640x640 grey) ---")
    all_ok = True

    # Create a neutral grey image — should produce no detections
    grey = Image.new("RGB", (640, 480), color=(128, 128, 128))
    tmp = Path(tempfile.mktemp(suffix=".jpg"))
    grey.save(str(tmp))

    try:
        tensor, orig_w, orig_h = preprocess(str(tmp))
        input_name = session.get_inputs()[0].name
        output = session.run(None, {input_name: tensor})[0]

        all_ok &= check("Output tensor has 3 dims", output.ndim == 3,
                        str(output.shape))
        all_ok &= check("Output dim 2 == 8400", output.shape[2] == 8400,
                        str(output.shape))
        all_ok &= check("Output values are finite",
                        bool(np.isfinite(output).all()),
                        f"min={output.min():.4f} max={output.max():.4f}")

        detections = parse_detections(output, orig_w, orig_h)
        all_ok &= check(f"No detections on grey image (threshold={CONFIDENCE_THRESHOLD})",
                        len(detections) == 0,
                        f"got {len(detections)} detection(s)")
    finally:
        tmp.unlink(missing_ok=True)

    return all_ok


def verify_real_images(session: ort.InferenceSession,
                       image_paths: list[tuple[str, str]]) -> bool:
    """Check 6: run on real keyframes, report what RunInference would output."""
    if not image_paths:
        return True

    print("\n--- Real image inference (mirrors RunInference/Function.cs) ---")
    all_ok = True

    for local_path, label in image_paths:
        try:
            tensor, orig_w, orig_h = preprocess(local_path)
            input_name = session.get_inputs()[0].name
            output = session.run(None, {input_name: tensor})[0]

            values_ok = bool(np.isfinite(output).all())
            all_ok &= check(f"Output finite — {label}", values_ok)

            detections = parse_detections(output, orig_w, orig_h)
            overall = keyframe_label(detections)

            print(f"    Lambda result  : detection_type={overall}, "
                  f"detections={len(detections)}")
            for d in detections:
                print(f"      {d['label']}  conf={d['confidence']:.3f}  "
                      f"box=({d['x']:.0f},{d['y']:.0f},{d['width']:.0f}x{d['height']:.0f})")
        except Exception as e:
            all_ok &= check(f"Inference succeeded — {label}", False, str(e))

    return all_ok


def fetch_sample_keyframes(s3, bucket: str, count: int) -> list[tuple[str, str]]:
    print(f"Fetching {count} sample keyframes from S3...")
    keys = []
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=bucket, Prefix=KEYFRAMES_PREFIX, MaxKeys=200):
        for obj in page.get("Contents", []):
            if obj["Key"].lower().endswith((".jpg", ".jpeg", ".png")):
                keys.append(obj["Key"])
            if len(keys) >= count:
                break
        if len(keys) >= count:
            break

    if not keys:
        print("  No keyframes found in S3 — skipping real-image checks")
        return []

    tmp_dir = Path(tempfile.mkdtemp(prefix="snoutspotter-verify-"))
    results = []
    for key in keys:
        local_path = tmp_dir / key.replace("/", "_")
        s3.download_file(bucket, key, str(local_path))
        results.append((str(local_path), key))
        print(f"  Downloaded {key}")
    return results


def main():
    parser = argparse.ArgumentParser(
        description="Verify an ONNX model is compatible with RunInference/Function.cs")
    parser.add_argument("images", nargs="*", help="Local images to run inference on")
    parser.add_argument("--model-path", help="Local .onnx file (skips S3 download)")
    parser.add_argument("--sample-count", type=int, default=0,
                        help="Download N keyframes from S3 to verify against (default: 0)")
    args = parser.parse_args()

    print("=" * 60)
    print("SnoutSpotter ONNX model verification")
    print("=" * 60)

    # Load model
    global CLASS_NAMES
    if args.model_path:
        model_path = args.model_path
        print(f"Model : {model_path}")
        cm = load_class_map(local_path=model_path)
        if cm:
            CLASS_NAMES = cm
    else:
        s3 = boto3.client("s3", region_name=REGION)
        bucket = get_bucket(s3)
        print(f"Bucket: {bucket}")
        model_path = download_model(s3, bucket)
        cm = load_class_map(s3=s3, bucket=bucket)
        if cm:
            CLASS_NAMES = cm

    try:
        session = ort.InferenceSession(model_path)
    except Exception as e:
        print(f"\n[FAIL] Could not load model: {e}")
        sys.exit(1)

    # Collect real images from args + optional S3 sample
    real_images: list[tuple[str, str]] = []
    for path in args.images:
        if os.path.exists(path):
            real_images.append((path, path))
        else:
            print(f"WARNING: {path} not found — skipping")

    if args.sample_count > 0:
        s3 = boto3.client("s3", region_name=REGION)
        bucket = get_bucket(s3)
        real_images.extend(fetch_sample_keyframes(s3, bucket, args.sample_count))

    # Run checks
    shape_ok   = verify_model_shape(session)
    synth_ok   = verify_synthetic_image(session)
    real_ok    = verify_real_images(session, real_images)

    all_ok = shape_ok and synth_ok and real_ok

    print("\n" + "=" * 60)
    if all_ok:
        print("RESULT: PASS — model is compatible with RunInference/Function.cs")
    else:
        print("RESULT: FAIL — see above for details")
    print("=" * 60)

    sys.exit(0 if all_ok else 1)


if __name__ == "__main__":
    main()
