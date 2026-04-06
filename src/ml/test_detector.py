#!/usr/bin/env python3
"""
Test the SnoutSpotter YOLOv8 detection model (models/dog-classifier/best.onnx).

Downloads the active model and sample keyframes from S3, runs inference, and
reports detections with bounding boxes and confidence scores.

Preprocessing and output parsing mirror RunInference/Function.cs exactly:
  - Resize to 640x640, RGB /255, NCHW tensor
  - Output: [1, 4+num_classes, 8400]
  - Confidence threshold: 0.4 (same default as Lambda)

NOTE: Replaces the old test_classifier.py, which tested a 224x224 image
classifier (ImageNet normalisation, softmax logits). That approach was
abandoned. The current RunInference Lambda uses YOLOv8 detection format.

Usage:
    # Sample keyframes from S3 using the active deployed model
    python scripts/test_detector.py

    # Test specific local images
    python scripts/test_detector.py image1.jpg image2.jpg

    # Use a local model file (e.g. before uploading)
    python scripts/test_detector.py --model-path runs/detect/train/weights/best.onnx

    # Adjust confidence threshold
    python scripts/test_detector.py --confidence 0.3

    # Pull more S3 samples
    python scripts/test_detector.py --sample-count 20

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
KEYFRAMES_PREFIX = "keyframes/"
YOLO_INPUT_SIZE = 640
CONFIDENCE_THRESHOLD = 0.4
CLASS_NAMES = ["my_dog", "other_dog"]

# Priority order for upgrading detection type — matches UpgradeDetectionType in Function.cs
DETECTION_PRIORITY = {"none": 0, "no_dog": 1, "other_dog": 2, "my_dog": 3}


def get_bucket(s3) -> str:
    for b in s3.list_buckets()["Buckets"]:
        if b["Name"].startswith("snout-spotter-"):
            return b["Name"]
    raise RuntimeError("Could not find snout-spotter-* bucket")


def download_model(s3, bucket: str) -> str:
    cache_dir = Path(tempfile.gettempdir()) / "snoutspotter-models"
    cache_dir.mkdir(exist_ok=True)
    local_path = cache_dir / "best.onnx"
    if local_path.exists():
        print(f"Using cached model: {local_path}")
        return str(local_path)
    print(f"Downloading s3://{bucket}/{MODEL_KEY} ...")
    s3.download_file(bucket, MODEL_KEY, str(local_path))
    print(f"Saved to {local_path}")
    return str(local_path)


def fetch_keyframes(s3, bucket: str, count: int) -> list[tuple[str, str]]:
    """Download a sample of keyframes from S3. Returns list of (local_path, s3_key)."""
    print(f"\nFetching {count} keyframes from S3...")
    keys = []
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=bucket, Prefix=KEYFRAMES_PREFIX, MaxKeys=500):
        for obj in page.get("Contents", []):
            if obj["Key"].lower().endswith((".jpg", ".jpeg", ".png")):
                keys.append(obj["Key"])
            if len(keys) >= count:
                break
        if len(keys) >= count:
            break

    if not keys:
        print("No keyframes found in S3.")
        return []

    tmp_dir = Path(tempfile.mkdtemp(prefix="snoutspotter-kf-"))
    results = []
    for key in keys:
        local_path = tmp_dir / key.replace("/", "_")
        s3.download_file(bucket, key, str(local_path))
        results.append((str(local_path), key))
        print(f"  Downloaded {key}")

    return results


def preprocess(image_path: str) -> tuple[np.ndarray, int, int]:
    """
    Load and preprocess an image for YOLOv8 inference.
    Mirrors RunInference/Function.cs: resize 640x640, RGB /255, NCHW.
    Returns (tensor [1,3,640,640], original_width, original_height).
    """
    img = Image.open(image_path).convert("RGB")
    orig_w, orig_h = img.size
    resized = img.resize((YOLO_INPUT_SIZE, YOLO_INPUT_SIZE), Image.BILINEAR)
    arr = np.array(resized, dtype=np.float32) / 255.0  # HWC [0,1]
    arr = arr.transpose(2, 0, 1)                        # CHW
    tensor = np.expand_dims(arr, axis=0)                # NCHW [1,3,640,640]
    return tensor, orig_w, orig_h


def parse_detections(output: np.ndarray, orig_w: int, orig_h: int,
                     confidence: float) -> list[dict]:
    """
    Parse YOLOv8 output tensor into detections.
    Mirrors RunInference/Function.cs exactly.

    output shape: [1, 4+num_classes, 8400]
    Returns list of {label, confidence, x, y, width, height} in original pixel coords.
    """
    scale_x = orig_w / YOLO_INPUT_SIZE
    scale_y = orig_h / YOLO_INPUT_SIZE

    num_classes = output.shape[1] - 4
    num_anchors = output.shape[2]

    detections = []
    for i in range(num_anchors):
        # Find best class — mirrors the inner loop in Function.cs
        best_class = 0
        best_conf = output[0, 4, i]
        for c in range(1, num_classes):
            conf = output[0, 4 + c, i]
            if conf > best_conf:
                best_class = c
                best_conf = conf

        if best_conf < confidence:
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


def upgrade_detection_type(current: str, candidate: str) -> str:
    """Mirrors UpgradeDetectionType in Function.cs."""
    if DETECTION_PRIORITY.get(candidate, 0) > DETECTION_PRIORITY.get(current, 0):
        return candidate
    return current


def keyframe_label(detections: list[dict]) -> str:
    label = "no_dog"
    for d in detections:
        label = upgrade_detection_type(label, d["label"])
    return label


def run_on_image(session: ort.InferenceSession, image_path: str,
                 label: str, confidence: float) -> dict:
    tensor, orig_w, orig_h = preprocess(image_path)
    input_name = session.get_inputs()[0].name
    output = session.run(None, {input_name: tensor})[0]

    detections = parse_detections(output, orig_w, orig_h, confidence)
    overall = keyframe_label(detections)

    name = label or os.path.basename(image_path)
    print(f"\n  {name}  [{orig_w}x{orig_h}]")
    print(f"    Keyframe label : {overall}")
    if detections:
        for d in detections:
            print(f"    Detection      : {d['label']}  conf={d['confidence']:.3f}  "
                  f"box=({d['x']:.0f},{d['y']:.0f},{d['width']:.0f}x{d['height']:.0f})")
    else:
        print(f"    Detection      : none above threshold ({confidence})")

    return {"name": name, "label": overall, "detections": detections}


def print_model_info(session: ort.InferenceSession):
    print("\n=== Model info ===")
    for inp in session.get_inputs():
        print(f"  Input  : {inp.name}  shape={inp.shape}  type={inp.type}")
    for out in session.get_outputs():
        print(f"  Output : {out.name}  shape={out.shape}  type={out.type}")

    out_shape = session.get_outputs()[0].shape
    if len(out_shape) == 3 and out_shape[2] == 8400:
        num_classes = out_shape[1] - 4
        print(f"  Format : YOLOv8 detection — {num_classes} class(es): "
              f"{CLASS_NAMES[:num_classes]}")
    else:
        print(f"  WARNING: unexpected output shape {out_shape} — "
              f"expected [1, 4+num_classes, 8400]")


def main():
    parser = argparse.ArgumentParser(description="Test the SnoutSpotter YOLOv8 detection model")
    parser.add_argument("images", nargs="*", help="Local image paths to test")
    parser.add_argument("--model-path", help="Local .onnx model file (skips S3 download)")
    parser.add_argument("--confidence", type=float, default=CONFIDENCE_THRESHOLD,
                        help=f"Confidence threshold (default: {CONFIDENCE_THRESHOLD})")
    parser.add_argument("--sample-count", type=int, default=10,
                        help="Number of S3 keyframes to sample (default: 10)")
    args = parser.parse_args()

    # Load model
    if args.model_path:
        model_path = args.model_path
        print(f"Using local model: {model_path}")
    else:
        s3 = boto3.client("s3", region_name=REGION)
        bucket = get_bucket(s3)
        print(f"Bucket : {bucket}")
        model_path = download_model(s3, bucket)

    session = ort.InferenceSession(model_path)
    print_model_info(session)

    results = []

    if args.images:
        print("\n=== Local images ===")
        for path in args.images:
            if not os.path.exists(path):
                print(f"  SKIP: {path} not found")
                continue
            r = run_on_image(session, path, path, args.confidence)
            results.append(r)
    else:
        s3 = boto3.client("s3", region_name=REGION)
        bucket = get_bucket(s3)
        samples = fetch_keyframes(s3, bucket, args.sample_count)
        if not samples:
            sys.exit(1)
        print("\n=== S3 keyframes ===")
        for local_path, s3_key in samples:
            r = run_on_image(session, local_path, s3_key, args.confidence)
            results.append(r)

    if not results:
        return

    # Summary
    counts = {"my_dog": 0, "other_dog": 0, "no_dog": 0}
    for r in results:
        counts[r["label"]] = counts.get(r["label"], 0) + 1
    total_detections = sum(len(r["detections"]) for r in results)

    print("\n=== Summary ===")
    print(f"  Images tested    : {len(results)}")
    print(f"  my_dog           : {counts.get('my_dog', 0)}")
    print(f"  other_dog        : {counts.get('other_dog', 0)}")
    print(f"  no_dog           : {counts.get('no_dog', 0)}")
    print(f"  Total detections : {total_detections}")
    print(f"  Confidence       : {args.confidence}")


if __name__ == "__main__":
    main()
