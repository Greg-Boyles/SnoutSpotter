"""
Local classifier model tester.

Downloads the active classifier from S3 and runs inference on test images
to diagnose the "everything is my_dog" problem.

Usage:
    # Test with images from S3 keyframes
    python scripts/test_classifier.py

    # Test with local image files
    python scripts/test_classifier.py path/to/image1.jpg path/to/image2.jpg

    # Test with a specific model version
    python scripts/test_classifier.py --model-version v2.0

Requirements:
    pip install onnxruntime numpy pillow boto3
"""

import argparse
import sys
import os
import tempfile
from pathlib import Path

import boto3
import numpy as np
import onnxruntime as ort
from PIL import Image

BUCKET_NAME = None  # resolved at runtime
REGION = "eu-west-1"
MODEL_KEY_ACTIVE = "models/dog-classifier/best.onnx"
ACTIVE_JSON_KEY = "models/dog-classifier/active.json"
KEYFRAMES_PREFIX = "keyframes/"

# ImageNet normalization (must match Function.cs)
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
TARGET_SIZE = 224


def get_bucket_name(s3):
    """Find the snout-spotter bucket."""
    response = s3.list_buckets()
    for bucket in response["Buckets"]:
        if bucket["Name"].startswith("snout-spotter-"):
            return bucket["Name"]
    raise RuntimeError("Could not find snout-spotter-* bucket")


def download_model(s3, bucket, model_key, local_dir):
    local_path = os.path.join(local_dir, os.path.basename(model_key))
    if os.path.exists(local_path):
        print(f"  Using cached model: {local_path}")
        return local_path
    print(f"  Downloading s3://{bucket}/{model_key} ...")
    s3.download_file(bucket, model_key, local_path)
    print(f"  Saved to {local_path}")
    return local_path


def preprocess(image_path):
    """Replicate the C# preprocessing: resize 224x224, ImageNet normalize, NCHW."""
    img = Image.open(image_path).convert("RGB")
    img = img.resize((TARGET_SIZE, TARGET_SIZE), Image.BILINEAR)
    arr = np.array(img, dtype=np.float32) / 255.0  # HWC, [0,1]
    arr = (arr - MEAN) / STD
    arr = arr.transpose(2, 0, 1)  # CHW
    return np.expand_dims(arr, axis=0)  # NCHW


def softmax(x):
    e = np.exp(x - np.max(x))
    return e / e.sum()


def run_inference(session, image_path):
    tensor = preprocess(image_path)
    input_name = session.get_inputs()[0].name
    outputs = session.run(None, {input_name: tensor})
    logits = outputs[0][0]  # first (only) batch item
    return logits


def print_model_info(session):
    print("\n=== Model Info ===")
    for inp in session.get_inputs():
        print(f"  Input:  {inp.name}  shape={inp.shape}  type={inp.type}")
    for out in session.get_outputs():
        print(f"  Output: {out.name}  shape={out.shape}  type={out.type}")
    print()


def classify_and_report(session, image_path, label=""):
    logits = run_inference(session, image_path)
    probs = softmax(logits)
    num_classes = len(logits)

    # Match the C# logic: binary [not_my_dog, my_dog]
    if num_classes == 2:
        is_my_dog = logits[1] > logits[0]
        prediction = "my_dog" if is_my_dog else "other_dog"
        confidence = probs[1] if is_my_dog else probs[0]
    elif num_classes == 3:
        # Possible 3-class: [my_dog, other_dog, no_dog] or similar
        pred_idx = np.argmax(logits)
        prediction = f"class_{pred_idx}"
        confidence = probs[pred_idx]
    else:
        pred_idx = np.argmax(logits)
        prediction = f"class_{pred_idx}"
        confidence = probs[pred_idx]

    name = label or os.path.basename(image_path)
    print(f"  {name}")
    print(f"    Raw logits:    {logits}")
    print(f"    Softmax probs: {probs}")
    print(f"    Prediction:    {prediction} (confidence={confidence:.4f})")
    if num_classes == 2:
        print(f"    Margin:        logit[1]-logit[0] = {logits[1] - logits[0]:.4f}")
    print()

    return prediction, confidence, logits


def fetch_sample_keyframes(s3, bucket, max_images=10):
    """Grab a sample of keyframe images from S3."""
    print(f"\nFetching up to {max_images} sample keyframes from S3...")
    paginator = s3.get_paginator("list_objects_v2")
    keys = []
    for page in paginator.paginate(Bucket=bucket, Prefix=KEYFRAMES_PREFIX, MaxKeys=200):
        for obj in page.get("Contents", []):
            if obj["Key"].lower().endswith((".jpg", ".jpeg", ".png")):
                keys.append(obj["Key"])
            if len(keys) >= max_images:
                break
        if len(keys) >= max_images:
            break

    if not keys:
        print("  No keyframes found in S3!")
        return []

    local_dir = tempfile.mkdtemp(prefix="snoutspotter-test-")
    local_paths = []
    for key in keys:
        local_path = os.path.join(local_dir, key.replace("/", "_"))
        print(f"  Downloading {key}...")
        s3.download_file(bucket, key, local_path)
        local_paths.append((local_path, key))

    return local_paths


def main():
    parser = argparse.ArgumentParser(description="Test SnoutSpotter classifier locally")
    parser.add_argument("images", nargs="*", help="Local image paths to test")
    parser.add_argument("--model-version", help="Specific model version (e.g. v2.0)")
    parser.add_argument("--model-path", help="Path to a local .onnx model file")
    parser.add_argument("--sample-count", type=int, default=10,
                        help="Number of S3 keyframes to sample (default: 10)")
    args = parser.parse_args()

    # Load model
    if args.model_path:
        model_path = args.model_path
        print(f"Using local model: {model_path}")
    else:
        s3 = boto3.client("s3", region_name=REGION)
        bucket = get_bucket_name(s3)
        print(f"Bucket: {bucket}")

        # Show active model info
        try:
            import json
            resp = s3.get_object(Bucket=bucket, Key=ACTIVE_JSON_KEY)
            active_info = json.loads(resp["Body"].read())
            print(f"Active model: {json.dumps(active_info, indent=2)}")
        except Exception:
            print("Could not read active.json")

        model_key = MODEL_KEY_ACTIVE
        if args.model_version:
            model_key = f"models/dog-classifier/versions/{args.model_version}.onnx"

        cache_dir = os.path.join(tempfile.gettempdir(), "snoutspotter-models")
        os.makedirs(cache_dir, exist_ok=True)
        model_path = download_model(s3, bucket, model_key, cache_dir)

    session = ort.InferenceSession(model_path)
    print_model_info(session)

    results = []

    # Test local images
    if args.images:
        print("=== Testing local images ===")
        for img_path in args.images:
            if not os.path.exists(img_path):
                print(f"  SKIP: {img_path} not found")
                continue
            pred, conf, logits = classify_and_report(session, img_path)
            results.append((os.path.basename(img_path), pred, conf, logits))
    else:
        # Test S3 keyframes
        s3 = boto3.client("s3", region_name=REGION)
        bucket = get_bucket_name(s3)
        samples = fetch_sample_keyframes(s3, bucket, args.sample_count)
        if samples:
            print("\n=== Testing S3 keyframes ===")
            for local_path, s3_key in samples:
                pred, conf, logits = classify_and_report(session, local_path, label=s3_key)
                results.append((s3_key, pred, conf, logits))

    # Summary
    if results:
        print("=== Summary ===")
        my_dog_count = sum(1 for _, p, _, _ in results if p == "my_dog")
        other_count = len(results) - my_dog_count
        print(f"  my_dog:    {my_dog_count}/{len(results)}")
        print(f"  other_dog: {other_count}/{len(results)}")

        logits_array = np.array([l for _, _, _, l in results])
        if logits_array.shape[1] >= 2:
            margins = logits_array[:, 1] - logits_array[:, 0]
            print(f"\n  Logit margin (logit[1]-logit[0]) stats:")
            print(f"    Mean:   {margins.mean():.4f}")
            print(f"    Std:    {margins.std():.4f}")
            print(f"    Min:    {margins.min():.4f}")
            print(f"    Max:    {margins.max():.4f}")
            print(f"    Median: {np.median(margins):.4f}")
            if margins.mean() > 1.0:
                print("\n  ⚠ Model is strongly biased toward my_dog (logit[1]).")
                print("    Possible causes:")
                print("    - Training data imbalance (too many my_dog samples)")
                print("    - Not enough negative examples (other dogs / no dogs)")
                print("    - Overfitting to training data")
                print("    - Output neuron order doesn't match [not_my_dog, my_dog] assumption")


if __name__ == "__main__":
    main()
