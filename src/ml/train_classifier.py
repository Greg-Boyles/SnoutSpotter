#!/usr/bin/env python3
"""
Train the SnoutSpotter dog classifier (MobileNetV3-Small).

Takes a directory of cropped dog images organised as:
    train/my_dog/*.jpg
    train/other_dog/*.jpg
    val/my_dog/*.jpg
    val/other_dog/*.jpg

Exports best checkpoint to ONNX with input [1, 3, 224, 224] and output [1, 2].

Usage:
    # Train on an extracted classification export
    python src/ml/train_classifier.py --data /path/to/extracted/export

    # Use a zip file (will extract automatically)
    python src/ml/train_classifier.py --zip /path/to/export.zip

    # Override hyperparameters
    python src/ml/train_classifier.py --data /path --epochs 80 --batch 64

Requirements:
    pip install torch torchvision onnx boto3 pillow
    CUDA-capable GPU recommended
"""

import argparse
import json
import os
import shutil
import sys
import tempfile
import time
import zipfile
from pathlib import Path

import boto3
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import DataLoader
from torchvision import datasets, models, transforms


CLASS_NAMES = ["my_dog", "other_dog"]


def parse_args():
    parser = argparse.ArgumentParser(description="Train SnoutSpotter dog classifier")
    parser.add_argument("--data", type=str, help="Path to extracted classification dataset")
    parser.add_argument("--zip", type=str, help="Path to classification export zip")
    parser.add_argument("--epochs", type=int, default=50, help="Training epochs (default: 50)")
    parser.add_argument("--batch", type=int, default=32, help="Batch size (default: 32)")
    parser.add_argument("--imgsz", type=int, default=224, help="Input image size (default: 224)")
    parser.add_argument("--lr", type=float, default=0.001, help="Learning rate (default: 0.001)")
    parser.add_argument("--workers", type=int, default=4, help="DataLoader workers (default: 4)")
    parser.add_argument("--patience", type=int, default=10, help="Early stopping patience (default: 10)")
    parser.add_argument("--keep-dataset", action="store_true", help="Don't delete extracted dataset after training")
    return parser.parse_args()


def download_latest_export(data_dir: str) -> str:
    """Download the latest completed classification export from S3."""
    print("Querying DynamoDB for latest classification export...")
    dynamodb = boto3.resource("dynamodb", region_name="eu-west-1")
    table = dynamodb.Table("snout-spotter-exports")

    response = table.scan()
    exports = [
        e for e in response["Items"]
        if e.get("status") == "complete" and e.get("config", {}).get("export_type") == "classification"
    ]

    if not exports:
        print("ERROR: No completed classification exports found. Create one from the Training Exports page.")
        sys.exit(1)

    exports.sort(key=lambda e: e.get("created_at", ""), reverse=True)
    latest = exports[0]
    s3_key = latest["s3_key"]
    export_id = latest["export_id"]
    print(f"Latest classification export: {export_id} ({s3_key})")

    s3 = boto3.client("s3", region_name="eu-west-1")
    sts = boto3.client("sts", region_name="eu-west-1")
    account_id = sts.get_caller_identity()["Account"]
    bucket = f"snout-spotter-{account_id}"

    zip_path = os.path.join(data_dir, f"{export_id}.zip")
    print(f"Downloading s3://{bucket}/{s3_key} ...")
    s3.download_file(bucket, s3_key, zip_path)
    return zip_path


def extract_dataset(zip_path: str, data_dir: str) -> str:
    """Extract zip to data_dir and return the root directory."""
    print(f"Extracting {zip_path} ...")
    with zipfile.ZipFile(zip_path, "r") as zf:
        zf.extractall(data_dir)
    os.remove(zip_path)

    # Check for expected structure
    train_dir = os.path.join(data_dir, "train")
    val_dir = os.path.join(data_dir, "val")
    if not os.path.isdir(train_dir) or not os.path.isdir(val_dir):
        print(f"ERROR: Expected train/ and val/ directories in {data_dir}")
        sys.exit(1)

    for split in ["train", "val"]:
        for cls in CLASS_NAMES:
            cls_dir = os.path.join(data_dir, split, cls)
            count = len(list(Path(cls_dir).glob("*.jpg"))) if os.path.isdir(cls_dir) else 0
            print(f"  {split}/{cls}: {count} images")

    return data_dir


def build_dataloaders(data_dir: str, img_size: int, batch_size: int, workers: int):
    """Create train and val DataLoaders with augmentation."""
    train_transform = transforms.Compose([
        transforms.RandomResizedCrop(img_size, scale=(0.8, 1.0)),
        transforms.RandomHorizontalFlip(),
        transforms.ColorJitter(brightness=0.2, contrast=0.2, saturation=0.2, hue=0.1),
        transforms.ToTensor(),
    ])

    val_transform = transforms.Compose([
        transforms.Resize((img_size, img_size)),
        transforms.ToTensor(),
    ])

    train_dataset = datasets.ImageFolder(os.path.join(data_dir, "train"), transform=train_transform)
    val_dataset = datasets.ImageFolder(os.path.join(data_dir, "val"), transform=val_transform)

    # Verify class mapping matches expected order
    print(f"Class mapping: {train_dataset.class_to_idx}")

    train_loader = DataLoader(train_dataset, batch_size=batch_size, shuffle=True,
                              num_workers=workers, pin_memory=True)
    val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False,
                            num_workers=workers, pin_memory=True)

    return train_loader, val_loader, train_dataset.class_to_idx


def train(args, data_dir: str):
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using device: {device}")

    train_loader, val_loader, class_to_idx = build_dataloaders(
        data_dir, args.imgsz, args.batch, args.workers)

    # MobileNetV3-Small pretrained on ImageNet
    model = models.mobilenet_v3_small(weights=models.MobileNet_V3_Small_Weights.IMAGENET1K_V1)
    model.classifier[-1] = nn.Linear(model.classifier[-1].in_features, len(CLASS_NAMES))
    model = model.to(device)

    criterion = nn.CrossEntropyLoss()
    optimizer = optim.Adam(model.parameters(), lr=args.lr)
    scheduler = optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs)

    best_val_acc = 0.0
    best_epoch = 0
    patience_counter = 0
    output_dir = os.path.join(data_dir, "runs")
    os.makedirs(output_dir, exist_ok=True)
    best_model_path = os.path.join(output_dir, "best.pt")

    start_time = time.time()

    for epoch in range(1, args.epochs + 1):
        # Train
        model.train()
        train_loss = 0.0
        train_correct = 0
        train_total = 0

        for images, labels in train_loader:
            images, labels = images.to(device), labels.to(device)
            optimizer.zero_grad()
            outputs = model(images)
            loss = criterion(outputs, labels)
            loss.backward()
            optimizer.step()

            train_loss += loss.item() * images.size(0)
            _, predicted = outputs.max(1)
            train_total += labels.size(0)
            train_correct += predicted.eq(labels).sum().item()

        train_loss /= train_total
        train_acc = train_correct / train_total

        # Validate
        model.eval()
        val_loss = 0.0
        val_correct = 0
        val_total = 0
        tp = fp = fn = 0  # for my_dog F1

        my_dog_idx = class_to_idx.get("my_dog", 0)

        with torch.no_grad():
            for images, labels in val_loader:
                images, labels = images.to(device), labels.to(device)
                outputs = model(images)
                loss = criterion(outputs, labels)

                val_loss += loss.item() * images.size(0)
                _, predicted = outputs.max(1)
                val_total += labels.size(0)
                val_correct += predicted.eq(labels).sum().item()

                # F1 for my_dog class
                tp += ((predicted == my_dog_idx) & (labels == my_dog_idx)).sum().item()
                fp += ((predicted == my_dog_idx) & (labels != my_dog_idx)).sum().item()
                fn += ((predicted != my_dog_idx) & (labels == my_dog_idx)).sum().item()

        val_loss /= val_total
        val_acc = val_correct / val_total
        precision = tp / (tp + fp) if (tp + fp) > 0 else 0.0
        recall = tp / (tp + fn) if (tp + fn) > 0 else 0.0
        f1 = 2 * precision * recall / (precision + recall) if (precision + recall) > 0 else 0.0

        scheduler.step()

        elapsed = time.time() - start_time
        eta = elapsed / epoch * (args.epochs - epoch)

        # Parseable progress line for training agent
        print(f"EPOCH {epoch}/{args.epochs} "
              f"train_loss={train_loss:.4f} val_loss={val_loss:.4f} "
              f"accuracy={val_acc:.4f} f1={f1:.4f} "
              f"precision={precision:.4f} recall={recall:.4f} "
              f"elapsed={elapsed:.0f}s eta={eta:.0f}s")

        # Save best model
        if val_acc > best_val_acc:
            best_val_acc = val_acc
            best_epoch = epoch
            patience_counter = 0
            torch.save(model.state_dict(), best_model_path)
        else:
            patience_counter += 1

        if patience_counter >= args.patience:
            print(f"Early stopping at epoch {epoch} (best accuracy {best_val_acc:.4f} at epoch {best_epoch})")
            break

    print(f"\nBest validation accuracy: {best_val_acc:.4f} at epoch {best_epoch}")

    # Export to ONNX
    print("Exporting best model to ONNX...")
    model.load_state_dict(torch.load(best_model_path, map_location=device, weights_only=True))
    model.eval()

    dummy_input = torch.randn(1, 3, args.imgsz, args.imgsz, device=device)
    onnx_path = os.path.join(output_dir, "best.onnx")

    torch.onnx.export(
        model,
        dummy_input,
        onnx_path,
        opset_version=12,
        input_names=["input"],
        output_names=["output"],
        dynamic_axes=None,
    )

    model_size_mb = os.path.getsize(onnx_path) / (1024 * 1024)
    print(f"ONNX model saved: {onnx_path} ({model_size_mb:.1f} MB)")

    # Write summary
    summary = {
        "best_epoch": best_epoch,
        "best_accuracy": round(best_val_acc, 4),
        "model_size_mb": round(model_size_mb, 1),
        "classes": CLASS_NAMES,
        "input_size": args.imgsz,
        "epochs_trained": epoch,
        "total_epochs": args.epochs,
    }
    summary_path = os.path.join(output_dir, "summary.json")
    with open(summary_path, "w") as f:
        json.dump(summary, f, indent=2)
    print(f"Summary: {json.dumps(summary)}")

    return onnx_path


def main():
    args = parse_args()
    cleanup_dir = None

    try:
        if args.data:
            data_dir = args.data
        elif args.zip:
            data_dir = tempfile.mkdtemp(prefix="snoutspotter-cls-")
            cleanup_dir = data_dir
            extract_dataset(args.zip, data_dir)
        else:
            data_dir = tempfile.mkdtemp(prefix="snoutspotter-cls-")
            cleanup_dir = data_dir
            zip_path = download_latest_export(data_dir)
            extract_dataset(zip_path, data_dir)

        onnx_path = train(args, data_dir)
        print(f"\nTraining complete. Model: {onnx_path}")
        print("Next steps:")
        print(f"  1. Verify: python src/ml/verify_classifier.py --model-path {onnx_path}")
        print("  2. Upload via the Models page (Dog Classifier tab)")
        print("  3. Set inference.pipeline_mode to 'two_stage' in Server Settings")

    finally:
        if cleanup_dir and not args.keep_dataset:
            shutil.rmtree(cleanup_dir, ignore_errors=True)


if __name__ == "__main__":
    main()
