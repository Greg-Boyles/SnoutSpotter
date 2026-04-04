#!/usr/bin/env python3
"""SnoutSpotter YOLOv8 dog detector fine-tuning script.

Run on SageMaker or any machine with a GPU:
    python train_detector.py --data /path/to/dataset.yaml --epochs 100
"""

import argparse
from pathlib import Path

from ultralytics import YOLO


def create_dataset_yaml(data_dir: str, output_path: str) -> str:
    """Create a YOLO dataset YAML config if one doesn't exist."""
    content = f"""
path: {data_dir}
train: images/train
val: images/val

names:
  0: my_dog
  1: other_dog
"""
    path = Path(output_path)
    path.write_text(content.strip())
    return str(path)


def resolve_dataset_yaml(data_yaml: str) -> str:
    """Ensure dataset.yaml has an absolute path so YOLO can find the images."""
    import yaml

    yaml_path = Path(data_yaml).resolve()
    with open(yaml_path) as f:
        cfg = yaml.safe_load(f)

    dataset_path = Path(cfg.get("path", "."))
    if not dataset_path.is_absolute():
        cfg["path"] = str(yaml_path.parent / dataset_path)
        with open(yaml_path, "w") as f:
            yaml.dump(cfg, f, default_flow_style=False)

    return str(yaml_path)


def train(
    data_yaml: str,
    model: str = "yolov8n.pt",
    epochs: int = 100,
    imgsz: int = 640,
    batch: int = 16,
    project: str = "runs/detect",
    name: str = "snout-spotter",
):
    """Fine-tune YOLOv8 on the dog detection dataset."""
    data_yaml = resolve_dataset_yaml(data_yaml)
    yolo = YOLO(model)

    results = yolo.train(
        data=data_yaml,
        epochs=epochs,
        imgsz=imgsz,
        batch=batch,
        project=project,
        name=name,
        patience=20,       # Early stopping
        save=True,
        save_period=10,    # Save checkpoint every 10 epochs
        plots=True,
        verbose=True,
    )

    # Export to ONNX for inference Lambda
    best_model = YOLO(f"{project}/{name}/weights/best.pt")
    best_model.export(format="onnx", imgsz=imgsz)

    print(f"\nTraining complete!")
    print(f"Best weights: {project}/{name}/weights/best.pt")
    print(f"ONNX export:  {project}/{name}/weights/best.onnx")
    print(f"\nUpload ONNX to S3: aws s3 cp {project}/{name}/weights/best.onnx s3://YOUR_BUCKET/models/dog-detector/best.onnx")

    return results


def main():
    parser = argparse.ArgumentParser(description="Train SnoutSpotter dog detector")
    parser.add_argument("--data", required=True, help="Path to dataset YAML")
    parser.add_argument("--model", default="yolov8n.pt", help="Base model")
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--batch", type=int, default=16)
    args = parser.parse_args()

    train(
        data_yaml=args.data,
        model=args.model,
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
    )


if __name__ == "__main__":
    main()
