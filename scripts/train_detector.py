#!/usr/bin/env python3
"""
Train the SnoutSpotter YOLOv8 detection model.

Downloads the latest completed export from S3, fixes dataset.yaml,
trains YOLOv8n, and exports the best checkpoint to ONNX.

Does NOT upload the model — run verify_onnx.py first, then upload via
the Models page in the dashboard.

Usage:
    # Train using the latest export from S3
    python scripts/train_detector.py

    # Use a specific export zip already downloaded
    python scripts/train_detector.py --zip path/to/export.zip

    # Override training hyperparameters
    python scripts/train_detector.py --epochs 150 --batch 32

    # Keep the extracted dataset after training (default: cleaned up)
    python scripts/train_detector.py --keep-dataset

Requirements:
    pip install ultralytics boto3
    CUDA-capable GPU recommended (CPU training will be very slow)
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

import boto3

REGION = "eu-west-1"
EXPORTS_PREFIX = "training-exports/"
EXPORTS_TABLE = "snout-spotter-exports"

# Defaults — match what RunInference/Function.cs expects
DEFAULT_EPOCHS = 100
DEFAULT_BATCH = 16
DEFAULT_IMGSZ = 640
BASE_MODEL = "yolov8n.pt"


def get_bucket(s3) -> str:
    for b in s3.list_buckets()["Buckets"]:
        if b["Name"].startswith("snout-spotter-"):
            return b["Name"]
    raise RuntimeError("Could not find snout-spotter-* bucket")


def get_latest_export(dynamodb, s3, bucket: str) -> tuple[str, str]:
    """
    Find the latest completed export and return (export_id, s3_key).
    Queries the exports DynamoDB table for completed exports.
    """
    print("Looking up latest completed export...")
    response = dynamodb.scan(
        TableName=EXPORTS_TABLE,
        FilterExpression="#s = :complete",
        ExpressionAttributeNames={"#s": "status"},
        ExpressionAttributeValues={":complete": {"S": "complete"}},
    )

    items = response.get("Items", [])
    if not items:
        raise RuntimeError("No completed exports found. Trigger an export from the dashboard first.")

    # Sort by created_at descending, pick the latest
    items.sort(key=lambda x: x.get("created_at", {}).get("S", ""), reverse=True)
    latest = items[0]
    export_id = latest["export_id"]["S"]
    s3_key = latest["s3_key"]["S"]

    total = latest.get("total_images", {}).get("N", "?")
    my_dog = latest.get("my_dog_count", {}).get("N", "?")
    not_my_dog = latest.get("not_my_dog_count", {}).get("N", "?")
    created = latest.get("created_at", {}).get("S", "?")

    print(f"  Export ID  : {export_id}")
    print(f"  Created    : {created}")
    print(f"  Images     : {total} total ({my_dog} my_dog, {not_my_dog} other+background)")
    print(f"  S3 key     : {s3_key}")

    return export_id, s3_key


def download_export(s3, bucket: str, s3_key: str, dest_path: str):
    print(f"\nDownloading s3://{bucket}/{s3_key} ...")
    s3.download_file(bucket, s3_key, dest_path)
    size_mb = os.path.getsize(dest_path) / (1024 * 1024)
    print(f"  Downloaded {size_mb:.1f} MB")


def extract_and_fix(zip_path: str, dataset_dir: Path) -> Path:
    """
    Extract the export zip and fix dataset.yaml so the path is absolute.
    The exported yaml has `path: .` which only works if you run training
    from inside the dataset directory — this makes it work from anywhere.
    """
    print(f"\nExtracting to {dataset_dir} ...")
    dataset_dir.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(zip_path, "r") as zf:
        zf.extractall(dataset_dir)

    yaml_path = dataset_dir / "dataset.yaml"
    if not yaml_path.exists():
        raise RuntimeError(f"dataset.yaml not found in export zip")

    # Fix path: . → absolute path to dataset_dir
    content = yaml_path.read_text()
    fixed = content.replace("path: .", f"path: {dataset_dir.as_posix()}")
    yaml_path.write_text(fixed)

    print(f"  Fixed dataset.yaml path → {dataset_dir.as_posix()}")

    # Report dataset stats
    train_images = list((dataset_dir / "images" / "train").glob("*.jpg"))
    val_images   = list((dataset_dir / "images" / "val").glob("*.jpg"))
    print(f"  Train images : {len(train_images)}")
    print(f"  Val images   : {len(val_images)}")

    return yaml_path


def check_cuda():
    """Report GPU availability."""
    try:
        import torch
        if torch.cuda.is_available():
            name = torch.cuda.get_device_name(0)
            vram = torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
            print(f"  GPU : {name} ({vram:.1f} GB VRAM)  ✓")
            return True
        else:
            print("  GPU : not available — training will run on CPU (slow)")
            return False
    except ImportError:
        print("  GPU : torch not importable directly — Ultralytics will handle device selection")
        return False


def train(yaml_path: Path, output_dir: Path, epochs: int, batch: int, imgsz: int) -> Path:
    """Run YOLOv8 training via the ultralytics CLI."""
    print(f"\nStarting training...")
    print(f"  Base model : {BASE_MODEL}")
    print(f"  Epochs     : {epochs}")
    print(f"  Batch size : {batch}")
    print(f"  Image size : {imgsz}")
    print(f"  Output dir : {output_dir}")

    cmd = [
        sys.executable, "-m", "ultralytics",
        "train",
        f"data={yaml_path}",
        f"model={BASE_MODEL}",
        f"epochs={epochs}",
        f"batch={batch}",
        f"imgsz={imgsz}",
        f"project={output_dir}",
        "name=train",
        "exist_ok=True",
    ]

    print(f"\n  Command: {' '.join(cmd)}\n")
    result = subprocess.run(cmd)
    if result.returncode != 0:
        raise RuntimeError(f"Training failed (exit code {result.returncode})")

    weights_dir = output_dir / "train" / "weights"
    best_pt = weights_dir / "best.pt"
    if not best_pt.exists():
        raise RuntimeError(f"best.pt not found at {best_pt} — training may have failed")

    print(f"\n  Training complete. Best weights: {best_pt}")
    return best_pt


def export_onnx(best_pt: Path, imgsz: int) -> Path:
    """Export best.pt to ONNX opset 12 — the format RunInference expects."""
    print(f"\nExporting to ONNX (opset 12)...")

    cmd = [
        sys.executable, "-m", "ultralytics",
        "export",
        f"model={best_pt}",
        "format=onnx",
        "opset=12",
        f"imgsz={imgsz}",
        "simplify=False",   # keep raw [1, 4+nc, 8400] output — required by RunInference
    ]

    print(f"  Command: {' '.join(cmd)}\n")
    result = subprocess.run(cmd)
    if result.returncode != 0:
        raise RuntimeError(f"ONNX export failed (exit code {result.returncode})")

    onnx_path = best_pt.with_suffix(".onnx")
    if not onnx_path.exists():
        raise RuntimeError(f"Expected ONNX at {onnx_path} but not found")

    size_mb = onnx_path.stat().st_size / (1024 * 1024)
    print(f"  Exported: {onnx_path} ({size_mb:.1f} MB)")
    return onnx_path


def copy_to_output(onnx_path: Path, export_id: str) -> Path:
    """Copy the ONNX to scripts/ with a meaningful name for easy identification."""
    scripts_dir = Path(__file__).parent
    dest = scripts_dir / f"detector_{export_id[:8]}.onnx"
    shutil.copy2(onnx_path, dest)
    print(f"\n  Saved to: {dest}")
    return dest


def main():
    parser = argparse.ArgumentParser(description="Train the SnoutSpotter YOLOv8 detection model")
    parser.add_argument("--zip", help="Path to a local export zip (skips S3 download)")
    parser.add_argument("--epochs", type=int, default=DEFAULT_EPOCHS,
                        help=f"Training epochs (default: {DEFAULT_EPOCHS})")
    parser.add_argument("--batch", type=int, default=DEFAULT_BATCH,
                        help=f"Batch size (default: {DEFAULT_BATCH})")
    parser.add_argument("--imgsz", type=int, default=DEFAULT_IMGSZ,
                        help=f"Image size (default: {DEFAULT_IMGSZ})")
    parser.add_argument("--keep-dataset", action="store_true",
                        help="Keep the extracted dataset directory after training")
    args = parser.parse_args()

    print("=" * 60)
    print("SnoutSpotter — YOLOv8 detection model training")
    print("=" * 60)

    print("\n--- Environment ---")
    check_cuda()

    work_dir = Path(tempfile.mkdtemp(prefix="snoutspotter-train-"))
    dataset_dir = work_dir / "dataset"
    output_dir = work_dir / "runs"
    export_id = "local"

    try:
        # Get the export zip
        if args.zip:
            zip_path = args.zip
            print(f"\nUsing local zip: {zip_path}")
        else:
            s3 = boto3.client("s3", region_name=REGION)
            dynamodb = boto3.client("dynamodb", region_name=REGION)
            bucket = get_bucket(s3)
            print(f"\n--- Latest export ---")
            export_id, s3_key = get_latest_export(dynamodb, s3, bucket)
            zip_path = str(work_dir / "export.zip")
            download_export(s3, bucket, s3_key, zip_path)

        # Extract and fix dataset.yaml
        print("\n--- Dataset ---")
        yaml_path = extract_and_fix(zip_path, dataset_dir)

        # Train
        print("\n--- Training ---")
        best_pt = train(yaml_path, output_dir, args.epochs, args.batch, args.imgsz)

        # Export to ONNX
        print("\n--- ONNX export ---")
        onnx_path = export_onnx(best_pt, args.imgsz)

        # Copy result to scripts/ with a recognisable name
        final_path = copy_to_output(onnx_path, export_id)

        print("\n" + "=" * 60)
        print("Training complete.")
        print(f"  Model : {final_path}")
        print()
        print("Next steps:")
        print(f"  1. Verify  : python scripts/verify_onnx.py --model-path {final_path} --sample-count 5")
        print(f"  2. Upload  : use the Models page in the dashboard")
        print("=" * 60)

    finally:
        if not args.keep_dataset and work_dir.exists():
            shutil.rmtree(work_dir, ignore_errors=True)


if __name__ == "__main__":
    main()
