# Self-Hosted Training Agent Plan

## Goal

Automate ML training so it can be triggered and monitored from the SnoutSpotter dashboard, running on a local desktop GPU. The desktop appears as a "training agent" in the system — submit training jobs with configurable parameters, watch live progress, and activate the finished model, all from the UI.

## Approach: IoT Core as Training Agent Backend

Reuse the existing IoT Core infrastructure. The desktop registers as an IoT Thing (type: `training-agent`), connects via MQTT, receives jobs via shadow deltas, and streams progress back over MQTT. No new AWS services needed.

```
┌─────────────────────────────────────────────────────────┐
│ Dashboard UI                                            │
│  - Submit training job (dataset, epochs, batch, imgsz)  │
│  - Watch live progress (epoch, loss, mAP)               │
│  - Review results, activate model                       │
└───────────────────────┬─────────────────────────────────┘
                        │ API call
                        ▼
┌─────────────────────────────────────────────────────────┐
│ API Lambda                                              │
│  - Writes training job to agent shadow desired state    │
│  - Stores job metadata in DynamoDB                      │
│  - Returns job ID                                       │
└───────────────────────┬─────────────────────────────────┘
                        │ IoT shadow delta (MQTT)
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Desktop Training Agent (Python)                         │
│  - Receives shadow delta with job config                │
│  - Downloads export dataset from S3                     │
│  - Runs train_detector.py with configured params        │
│  - Streams progress via MQTT topic                      │
│  - Uploads .onnx model to S3                            │
│  - Reports final metrics                                │
└───────────────────────┬─────────────────────────────────┘
                        │ MQTT progress + S3 upload
                        ▼
┌─────────────────────────────────────────────────────────┐
│ IoT Rule → DynamoDB (training-jobs table)               │
│  - Stores epoch progress, loss, mAP per job             │
│  - Final status: complete/failed + metrics              │
└─────────────────────────────────────────────────────────┘
```

---

## Phase 1: Training Agent Registration & Status

### 1.1 IoT Thing Registration

Extend the PiMgmt API to support a `training-agent` device type.

**File:** `src/lambdas/SnoutSpotter.Lambda.PiMgmt/Controllers/DevicesController.cs`

- `POST /api/devices/register` — accept `{"name": "desktop-gpu", "type": "training-agent"}`
- Thing name: `snoutspotter-trainer-{name}` (e.g. `snoutspotter-trainer-desktop-gpu`)
- Same registration flow: create IoT Thing, generate X.509 certs, attach policy
- Return certs, IoT endpoint, and credential provider endpoint

### 1.2 Training Agent IoT Policy

**File:** `src/infra/Stacks/IoTStack.cs`

Add policy permissions for training agent things:
```
- iot:Connect (client ID: snoutspotter-trainer-*)
- iot:Subscribe/Receive (shadow topics + snoutspotter/trainer/*/progress)
- iot:Publish (shadow update + progress topics)
- s3:GetObject (export datasets)
- s3:PutObject (trained models)
```

### 1.3 Agent Shadow Reported State

The training agent reports its capabilities and status:

```json
{
  "state": {
    "reported": {
      "agentType": "training-agent",
      "hostname": "greg-desktop",
      "version": "1.0.0",
      "lastHeartbeat": "2026-04-07T10:00:00Z",
      "status": "idle",
      "gpu": {
        "name": "NVIDIA RTX 3080",
        "vramMb": 10240,
        "cudaVersion": "12.1",
        "driverVersion": "535.129"
      },
      "system": {
        "cpuCores": 16,
        "ramGb": 32,
        "diskFreeGb": 250,
        "os": "Windows 11",
        "pythonVersion": "3.11.5",
        "torchVersion": "2.1.0"
      },
      "currentJob": null,
      "lastJobId": "tj-20260407-001",
      "lastJobStatus": "complete"
    }
  }
}
```

---

## Phase 2: Training Jobs Data Model

### 2.1 DynamoDB Table: `snout-spotter-training-jobs`

**File:** `src/infra/Stacks/CoreStack.cs`

| Attribute | Type | Description |
|-----------|------|-------------|
| `job_id` (PK) | String | Unique ID (e.g. `tj-20260407-001`) |
| `status` | String | `pending`, `downloading`, `training`, `uploading`, `complete`, `failed`, `cancelled` |
| `agent_thing_name` | String | Thing name of the agent that picked up the job |
| `export_id` | String | Reference to the training export dataset |
| `export_s3_key` | String | S3 key of the export zip |
| `config` | Map | Training configuration (see below) |
| `progress` | Map | Current progress (epoch, loss, mAP, etc.) |
| `result` | Map | Final metrics + model S3 key |
| `created_at` | String | ISO 8601 |
| `started_at` | String | ISO 8601 |
| `completed_at` | String | ISO 8601 |
| `error` | String | Error message if failed |

**GSI:** `by-status` — PK: `status`, SK: `created_at`

### 2.2 Training Job Config

Submitted by the user from the dashboard:

```json
{
  "export_id": "exp-20260405-001",
  "epochs": 100,
  "batch_size": 16,
  "image_size": 640,
  "learning_rate": 0.01,
  "workers": 8,
  "resume_from": null,
  "model_base": "yolov8n.pt",
  "notes": "Training with new bowl labels"
}
```

### 2.3 Training Progress (updated per epoch)

```json
{
  "epoch": 45,
  "total_epochs": 100,
  "train_loss": 0.0234,
  "val_loss": 0.0312,
  "mAP50": 0.89,
  "mAP50_95": 0.72,
  "best_mAP50": 0.91,
  "elapsed_seconds": 1823,
  "eta_seconds": 2230,
  "gpu_util_percent": 95,
  "gpu_temp_c": 72
}
```

### 2.4 Training Result (on completion)

```json
{
  "model_s3_key": "models/dog-classifier/versions/v3.0/best.onnx",
  "model_size_mb": 12.4,
  "final_mAP50": 0.91,
  "final_mAP50_95": 0.74,
  "precision": 0.88,
  "recall": 0.93,
  "classes": ["my_dog", "other_dog", "food_bowl", "water_bowl"],
  "total_epochs": 100,
  "best_epoch": 87,
  "training_time_seconds": 4053,
  "dataset_images": 1250
}
```

---

## Phase 3: API Endpoints

### 3.1 Training Agent Endpoints

**File:** `src/api/Controllers/TrainingController.cs` (new)

```
GET  /api/training/agents         — list registered training agents with shadow status
GET  /api/training/agents/{name}  — single agent status + GPU info

POST /api/training/jobs           — submit a new training job
GET  /api/training/jobs           — list jobs (filterable by status)
GET  /api/training/jobs/{jobId}   — job detail with progress
POST /api/training/jobs/{jobId}/cancel — cancel a running job

GET  /api/training/jobs/{jobId}/progress — latest progress (polled by UI)
```

### 3.2 Submit Job Flow

```
1. UI calls POST /api/training/jobs with config
2. API creates job record in DynamoDB (status=pending)
3. API writes to training agent shadow desired state:
   {
     "desired": {
       "trainingJob": {
         "jobId": "tj-20260407-001",
         "exportS3Key": "training-exports/exp-20260405-001.zip",
         "config": { "epochs": 100, "batch_size": 16, ... }
       }
     }
   }
4. Agent receives shadow delta via MQTT
5. Agent starts training, updates job status to "training"
6. Returns job ID to UI for polling
```

### 3.3 Cancel Job Flow

```
1. UI calls POST /api/training/jobs/{jobId}/cancel
2. API writes to shadow: { "desired": { "cancelJob": "tj-20260407-001" } }
3. Agent receives delta, sends SIGTERM to training process
4. Agent reports status=cancelled
```

---

## Phase 4: Desktop Training Agent

### 4.1 Agent Script

**File:** `src/training-agent/agent.py` (new directory)

Core architecture mirrors `src/pi/agent.py` — single MQTT connection, shadow-based job dispatch.

```python
class TrainingAgent:
    def __init__(self, config):
        self.mqtt = MqttManager(...)      # Same pattern as Pi agent
        self.gpu_info = detect_gpu()       # nvidia-smi parsing
        self.current_job = None
        self.training_process = None

    def on_shadow_delta(self, state):
        if "trainingJob" in state:
            self.start_job(state["trainingJob"])
        if "cancelJob" in state:
            self.cancel_job(state["cancelJob"])

    def start_job(self, job_config):
        # 1. Download export zip from S3
        # 2. Extract dataset
        # 3. Spawn train_detector.py as subprocess
        # 4. Monitor stdout for epoch progress (YOLO prints metrics per epoch)
        # 5. Parse and publish progress via MQTT
        # 6. On completion: upload best.onnx to S3, report results

    def publish_progress(self, job_id, progress):
        topic = f"snoutspotter/trainer/{self.thing_name}/progress"
        self.mqtt.publish(topic, json.dumps({
            "job_id": job_id,
            "progress": progress,
            "timestamp": now()
        }))

    def report_shadow(self):
        # Report: status, GPU info, system health, current job
```

### 4.2 Agent Directory Structure

```
src/training-agent/
├── agent.py              # Main agent: MQTT connection, shadow delta handling
├── config.yaml           # Device-specific: certs, IoT endpoint, S3 bucket
├── gpu_info.py           # GPU detection: nvidia-smi parsing, VRAM, CUDA version
├── job_runner.py         # Training job execution: download, train, upload, report
├── progress_parser.py    # Parse YOLO stdout for epoch metrics
├── requirements.txt      # boto3, awsiotsdk, pyyaml, torch, ultralytics
└── setup.sh              # One-time setup: register agent, install deps, save certs
```

### 4.3 Progress Parsing

YOLO (ultralytics) prints progress per epoch to stdout:
```
      Epoch    GPU_mem   box_loss   cls_loss   dfl_loss  Instances       Size
      45/100      4.8G    0.02341    0.01234    0.00891         12        640
```

`progress_parser.py` reads stdout line by line, extracts metrics via regex, and feeds them to `publish_progress()`.

Also parse the validation results printed after each epoch:
```
                 Class     Images  Instances      Box(P          R      mAP50  mAP50-95)
                   all        250        312      0.879      0.931      0.912      0.742
```

### 4.4 Setup Script

**File:** `src/training-agent/setup.sh`

```bash
1. Check prerequisites (Python 3.10+, CUDA, torch, ultralytics)
2. Prompt for: agent name, S3 bucket, Pi Management API URL
3. Register as training-agent via PiMgmt API
4. Save certs to ~/.snoutspotter-trainer/certs/
5. Write config.yaml
6. Test MQTT connection
7. Print "Agent ready — run: python agent.py"
```

### 4.5 Running as a Service

**Windows:** Create a scheduled task or run as a background script:
```
pythonw agent.py
```

**Linux/Mac:** systemd service or launchd plist, same pattern as Pi services.

The agent should handle sleep/wake gracefully — reconnect MQTT on resume, skip any stale shadow deltas.

---

## Phase 5: Dashboard Training UI

### 5.1 Training Agents Page

**File:** `src/web/src/pages/TrainingAgents.tsx` (new)

Shows registered training agents:
- Agent name, hostname, status (online/offline)
- GPU: name, VRAM, CUDA version, current temperature
- System: CPU, RAM, disk free
- Current job (if any) with progress bar
- Last heartbeat

### 5.2 Submit Training Job

**File:** `src/web/src/pages/SubmitTraining.tsx` (new) or modal in TrainingAgents

Form fields:
- **Dataset:** dropdown of available exports (from `GET /api/ml/exports` where status=complete)
- **Epochs:** number input (default 100, range 10-500)
- **Batch size:** dropdown [8, 16, 32] (default 16)
- **Image size:** dropdown [416, 640, 800] (default 640)
- **Learning rate:** number input (default 0.01)
- **Workers:** number input (default 8)
- **Base model:** dropdown [yolov8n.pt, yolov8s.pt] (default yolov8n.pt)
- **Resume from:** optional, select a previous job's checkpoint
- **Notes:** free text

Submit button → creates job → redirects to job detail page.

### 5.3 Training Job Detail / Live Progress

**File:** `src/web/src/pages/TrainingJobDetail.tsx` (new)

Real-time training dashboard (polls every 5s):

```
┌──────────────────────────────────────────────────┐
│ Training Job tj-20260407-001                     │
│ Status: Training ●  Epoch 45/100 (45%)           │
│ ████████████████████░░░░░░░░░░░░░░░░░░  ETA: 37m│
├──────────────────────────────────────────────────┤
│                                                  │
│  Loss (train/val)          mAP50                 │
│  ┌────────────────┐       ┌────────────────┐     │
│  │ ╲              │       │          ╱──── │     │
│  │  ╲─────────    │       │        ╱       │     │
│  │               ─│       │     ╱──        │     │
│  └────────────────┘       └────────────────┘     │
│                                                  │
│  Config                    Metrics               │
│  Dataset: exp-20260405     Best mAP50: 0.912     │
│  Epochs: 100               Precision: 0.879      │
│  Batch: 16                 Recall: 0.931          │
│  ImgSz: 640                GPU: 95% / 72°C       │
│                                                  │
│  [Cancel Job]                                    │
├──────────────────────────────────────────────────┤
│ On completion:                                   │
│  [Activate Model]  [Download .onnx]  [Discard]   │
└──────────────────────────────────────────────────┘
```

### 5.4 Training Jobs List

**File:** `src/web/src/pages/TrainingJobs.tsx` (new)

Table of all training jobs:
- Job ID, status, dataset, epochs, best mAP50, duration, agent, created date
- Click to view detail
- Filter by status

### 5.5 Navigation

Add to sidebar:
```
Training (new section)
  ├── Agents     → /training/agents
  ├── Jobs       → /training/jobs
  └── New Job    → /training/jobs/new
```

Or consolidate under existing Models page as a "Train" tab.

---

## Phase 6: Post-Training Model Activation

### 6.1 One-Click Activate

After training completes, the job detail page shows an "Activate Model" button:

```
1. Calls POST /api/ml/models/activate?version=v3.0
2. Copies model from versions/v3.0/best.onnx → models/dog-classifier/best.onnx
3. RunInference Lambda picks up new model on next cold start
4. Job status updated to "activated"
```

This reuses the existing model activation flow from the Models page.

### 6.2 Model Comparison

Before activating, show a comparison table:

| Metric | Current (v2.0) | New (v3.0) |
|--------|---------------|------------|
| mAP50 | 0.87 | 0.91 |
| Precision | 0.85 | 0.88 |
| Recall | 0.89 | 0.93 |
| Classes | 2 | 4 |

---

## Implementation Order

| Step | Phase | Description | Effort | Dependencies |
|------|-------|-------------|--------|--------------|
| 1 | 2.1 | Create training-jobs DynamoDB table | 1h | None |
| 2 | 1.1 | Extend PiMgmt registration for training-agent type | 2h | None |
| 3 | 1.2 | Add IoT policy for training agent things | 1h | Step 2 |
| 4 | 3.1 | Create TrainingController API endpoints | 3h | Step 1 |
| 5 | 4.1-4.3 | Build desktop training agent (MQTT, job runner, progress parser) | 6h | Steps 2, 3 |
| 6 | 4.4 | Setup script for agent registration | 1h | Step 5 |
| 7 | 5.1 | Training Agents page (agent status + GPU info) | 2h | Step 4 |
| 8 | 5.2 | Submit Training Job form | 2h | Step 4 |
| 9 | 5.3 | Training Job Detail with live progress | 3h | Step 4 |
| 10 | 5.4 | Training Jobs list page | 1h | Step 4 |
| 11 | 5.5 | Navigation + routing | 30m | Steps 7-10 |
| 12 | 6.1 | One-click model activation from job detail | 1h | Step 9 |
| 13 | — | End-to-end testing | 2h | All |

**Total estimated effort: ~26 hours across 13 steps**

---

## MQTT Topics

| Topic | Direction | Purpose |
|-------|-----------|---------|
| `$aws/things/{agentThing}/shadow/update` | Agent → Cloud | Report status, GPU info, heartbeat |
| `$aws/things/{agentThing}/shadow/update/delta` | Cloud → Agent | New job or cancel command |
| `snoutspotter/trainer/{agentThing}/progress` | Agent → Cloud | Per-epoch training progress |
| `snoutspotter/trainer/{agentThing}/logs` | Agent → Cloud | Training agent logs (optional) |

**IoT Rules:**
- `snoutspotter/trainer/+/progress` → DynamoDB action: update training-jobs table progress field
- `snoutspotter/trainer/+/logs` → CloudWatch Logs (same pattern as Pi log shipping)

---

## Security Considerations

- Training agent certs should be scoped to training-specific IoT policy (no access to Pi shadow topics)
- S3 access limited to `training-exports/*` (read) and `models/*` (write)
- Job submission requires Okta JWT (same as all API endpoints)
- Agent cannot activate models directly — only the API can do that (requires Okta auth)
- Training agent should not have access to raw clips or keyframes — only exported datasets

---

## Desktop Requirements

- **GPU:** NVIDIA with CUDA support (RTX 2060+ recommended)
- **VRAM:** 6 GB minimum (8 GB+ for batch_size > 16)
- **Disk:** 10 GB free for datasets + checkpoints
- **Software:** Python 3.10+, CUDA toolkit, PyTorch, Ultralytics
- **Network:** Reliable connection for S3 downloads/uploads and MQTT
- **OS:** Windows 10/11, Linux, or macOS (with NVIDIA GPU)

---

## Future Extensions

- **Multiple training agents** — register multiple desktops/servers, jobs auto-assigned to idle agents
- **Job queue priority** — urgent jobs skip the queue
- **Hyperparameter sweeps** — submit multiple jobs with different configs, compare results
- **Auto-training pipeline** — trigger training automatically when N new labels are confirmed
- **Training notifications** — SNS/email/Slack when training completes or fails
- **Checkpoint resume** — resume failed/cancelled jobs from last checkpoint
- **Distributed training** — split across multiple GPUs/machines (future, when dataset grows large)
- **Cost tracking** — estimate electricity cost based on GPU power draw and training duration
