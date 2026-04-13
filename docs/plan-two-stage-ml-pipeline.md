# Plan: Two-Stage ML Pipeline (YOLO Detector + Classifier)

## Context

Single-stage YOLOv8 trained on `my_dog` vs `other_dog` achieves high precision (0.84) but terrible recall (~0.14), even with balanced datasets (2000/class). The fundamental problem is architectural: both classes are visually "dogs" and single-stage detection conflates localisation with fine-grained identity.

**Solution**: Two-stage pipeline — COCO-pretrained YOLO for finding all dogs (already works well via AutoLabel) + lightweight MobileNetV3-Small classifier on cropped dog patches for my_dog vs other_dog identity.

**No DynamoDB schema changes needed** — the output format (detection_type, keyframe_detections with label/confidence/boundingBox) stays identical. Downstream consumers (frontend, labels) require zero changes.

**What's new vs what's extended:**
- **RunInference Lambda** — extended to load a second model and run classification on crops
- **ExportDataset Lambda** — extended with a new "classification" export mode (existing detection export unchanged)
- **Training agent** — extended with `jobType` field to route to the correct script (existing detector training unchanged)
- **New files**: `train_classifier.py`, `verify_classifier.py`, `ClassifierProgressParser.cs`
- **No rebuilds** — existing training pipeline, agent infrastructure, and export system stay intact

---

## Phase 1 — Two-Stage Inference + Classifier Training Script

Get the two-stage RunInference Lambda working. Use the COCO-pretrained YOLO as the detector (no detector training needed — it already finds dogs reliably via AutoLabel). Train the classifier manually with a local script.

### Step 1.1 — Server settings for two-stage mode

**File:** `src/shared/SnoutSpotter.Contracts/ServerSettings.cs`

Add new settings:
```csharp
public const string InferencePipelineMode = "inference.pipeline_mode";  // "single" or "two_stage"
public const string ClassifierConfidenceThreshold = "inference.classifier_confidence_threshold";  // 0.5
public const string ClassifierInputSize = "inference.classifier_input_size";  // 224
public const string CropPaddingRatio = "inference.crop_padding_ratio";  // 0.1
```

Add to `All` dictionary with specs:
- `pipeline_mode`: select type, options `["single", "two_stage"]`, default `"single"`
- `classifier_confidence_threshold`: float 0.1–0.95, default 0.5
- `classifier_input_size`: int 128–512, default 224
- `crop_padding_ratio`: float 0.0–0.5, default 0.1

### Step 1.2 — Modify RunInference Lambda for two-stage

**File:** `src/lambdas/SnoutSpotter.Lambda.RunInference/Function.cs`

Key changes:
1. Rename `_session` → `_detectorSession`, add `_classifierSession`
2. Add env vars: `DETECTOR_MODEL_KEY` (defaults to COCO model, e.g. `models/yolov8m.onnx`), `CLASSIFIER_MODEL_KEY` (defaults to `models/dog-classifier/best.onnx`)
3. Keep existing `MODEL_KEY` env var for single-stage backward compat
4. Add `EnsureClassifierLoaded()` mirroring `EnsureModelLoaded()`
5. Read `pipeline_mode` from settings in `FunctionHandler`
6. When `pipeline_mode == "single"`: existing behavior unchanged (ClassNames = `["my_dog", "other_dog"]`, uses `MODEL_KEY`)
7. When `pipeline_mode == "two_stage"`:
   - Run COCO detector, filter for COCO class 16 (dog) — same logic as AutoLabel
   - For each dog detection, crop the original image at the bounding box (with padding %)
   - Resize crop to classifier input size (224x224), normalize RGB/255, NCHW
   - Run classifier: output `[1, 2]` tensor → argmax → `["my_dog", "other_dog"]`
   - Combined confidence: `detector_confidence * classifier_confidence`
   - Assemble same `KeyframeResult` format with the classified label

The detector is a COCO-pretrained YOLO (80 classes, we use class 16 only) — the same model AutoLabel already uses. No detector training needed initially.

### Step 1.3 — CDK env var changes

**File:** `src/infra/Stacks/InferenceStack.cs`

Add environment variables:
- `DETECTOR_MODEL_KEY` = `"models/yolov8m.onnx"` (COCO-pretrained, same as AutoLabel default)
- `CLASSIFIER_MODEL_KEY` = `"models/dog-classifier/best.onnx"`
- Keep `MODEL_KEY` for single-stage backward compat

### Step 1.4 — Create train_classifier.py

**File:** `src/ml/train_classifier.py` (new)

PyTorch training script for MobileNetV3-Small:
- Input: directory with `train/my_dog/*.jpg`, `train/other_dog/*.jpg`, `val/my_dog/*.jpg`, `val/other_dog/*.jpg`
- Loads torchvision `mobilenet_v3_small(pretrained=True)`
- Replaces final classifier head: `nn.Linear(576, 2)`
- Augmentation: RandomHorizontalFlip, ColorJitter, RandomResizedCrop
- Optimizer: Adam, lr=0.001, cosine annealing LR scheduler
- Early stopping (patience 10)
- Prints parseable progress: `EPOCH 5/50 train_loss=0.234 val_loss=0.189 accuracy=0.923 f1=0.891`
- Exports best model to ONNX: input `[1, 3, 224, 224]`, output `[1, 2]`
- Args: `--data`, `--epochs` (50), `--batch` (32), `--imgsz` (224), `--lr` (0.001), `--workers` (4)

### Step 1.5 — Create verify_classifier.py

**File:** `src/ml/verify_classifier.py` (new)

Validates classifier ONNX:
- Input shape `[1, 3, 224, 224]` float32
- Output shape `[1, 2]`
- Run on synthetic grey image → finite outputs
- Optionally test on sample S3 keyframe crops

### Step 1.6 — S3 path conventions

Detector (COCO pretrained, no versioning needed initially):
- `models/yolov8m.onnx` — same model AutoLabel uses

Classifier:
- `models/dog-classifier/versions/{version}/best.onnx` — classifier versions (reuses existing prefix)
- `models/dog-classifier/best.onnx` — active classifier
- `models/dog-classifier/active.json` — active classifier version

Future (if detector fine-tuning is needed):
- `models/dog-detector/versions/{version}/best.onnx` — fine-tuned detector versions
- `models/dog-detector/best.onnx` — active detector
- `models/dog-detector/active.json` — active detector version

---

## Phase 2 — Classifier Dataset Export

### Step 2.1 — Add classification export type to ExportDataset Lambda

**File:** `src/lambdas/SnoutSpotter.Lambda.ExportDataset/Function.cs`

Add `ExportType` to `ExportRequest`: `"detection"` (default, existing behavior unchanged) or `"classification"`

Classification export mode:
1. Query reviewed `my_dog` and `other_dog` labels with bounding boxes
2. Download each keyframe image
3. Crop each bounding box (with configurable padding, e.g. 10%)
4. Save as: `train/my_dog/img_NNNN.jpg`, `train/other_dog/img_NNNN.jpg`, `val/...`
5. Write manifest.json with `format: "classification"`, counts
6. No YOLO `.txt` label files needed
7. Same train/val split, same MaxPerClass balancing (operates on crops)
8. Zip and upload to same S3 location

### Step 2.2 — API + frontend for export type

**File:** `src/api/Services/ExportService.cs` — pass `exportType` to Lambda payload + DDB config Map
**File:** `src/api/Controllers/LabelsController.cs` — add `ExportType` to `TriggerExportRequest`
**File:** `src/web/src/pages/TrainingExports.tsx` — add export type selector (Detection / Classification)
**File:** `src/web/src/api.ts` — add `exportType` param to `triggerExport`

---

## Phase 3 — Model Management UI (Detector + Classifier)

### Step 3.1 — Generalize model API endpoints

**File:** `src/api/Controllers/LabelsController.cs`

Add `?type=detector|classifier` query param to model endpoints:
- `GET /api/ml/models?type=detector` — list detector versions
- `POST /api/ml/models/upload-url?version=v1&type=detector`
- `POST /api/ml/models/activate?version=v1&type=detector`

Constants per type:
- Detector prefix: `models/dog-detector/versions/`, active key: `models/dog-detector/best.onnx`
- Classifier prefix: `models/dog-classifier/versions/`, active key: `models/dog-classifier/best.onnx`

Default `type` = `"classifier"` for backward compat with existing model uploads.

### Step 3.2 — Frontend Models page tabs

**File:** `src/web/src/pages/Models.tsx`

- Add tab bar: "Dog Detector" | "Dog Classifier"
- State: `modelType: "detector" | "classifier"`
- Pass type to all API calls
- Tab descriptions explain each stage's role

### Step 3.3 — Frontend API changes

**File:** `src/web/src/api.ts`

- `listModels(type?: string)` → `GET /ml/models?type={type}`
- `getModelUploadUrl(version, type?)` → adds `&type={type}`
- `activateModel(version, type?)` → adds `&type={type}`

---

## Phase 4 — Automated Classifier Training via Agent

Extends the existing training agent to support classifier jobs. No rebuilds — adds a `jobType` field and routes to the correct script.

### Step 4.1 — Add job_type to training contracts

**File:** `src/shared/SnoutSpotter.Contracts/TrainingJobMessage.cs` — add `JobType` field (default `"detector"`)
**File:** `src/shared/TrainingResult.cs` — add optional `Accuracy`, `F1Score` fields
**File:** `src/shared/TrainingProgress.cs` — add optional `Accuracy`, `F1Score` fields

### Step 4.2 — Update JobRunner for classifier jobs

**File:** `src/training-agent/SnoutSpotter.TrainingAgent/JobRunner.cs`

- Read `JobType` from message
- Select script: `train_detector.py` (default, unchanged) or `train_classifier.py`
- Select upload path: `models/dog-detector/versions/` or `models/dog-classifier/versions/`
- Use appropriate progress parser
- Existing detector training flow completely unchanged

### Step 4.3 — Add ClassifierProgressParser

**File:** `src/training-agent/SnoutSpotter.TrainingAgent/ClassifierProgressParser.cs` (new)

Parses `EPOCH N/M train_loss=X val_loss=X accuracy=X f1=X` lines from train_classifier.py stdout.

### Step 4.4 — Frontend: job type in submit + detail

**File:** `src/web/src/pages/SubmitTraining.tsx`
- Add job type selector: "Detector" | "Classifier"
- When classifier: filter exports to `format == "classification"`, adjust defaults (imgsz=224, modelBase=mobilenet_v3_small)
- When detector: existing behavior unchanged

**File:** `src/web/src/pages/TrainingJobDetail.tsx`
- Show Accuracy/F1 for classifier jobs instead of mAP50

**File:** `src/api/Services/TrainingService.cs` — include `job_type` in SQS message and DDB record

---

## Phase 5 — Migration Cutover

1. Deploy Phases 1-4 with `pipeline_mode = "single"` (no behavior change)
2. Export classification dataset (Phase 2) → train classifier via agent or locally
3. Upload classifier to `models/dog-classifier/`, activate
4. Switch `pipeline_mode` to `"two_stage"` via server settings
5. Re-run inference on recent clips, validate results
6. Re-run on all clips if satisfactory
7. If detection recall is poor (unlikely — COCO model finds dogs well), consider fine-tuning a single-class detector later

---

## Critical Files

| File | Changes |
|------|---------|
| `src/lambdas/SnoutSpotter.Lambda.RunInference/Function.cs` | Two-stage inference, dual model loading, COCO dog filter + crop + classify |
| `src/shared/SnoutSpotter.Contracts/ServerSettings.cs` | Pipeline mode, classifier threshold/input/padding settings |
| `src/infra/Stacks/InferenceStack.cs` | DETECTOR_MODEL_KEY + CLASSIFIER_MODEL_KEY env vars |
| `src/ml/train_classifier.py` | New: MobileNetV3 training script |
| `src/ml/verify_classifier.py` | New: Classifier ONNX validation |
| `src/lambdas/SnoutSpotter.Lambda.ExportDataset/Function.cs` | Classification export type (crops dog patches) |
| `src/api/Controllers/LabelsController.cs` | Model type param, export type param |
| `src/api/Services/ExportService.cs` | Export type passthrough |
| `src/web/src/pages/Models.tsx` | Detector/Classifier tabs |
| `src/web/src/pages/TrainingExports.tsx` | Export type selector |
| `src/web/src/pages/SubmitTraining.tsx` | Job type selector |
| `src/web/src/pages/TrainingJobDetail.tsx` | Classifier metrics display |
| `src/web/src/api.ts` | Model type + export type + job type params |
| `src/training-agent/SnoutSpotter.TrainingAgent/JobRunner.cs` | Job type routing, classifier script/upload path |
| `src/training-agent/SnoutSpotter.TrainingAgent/ClassifierProgressParser.cs` | New: parse classifier training output |
| `src/shared/TrainingResult.cs` | Optional Accuracy, F1Score fields |
| `src/shared/TrainingProgress.cs` | Optional Accuracy, F1Score fields |
| `src/shared/SnoutSpotter.Contracts/TrainingJobMessage.cs` | JobType field |

## Verification

1. `dotnet build SnoutSpotter.sln` — no errors after each phase
2. `npm run build` from `src/web/` — no errors after each phase
3. Phase 1: `pipeline_mode=single` → identical to current behavior
4. Phase 1: `pipeline_mode=two_stage` with COCO detector + classifier → correct my_dog/other_dog labels
5. Phase 2: Classification export produces cropped images in `train/my_dog/`, `train/other_dog/` format
6. Phase 3: Models page shows both tabs, upload/activate works for each type
7. Phase 4: Classifier training job runs through agent, reports accuracy/f1
