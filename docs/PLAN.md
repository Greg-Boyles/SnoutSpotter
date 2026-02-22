# SnoutSpotter — Pi Zero Motion-Triggered Video Capture & Dog Detection ML Platform

## Problem Statement
Build an end-to-end system where a Raspberry Pi Zero 2 W captures video when motion is detected, uploads clips to AWS, and uses those clips to train an ML model that can (1) detect dogs in general, and (2) identify a specific dog. Includes a web dashboard for viewing clips and inference results.

## System Overview
Four subsystems:
1. **Edge device** (Pi Zero 2 W + Camera Module 3) — motion detection, recording, upload
2. **Cloud storage & pipeline** (AWS) — ingest, store, label, process
3. **ML training & inference** — dog detector → specific dog classifier
4. **Web dashboard** — ASP.NET Core API + React frontend

```
[Pi Zero 2W + Camera]
        │
        │ motion detected → record clip
        ▼
   [Local Buffer]
        │
        │ boto3 multipart upload
        ▼
   [S3: raw-clips/]
        │
        │ S3 event notification
        ▼
   [Lambda: ingest]
        │
        ├──► [DynamoDB: clip metadata]
        └──► [S3: keyframes/]  (extracted JPEGs)
                   │
           ┌───────┴───────┐
           ▼               ▼
   [Label + Train]   [Web Dashboard]
   YOLOv8 + MobileNet  React + .NET API
           │
           ▼
   [Lambda: inference]
           │
           ▼
   [DynamoDB: detection results]
```

## Phase 1: Hardware & Pi OS Setup
**Hardware:**
- Raspberry Pi Zero 2 W
- Pi Camera Module 3 (or 3 NoIR for night vision)
- 15→22 pin CSI adapter cable
- 32GB+ MicroSD card
- 5V 2.5A power supply
- Optional: weatherproof enclosure if outdoors, IR floodlight if using NoIR

**OS & base config:**
- Flash Raspberry Pi OS Lite (64-bit, Bookworm) via Raspberry Pi Imager
- During imaging: set hostname, enable SSH, configure WiFi credentials
- Boot and SSH in, then:
```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y python3-pip python3-venv python3-picamera2 python3-opencv libcamera-apps ffmpeg
```
- Verify camera works: `libcamera-hello --timeout 5000`

## Phase 2: Motion Detection & Recording Script
**Approach:** Frame-differencing with `picamera2` + OpenCV. Lightweight enough for Pi Zero 2 W.

**How it works:**
1. Continuously capture low-res preview frames (640×480, ~5 FPS)
2. Convert to grayscale, apply Gaussian blur, compute absolute difference between consecutive frames
3. Threshold the diff → count changed pixels
4. If changed pixels exceed `MOTION_THRESHOLD`, begin recording at full resolution (1080p H.264)
5. Keep recording for `POST_MOTION_BUFFER` seconds after last motion
6. Cap clip length at `CLIP_MAX_LENGTH` to avoid huge files
7. Save as `{ISO-timestamp}_{duration}s.mp4` to `/home/pi/clips/`

**Key config (via a `config.yaml`):**
- `MOTION_THRESHOLD`: 5000 (tunable — number of changed pixels)
- `PREVIEW_RESOLUTION`: 640×480
- `RECORD_RESOLUTION`: 1920×1080
- `POST_MOTION_BUFFER`: 10 seconds
- `CLIP_MAX_LENGTH`: 60 seconds
- `DETECTION_FPS`: 5

## Phase 3: Upload to AWS S3
**S3 bucket layout:**
```
s3://snout-spotter-{account-id}/
├── raw-clips/{YYYY}/{MM}/{DD}/{timestamp}_{duration}s.mp4
├── keyframes/{YYYY}/{MM}/{DD}/{timestamp}_{frame_num}.jpg
├── labeled-data/
│   ├── detection/          # YOLO-format labels for dog detection
│   └── classification/     # cropped dog images sorted into class folders
└── models/
    ├── dog-detector/       # YOLOv8 weights
    └── dog-classifier/     # MobileNet weights
```

**Upload logic (`uploader.py`):**
- Watch `/home/pi/clips/` for new `.mp4` files
- Use `boto3` multipart upload (handles large files, resumable)
- On success: delete local file
- On failure: exponential backoff retry (max 5 attempts), keep file locally
- Log all uploads to a local SQLite DB as a fallback ledger

**IAM setup:**
- Create an IAM user `snout-spotter-pi` with a policy scoped to `s3:PutObject` on the bucket only
- Store credentials in `/home/pi/.aws/credentials` (or use IoT Core certificates for better security later)

**S3 lifecycle rules:**
- Transition to Infrequent Access after 30 days
- Transition to Glacier after 90 days
- No auto-deletion (you want to keep training data)

## Phase 4: AWS Ingest Pipeline
**Trigger:** S3 Event Notification → Lambda on every new `.mp4` in `raw-clips/`

**Lambda function (`ingest-clip`) — .NET 8 (C#):**
1. Extract metadata from the S3 key (timestamp, duration)
2. Write record to DynamoDB `clips` table via `Amazon.DynamoDBv2`
3. Use FFmpeg (via Lambda layer) to extract 1 keyframe per 5 seconds
4. Save keyframes to `s3://snout-spotter-{id}/keyframes/...` via `AWSSDK.S3`
5. Update DynamoDB record with keyframe count

Use `Amazon.Lambda.Annotations` for cleaner handler definitions. Deploy as a native AOT compiled Lambda for fast cold starts.

**DynamoDB `clips` table:**
- Partition key: `clip_id` (ISO timestamp)
- Attributes: `s3_key`, `timestamp`, `duration_s`, `keyframe_count`, `labeled` (bool), `labels` (map)

**Infrastructure as Code:** AWS CDK (C#/.NET).

## Phase 5: Data Labeling
You need labeled data before training. Two rounds:

**Round 1 — Dog detection (object detection bounding boxes):**
- Tool: **Label Studio** (free, self-hosted) or **Roboflow** (free tier, more polished UI)
- Task: Draw bounding boxes around dogs in keyframe images
- Export format: YOLO format (`.txt` per image with `class x_center y_center width height`)
- Target: **500–1000 labeled images** minimum for good transfer learning results
- Tip: Pre-trained YOLOv8 on COCO already knows "dog" — you can use it to auto-label, then correct mistakes manually.

**Round 2 — Specific dog classification:**
- Take the dog bounding box crops from Round 1 detections
- Sort into folders: `my_dog/`, `other_dog/`, `not_dog/`
- Target: **200+ images per class** minimum

## Phase 6: ML Model Training
**Stage 1 — Dog detector (object detection):**
- Model: **YOLOv8n** (nano) from the `ultralytics` package
- Fine-tune on your labeled keyframes
- Training environment: SageMaker `ml.g4dn.xlarge` (~$0.53/hr) or any machine with a GPU
- Expected training time: ~1–2 hours

**Stage 2 — Specific dog classifier (binary classification):**
- Model: **MobileNetV3-Small** (via torchvision or `timm`)
- Input: cropped dog images from Stage 1
- Classes: `my_dog` vs `not_my_dog`
- Training: same SageMaker instance, ~30 min

**Model artifacts** saved to `s3://snout-spotter-{id}/models/`.

## Phase 7: Inference
**Option A — Cloud inference (start here):**
- Lambda (.NET 8) triggered on new clip upload
- Pull model from S3, run ONNX inference on keyframes via `Microsoft.ML.OnnxRuntime`
- If dog detected → run classifier → log result to DynamoDB

**Option B — Edge inference (future optimization):**
- Export YOLOv8n to ONNX or TFLite
- Run on Pi Zero 2 W (~2–5 FPS, fine for non-realtime)
- Only upload clips that contain dogs → saves bandwidth and S3 costs

## Phase 8: Web Dashboard
**Backend — ASP.NET Core 8 Web API (C#):**
- Hosted on AWS App Runner
- Endpoints: clips CRUD, detections, stats, presigned URLs for video/image playback
- Auth: Amazon Cognito (or simple API key if single-user)

**Frontend — React (TypeScript):**
- Hosted on S3 + CloudFront
- Pages: Dashboard, Clips Browser, Clip Detail, Detections, System Health
- UI: Tailwind CSS + shadcn/ui

## Phase 9: Monitoring & Ops
- Pi heartbeat to CloudWatch every 5 min
- CloudWatch alarms for upload gaps
- S3 lifecycle policies + budget alerts
- Periodic model retraining as labeled data grows

## Phase 10: CI/CD Pipelines (GitHub Actions)
One workflow per service with path filters:
- `deploy-infra.yml` — CDK deploy on `src/infra/**`
- `deploy-api.yml` — Docker → ECR → App Runner on `src/api/**`
- `deploy-lambdas.yml` — Native AOT → CDK deploy on `src/lambdas/**`
- `deploy-web.yml` — npm build → S3 → CloudFront on `src/web/**`
- `deploy-ml.yml` — Package → S3 on `src/ml/**`

All use OIDC-based AWS auth. PRs run lint/build/test; deploy on merge to `main`.

## Estimated Costs
- **Total ongoing (no real-time inference endpoint):** ~$5–15/month

## Implementation Order
1. Hardware: assemble Pi + camera, verify video capture works
2. Pi software: implement `motion_detector.py`, test locally
3. CDK infra: scaffold `CoreStack` (S3, DynamoDB, IAM), deploy
4. Pi uploader: implement `uploader.py`, wire up systemd services, test upload
5. Ingest pipeline: implement IngestClip Lambda, deploy via `IngestStack`
6. Web API: build ASP.NET Core API, deploy to App Runner via `ApiStack`
7. Web frontend: build React dashboard, deploy to S3 + CloudFront via `WebStack`
8. CI/CD: set up all 5 GitHub Actions workflows
9. Collect data: let the system run for 1–2 weeks to gather clips
10. Label data: label keyframes using Label Studio or Roboflow
11. Train Stage 1: fine-tune YOLOv8n dog detector
12. Train Stage 2: train MobileNet dog classifier on crops
13. Deploy inference: implement RunInference Lambda, deploy via `InferenceStack`
14. Iterate: review results, add more labels, retrain
