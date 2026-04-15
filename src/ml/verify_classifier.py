#!/usr/bin/env python3
"""
Verify an ONNX classifier model is compatible with RunInference two-stage pipeline.

Checks:
  1. Input shape is [1, 3, 224, 224] float32
  2. Output shape is [1, 2] (my_dog, other_dog)
  3. Runs on a synthetic image and produces finite outputs
  4. Optionally tests on real keyframe crops

Exit code 0 = all checks passed, 1 = one or more failures.

Usage:
    python src/ml/verify_classifier.py --model-path runs/best.onnx
    python src/ml/verify_classifier.py --model-path runs/best.onnx image1.jpg image2.jpg
"""

import argparse
import math
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image

FALLBACK_CLASS_NAMES = ["my_dog", "other_dog"]
CLASS_NAMES = FALLBACK_CLASS_NAMES  # overridden if class_map.json found
EXPECTED_INPUT_SHAPE = [1, 3, 224, 224]


def load_class_map(model_path: str) -> list[str] | None:
    """Load class_map.json from same directory as model."""
    import json
    p = Path(model_path).parent / "class_map.json"
    if p.exists():
        print(f"  Loaded class map from {p}")
        return json.loads(p.read_text())
    return None


def check_model(model_path: str, test_images: list[str]):
    global CLASS_NAMES
    failures = []

    print(f"Loading model: {model_path}")
    session = ort.InferenceSession(model_path)

    # Load class_map.json if available
    cm = load_class_map(model_path)
    if cm:
        CLASS_NAMES = cm

    expected_output_classes = len(CLASS_NAMES)

    # Check input shape
    inp = session.get_inputs()[0]
    print(f"  Input: name={inp.name}, shape={inp.shape}, dtype={inp.type}")
    if inp.shape != EXPECTED_INPUT_SHAPE:
        failures.append(f"Input shape {inp.shape} != expected {EXPECTED_INPUT_SHAPE}")

    # Check output shape
    out = session.get_outputs()[0]
    print(f"  Output: name={out.name}, shape={out.shape}")
    if out.shape != [1, expected_output_classes]:
        failures.append(f"Output shape {out.shape} != expected [1, {expected_output_classes}]")

    # Synthetic test (grey image)
    print("\n--- Synthetic grey image test ---")
    dummy = np.full((1, 3, EXPECTED_INPUT_SHAPE[2], EXPECTED_INPUT_SHAPE[3]), 0.5, dtype=np.float32)
    result = session.run(None, {inp.name: dummy})
    logits = result[0][0]
    print(f"  Logits: {logits}")

    if not all(math.isfinite(v) for v in logits):
        failures.append("Synthetic test produced non-finite logits")
    else:
        # Softmax
        max_val = max(logits)
        exps = [math.exp(v - max_val) for v in logits]
        total = sum(exps)
        probs = [e / total for e in exps]
        predicted = CLASS_NAMES[np.argmax(logits)]
        print(f"  Prediction: {predicted} (probs: {[f'{p:.3f}' for p in probs]})")
        print("  PASS: Synthetic test OK")

    # Real image tests
    for img_path in test_images:
        print(f"\n--- Testing: {img_path} ---")
        try:
            img = Image.open(img_path).convert("RGB")
            img = img.resize((EXPECTED_INPUT_SHAPE[3], EXPECTED_INPUT_SHAPE[2]))
            arr = np.array(img, dtype=np.float32) / 255.0
            tensor = arr.transpose(2, 0, 1)[np.newaxis, ...]  # NCHW

            result = session.run(None, {inp.name: tensor})
            logits = result[0][0]

            max_val = max(logits)
            exps = [math.exp(v - max_val) for v in logits]
            total = sum(exps)
            probs = [e / total for e in exps]
            predicted = CLASS_NAMES[np.argmax(logits)]
            print(f"  Prediction: {predicted} (probs: {[f'{p:.3f}' for p in probs]})")
        except Exception as e:
            failures.append(f"Failed on {img_path}: {e}")

    # Summary
    print(f"\n{'='*40}")
    if failures:
        print(f"FAILED: {len(failures)} issue(s)")
        for f in failures:
            print(f"  - {f}")
        return 1
    else:
        print("ALL CHECKS PASSED")
        print(f"Model is compatible with RunInference two-stage pipeline.")
        return 0


def main():
    parser = argparse.ArgumentParser(description="Verify classifier ONNX model")
    parser.add_argument("--model-path", required=True, help="Path to ONNX model")
    parser.add_argument("images", nargs="*", help="Optional test images")
    args = parser.parse_args()

    sys.exit(check_model(args.model_path, args.images))


if __name__ == "__main__":
    main()
