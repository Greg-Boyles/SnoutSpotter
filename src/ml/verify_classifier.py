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

CLASS_NAMES = ["my_dog", "other_dog"]
EXPECTED_INPUT_SHAPE = [1, 3, 224, 224]
EXPECTED_OUTPUT_CLASSES = 2


def check_model(model_path: str, test_images: list[str]):
    failures = []

    print(f"Loading model: {model_path}")
    session = ort.InferenceSession(model_path)

    # Check input shape
    inp = session.get_inputs()[0]
    print(f"  Input: name={inp.name}, shape={inp.shape}, dtype={inp.type}")
    if inp.shape != EXPECTED_INPUT_SHAPE:
        failures.append(f"Input shape {inp.shape} != expected {EXPECTED_INPUT_SHAPE}")

    # Check output shape
    out = session.get_outputs()[0]
    print(f"  Output: name={out.name}, shape={out.shape}")
    if out.shape != [1, EXPECTED_OUTPUT_CLASSES]:
        failures.append(f"Output shape {out.shape} != expected [1, {EXPECTED_OUTPUT_CLASSES}]")

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
