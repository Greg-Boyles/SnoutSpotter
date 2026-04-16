#!/usr/bin/env python3
"""
Train the SnoutSpotter YOLOv8 detection model.

Downloads the latest completed export from S3, fixes dataset.yaml,
fine-tunes YOLOv8n, and exports the best checkpoint to ONNX.

Does NOT upload the model — run verify_onnx.py first, then upload via
the Models page in the dashboard.

Usage:
    # Train using the latest export from S3
    python src/ml/train_detector.py

    # Use a specific export zip already downloaded
    python src/ml/train_detector.py --zip path/to/export.zip

    # Resume an interrupted training run
    python src/ml/train_detector.py --resume runs/detect/train/weights/last.pt

    # Override training hyperparameters
    python src/ml/train_detector.py --epochs 150 --batch 32

    # Keep the extracted dataset after training (default: cleaned up)
    python src/ml/train_detector.py --keep-dataset

Requirements:
    pip install ultralytics boto3
    CUDA-capable GPU recommended (CPU training will be very slow)
"""

import argparse
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path

import boto3
from ultralytics import YOLO

REGION = "eu-west-1"
EXPORTS_TABLE = "snout-spotter-exports"

# Defaults — match what RunInference/Function.cs expects
DEFAULT_EPOCHS = 100
DEFAULT_BATCH = 16
DEFAULT_IMGSZ = 640
DEFAULT_WORKERS = 4
BASE_MODEL = "yolov8n.pt"


def get_bucket(s3) -> str:
    for b in s3.list_buckets()["Buckets"]:
        if b["Name"].startswith("snout-spotter-"):
            return b["Name"]
    raise RuntimeError("Could not find snout-spotter-* bucket")


def get_latest_export(dynamodb, s3, bucket: str) -> tuple[str, str]:
    print("Looking up latest completed export...")
    response = dynamodb.scan(
        TableName=EXPORTS_TABLE,
        FilterExpression="#s = :complete",
        ExpressionAttributeNames={"#s": "status"},
        ExpressionAttributeValues={":complete": {"S": "complete"}},
    )

    items = response.get("Items", [])
    if not items:
        raise RuntimeError(
            "No completed exports found. Trigger an export from the dashboard first."
        )

    items.sort(key=lambda x: x.get("created_at", {}).get("S", ""), reverse=True)
    latest = items[0]
    export_id = latest["export_id"]["S"]
    s3_key = latest["s3_key"]["S"]

    total    = latest.get("total_images",    {}).get("N", "?")
    created  = latest.get("created_at",      {}).get("S", "?")

    # Show per-pet counts if available, fall back to legacy my_dog/not_my_dog
    pet_counts = latest.get("pet_counts", {}).get("M", {})
    if pet_counts:
        counts_str = ", ".join(f"{k}: {v.get('N', '?')}" for k, v in pet_counts.items())
    else:
        my_dog   = latest.get("my_dog_count",    {}).get("N", "?")
        not_mine = latest.get("not_my_dog_count",{}).get("N", "?")
        counts_str = f"my_dog: {my_dog}, other+background: {not_mine}"

    print(f"  Export ID  : {export_id}")
    print(f"  Created    : {created}")
    print(f"  Images     : {total} total ({counts_str})")
    print(f"  S3 key     : {s3_key}")
    return export_id, s3_key


def download_export(s3, bucket: str, s3_key: str, dest: str):
    print(f"\nDownloading s3://{bucket}/{s3_key} ...")
    s3.download_file(bucket, s3_key, dest)
    size_mb = Path(dest).stat().st_size / (1024 * 1024)
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
        raise RuntimeError("dataset.yaml not found in export zip")

    # Fix path: . → absolute path
    content = yaml_path.read_text()
    fixed = content.replace("path: .", f"path: {dataset_dir.as_posix()}")
    yaml_path.write_text(fixed)
    print(f"  Fixed dataset.yaml path → {dataset_dir.as_posix()}")

    train_images = list((dataset_dir / "images" / "train").glob("*.jpg"))
    val_images   = list((dataset_dir / "images" / "val").glob("*.jpg"))
    print(f"  Train images : {len(train_images)}")
    print(f"  Val images   : {len(val_images)}")

    return yaml_path


def check_cuda():
    try:
        import torch
        if torch.cuda.is_available():
            name = torch.cuda.get_device_name(0)
            vram = torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
            print(f"  GPU : {name} ({vram:.1f} GB VRAM)")
        else:
            print("  GPU : not available — training will run on CPU (slow)")
    except ImportError:
        pass


def train(yaml_path: Path, output_dir: Path, epochs: int, batch: int,
          imgsz: int, workers: int, resume: str | None) -> Path:
    # Use output_dir as the project root and "snoutspotter" as the run name.
    # Avoids nesting: passing project="runs/detect" conflicts with Ultralytics'
    # own default of runs/detect, causing it to nest as runs/detect/runs/detect/train.
    project = str(output_dir)
    run_name = "snoutspotter"

    if resume:
        print(f"\nResuming from {resume} ...")
        yolo = YOLO(resume)
        yolo.train(resume=True)
    else:
        print(f"\nStarting training...")
        print(f"  Base model : {BASE_MODEL}")
        print(f"  Epochs     : {epochs}  (early stop patience=20)")
        print(f"  Batch size : {batch}")
        print(f"  Image size : {imgsz}")
        yolo = YOLO(BASE_MODEL)
        yolo.train(
            data=str(yaml_path),
            epochs=epochs,
            imgsz=imgsz,
            batch=batch,
            workers=workers,
            project=project,
            name=run_name,
            exist_ok=True,
            patience=20,        # early stopping
            save=True,
            save_period=10,     # checkpoint every 10 epochs
            plots=True,
            verbose=True,
        )

    best_pt = output_dir / run_name / "weights" / "best.pt"
    if not best_pt.exists():
        raise RuntimeError(f"best.pt not found at {best_pt} — training may have failed")

    print(f"\n  Training complete. Best weights: {best_pt}")
    return best_pt


def export_onnx(best_pt: Path, imgsz: int) -> Path:
    """
    Export best.pt to ONNX opset 12.
    simplify=False keeps the raw [1, 4+nc, 8400] output required by RunInference/Function.cs.
    """
    print(f"\nExporting to ONNX (opset 12, simplify=False)...")
    yolo = YOLO(str(best_pt))
    yolo.export(format="onnx", imgsz=imgsz, opset=12, simplify=False)

    onnx_path = best_pt.with_suffix(".onnx")
    if not onnx_path.exists():
        raise RuntimeError(f"Expected ONNX at {onnx_path} but not found")

    size_mb = onnx_path.stat().st_size / (1024 * 1024)
    print(f"  Exported: {onnx_path} ({size_mb:.1f} MB)")
    return onnx_path


def main():
    parser = argparse.ArgumentParser(
        description="Train the SnoutSpotter YOLOv8 detection model"
    )
    parser.add_argument("--zip",     help="Local export zip (skips S3 download)")
    parser.add_argument("--data",    help="Path to an already-extracted dataset directory (skips download and extraction)")
    parser.add_argument("--resume",  help="Path to last.pt to resume interrupted training")
    parser.add_argument("--epochs",  type=int, default=DEFAULT_EPOCHS,
                        help=f"Training epochs (default: {DEFAULT_EPOCHS})")
    parser.add_argument("--batch",   type=int, default=DEFAULT_BATCH,
                        help=f"Batch size (default: {DEFAULT_BATCH})")
    parser.add_argument("--imgsz",   type=int, default=DEFAULT_IMGSZ,
                        help=f"Image size (default: {DEFAULT_IMGSZ})")
    parser.add_argument("--workers", type=int, default=DEFAULT_WORKERS,
                        help=f"Dataloader workers (default: {DEFAULT_WORKERS})")
    parser.add_argument("--keep-dataset", action="store_true",
                        help="Keep the extracted dataset directory after training")
    args = parser.parse_args()

    print("=" * 60)
    print("SnoutSpotter — YOLOv8 detection model training")
    print("=" * 60)

    print("\n--- Environment ---")
    check_cuda()

    work_dir    = Path(tempfile.mkdtemp(prefix="snoutspotter-train-"))
    dataset_dir = work_dir / "dataset"
    output_dir  = work_dir / "runs"   # absolute — avoids ultralytics prepending runs/detect
    export_id   = "local"

    try:
        if args.resume:
            # Resume doesn't need a dataset — just retrain from last checkpoint
            best_pt = train(None, output_dir, args.epochs, args.batch,
                            args.imgsz, args.workers, args.resume)
        else:
            if args.data:
                # Already-extracted dataset — just fix the yaml path in place
                dataset_dir = Path(args.data).resolve()
                output_dir  = dataset_dir / "runs"  # absolute, inside dataset dir so agent finds outputs
                print(f"\nUsing local dataset: {dataset_dir}")
                print("\n--- Dataset ---")
                yaml_path = dataset_dir / "dataset.yaml"
                if not yaml_path.exists():
                    raise RuntimeError(f"dataset.yaml not found in {dataset_dir}")
                content = yaml_path.read_text()
                if "path: ." in content:
                    yaml_path.write_text(content.replace("path: .", f"path: {dataset_dir.as_posix()}"))
                    print(f"  Fixed dataset.yaml path → {dataset_dir.as_posix()}")
                train_images = list((dataset_dir / "images" / "train").glob("*.jpg"))
                val_images   = list((dataset_dir / "images" / "val").glob("*.jpg"))
                print(f"  Train images : {len(train_images)}")
                print(f"  Val images   : {len(val_images)}")
            else:
                # Get the export zip
                if args.zip:
                    zip_path = args.zip
                    print(f"\nUsing local zip: {zip_path}")
                else:
                    s3       = boto3.client("s3",       region_name=REGION)
                    dynamodb = boto3.client("dynamodb", region_name=REGION)
                    bucket   = get_bucket(s3)
                    print(f"\n--- Latest export ---")
                    export_id, s3_key = get_latest_export(dynamodb, s3, bucket)
                    zip_path = str(work_dir / "export.zip")
                    download_export(s3, bucket, s3_key, zip_path)

                print("\n--- Dataset ---")
                yaml_path = extract_and_fix(zip_path, dataset_dir)

            print("\n--- Training ---")
            best_pt = train(yaml_path, output_dir, args.epochs, args.batch,
                            args.imgsz, args.workers, resume=None)

        print("\n--- ONNX export ---")
        onnx_path = export_onnx(best_pt, args.imgsz)

        # Copy to src/ml/ with a recognisable name
        ml_dir = Path(__file__).parent
        dest = ml_dir / f"detector_{export_id[:8]}.onnx"
        shutil.copy2(onnx_path, dest)

        print("\n" + "=" * 60)
        print("Training complete.")
        print(f"  Model : {dest}")
        print()
        print("Next steps:")
        print(f"  1. Verify  : python src/ml/verify_onnx.py --model-path {dest} --sample-count 5")
        print(f"  2. Upload  : use the Models page in the dashboard")
        print("=" * 60)

    finally:
        if not args.keep_dataset and work_dir.exists():
            shutil.rmtree(work_dir, ignore_errors=True)


if __name__ == "__main__":
    main()
