# Self-Hosted Training Agent Plan

## Goal

Automate ML training so it can be triggered and monitored from the SnoutSpotter dashboard, running on a local desktop GPU. The desktop appears as a "training agent" in the system — submit training jobs with configurable parameters, watch live progress, and activate the finished model, all from the UI.

## Approach: .NET Agent in Docker + IoT Core

The training agent is a **.NET 8 console app** running inside a **Docker container** with NVIDIA GPU passthrough. It connects to IoT Core via MQTT (same pattern as the Pi agent), receives jobs via shadow deltas, shells out to `train_detector.py` (Python/CUDA/ultralytics bundled in the same container), and streams progress back over MQTT.

Updates are **shadow-triggered** — the dashboard writes a desired version to the agent shadow, the agent defers if training is in progress, and a host-level helper script handles the container swap.

```
┌─────────────────────────────────────────────────────────┐
│ Dashboard UI                                            │
│  - Submit training job (dataset, epochs, batch, imgsz)  │
│  - Watch live progress (epoch, loss, mAP)               │
│  - Review results, activate model                       │
│  - Trigger agent update                                 │
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
┌─ Docker Container (nvidia runtime) ─────────────────────┐
│                                                         │
│  .NET 8 Training Agent (long-running)                   │
│    - MQTT connection to IoT Core (MqttManager)          │
│    - Shadow delta handling (job dispatch + updates)      │
│    - S3 download/upload via AWS SDK                     │
│    - Spawns training subprocess                         │
│    - Parses stdout, publishes progress via MQTT         │
│                                                         │
│  Python 3.11 + CUDA 12 + PyTorch + Ultralytics         │
│    - train_detector.py (called as subprocess)           │
│    - verify_onnx.py                                     │
│                                                         │
├─────────────────────────────────────────────────────────┤
│  NVIDIA Container Toolkit (GPU passthrough)             │
└─────────────────────────────────────────────────────────┘
         │                              ▲
         │ MQTT progress + S3 upload    │ docker compose pull/restart
         ▼                              │
┌─────────────────────────┐    ┌────────────────────┐
│ IoT Rule → DynamoDB     │    │ Host: updater.sh   │
│ (training-jobs table)   │    │ (watches exit code) │
└─────────────────────────┘    └────────────────────┘
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
      "agentVersion": "1.0.0",
      "hostname": "greg-desktop",
      "lastHeartbeat": "2026-04-07T10:00:00Z",
      "status": "idle",
      "updateStatus": "idle",
      "gpu": {
        "name": "NVIDIA RTX 4080 Super",
        "vramMb": 16384,
        "cudaVersion": "12.1",
        "driverVersion": "535.129",
        "temperatureC": 45,
        "utilizationPercent": 0
      },
      "system": {
        "cpuCores": 16,
        "ramGb": 32,
        "diskFreeGb": 250,
        "os": "Linux (Docker)",
        "dotnetVersion": "8.0.4",
        "pythonVersion": "3.11.5",
        "torchVersion": "2.1.0",
        "ultralyticsVersion": "8.1.0"
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
| `status` | String | `pending`, `downloading`, `training`, `uploading`, `complete`, `failed`, `cancelled`, `interrupted` |
| `agent_thing_name` | String | Thing name of the agent that picked up the job |
| `export_id` | String | Reference to the training export dataset |
| `export_s3_key` | String | S3 key of the export zip |
| `config` | Map | Training configuration (see below) |
| `progress` | Map | Current progress (epoch, loss, mAP, etc.) |
| `result` | Map | Final metrics + model S3 key |
| `checkpoint_s3_key` | String | S3 key of last.pt checkpoint (for resume) |
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
GET  /api/training/agents              — list registered training agents with shadow status
GET  /api/training/agents/{name}       — single agent status + GPU info
POST /api/training/agents/{name}/update — trigger agent update to specified version

POST /api/training/jobs                — submit a new training job
GET  /api/training/jobs                — list jobs (filterable by status)
GET  /api/training/jobs/{jobId}        — job detail with progress
POST /api/training/jobs/{jobId}/cancel — cancel a running job
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
4. Agent uploads last.pt checkpoint to S3 (for future resume)
5. Agent reports status=cancelled with checkpoint_s3_key
```

### 3.4 Update Agent Flow

```
1. UI calls POST /api/training/agents/{name}/update with { "version": "1.2.0" }
2. API writes to shadow: { "desired": { "agentVersion": "1.2.0" } }
3. Agent receives delta (see Phase 7 for mid-training handling)
```

---

## Phase 4: Docker Image

### 4.1 Multi-Stage Dockerfile

**File:** `src/training-agent/Dockerfile`

```dockerfile
# Stage 1: Build .NET agent
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/training-agent/SnoutSpotter.TrainingAgent/ .
RUN dotnet publish -c Release -o /app

# Stage 2: Runtime with .NET + Python + CUDA
FROM nvidia/cuda:12.1.1-runtime-ubuntu22.04

# .NET runtime
RUN apt-get update && apt-get install -y dotnet-runtime-8.0

# Python + training deps
RUN apt-get install -y python3.11 python3-pip
COPY src/ml/requirements-training.txt /tmp/
RUN pip3 install -r /tmp/requirements-training.txt

# Copy .NET agent
COPY --from=build /app /app

# Copy training scripts
COPY src/ml/ /app/ml/

WORKDIR /app
ENTRYPOINT ["dotnet", "SnoutSpotter.TrainingAgent.dll"]
```

### 4.2 ECR Repository

**File:** `src/infra/Stacks/CoreStack.cs`

Add ECR repo: `snout-spotter-training-agent` (same pattern as other ECR repos).

### 4.3 GitHub Actions Workflow

**File:** `.github/workflows/build-training-agent-image.yml`

Triggered on changes to `src/training-agent/**` or `src/ml/**`:
1. Build Docker image
2. Push to ECR with version tag + `:latest`
3. Optionally tag with git SHA for traceability

---

## Phase 5: .NET Training Agent

### 5.1 Project Structure

```
src/training-agent/
├── SnoutSpotter.TrainingAgent/
│   ├── SnoutSpotter.TrainingAgent.csproj
│   ├── Program.cs                 # Entry point: config load, MQTT connect, main loop
│   ├── MqttManager.cs             # MQTT connection wrapper (same pattern as Pi MqttManager)
│   ├── ShadowHandler.cs           # Shadow delta parsing: training jobs, cancel, updates
│   ├── JobRunner.cs               # Download dataset, spawn training, monitor, upload model
│   ├── ProgressParser.cs          # Parse YOLO stdout for epoch metrics (regex)
│   ├── GpuInfo.cs                 # nvidia-smi parsing: name, VRAM, temp, utilization
│   ├── UpdateHandler.cs           # Agent update logic with mid-training deferral
│   └── Models/
│       ├── TrainingJobConfig.cs   # Job config record type
│       ├── TrainingProgress.cs    # Per-epoch progress record type
│       └── TrainingResult.cs      # Final result record type
├── Dockerfile
└── docker-compose.yml
```

### 5.2 Core Agent Loop

**File:** `Program.cs`

```csharp
// Mirrors Pi agent pattern:
// 1. Load config (certs, IoT endpoint, S3 bucket)
// 2. Connect MQTT via MqttManager (reconnect tracking, re-subscribe)
// 3. Subscribe to shadow delta
// 4. Request current shadow (catch pending jobs from while offline)
// 5. Main loop: heartbeat, shadow dirty flag, event queue drain

var agent = new TrainingAgent(config);
agent.Run(); // blocking main loop
```

### 5.3 Job Runner

**File:** `JobRunner.cs`

```csharp
public class JobRunner
{
    public async Task RunJobAsync(TrainingJobConfig job, CancellationToken ct)
    {
        // 1. Update status: downloading
        ReportStatus("downloading");

        // 2. Download export zip from S3
        await DownloadDatasetAsync(job.ExportS3Key, ct);

        // 3. Extract dataset
        ExtractDataset();

        // 4. Update status: training
        ReportStatus("training");

        // 5. Spawn: python3 /app/ml/train_detector.py
        //    --data /app/data/dataset
        //    --epochs {job.Epochs}
        //    --batch {job.BatchSize}
        //    --imgsz {job.ImageSize}
        //    --workers {job.Workers}
        var process = StartTrainingProcess(job);

        // 6. Read stdout line by line, parse with ProgressParser
        //    Publish progress via MQTT on each epoch
        await MonitorTrainingAsync(process, job.JobId, ct);

        // 7. Update status: uploading
        ReportStatus("uploading");

        // 8. Upload best.onnx + last.pt to S3
        await UploadResultsAsync(job);

        // 9. Report final metrics
        ReportComplete(job);
    }
}
```

### 5.4 Progress Parser

**File:** `ProgressParser.cs`

Parses YOLO (ultralytics) stdout with regex:

```csharp
public class ProgressParser
{
    // Match:       45/100      4.8G    0.02341    0.01234    0.00891         12        640
    private static readonly Regex EpochRegex = new(
        @"\s+(\d+)/(\d+)\s+[\d.]+G\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)");

    // Match:                    all        250        312      0.879      0.931      0.912      0.742
    private static readonly Regex MetricsRegex = new(
        @"\s+all\s+\d+\s+\d+\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)");

    public TrainingProgress? ParseLine(string line) { ... }
}
```

### 5.5 GPU Info

**File:** `GpuInfo.cs`

```csharp
public static class GpuInfo
{
    public static GpuStatus GetStatus()
    {
        // Run: nvidia-smi --query-gpu=name,memory.total,memory.used,temperature.gpu,
        //      utilization.gpu,driver_version --format=csv,noheader,nounits
        var output = RunProcess("nvidia-smi", "--query-gpu=...");
        // Parse CSV output
        return new GpuStatus(Name, VramMb, TemperatureC, UtilizationPercent, ...);
    }
}
```

---

## Phase 6: Image Delivery & Updates

### 6.1 Shadow-Triggered Updates

The dashboard writes `desired.agentVersion` to the agent shadow. The agent handles this via `UpdateHandler`.

### 6.2 Mid-Training Update Deferral

**File:** `UpdateHandler.cs`

```
Shadow delta arrives: desired.agentVersion = "1.2.0"

Is a training job running?
├── NO  → apply update immediately
│         1. Report updateStatus: "updating"
│         2. Exit with code 42 (signals updater.sh to pull + restart)
│
└── YES → defer update
          1. Report shadow:
             {
               "updateStatus": "deferred",
               "deferredVersion": "1.2.0",
               "deferReason": "Training job tj-20260407-001 in progress (epoch 67/100)"
             }
          2. Save pending version in memory
          3. When job completes (or fails/cancels):
             → apply the pending update (exit code 42)
```

Dashboard shows: **"Update to v1.2.0 queued — waiting for training to finish (epoch 67/100)"**

### 6.3 Force Update (Urgent)

For critical updates, the dashboard can send a force flag:

```json
{ "desired": { "agentVersion": "1.2.0", "forceUpdate": true } }
```

Force update during training:
1. Save `last.pt` checkpoint to S3
2. Record job as `interrupted` with `checkpoint_s3_key`
3. Apply update (exit code 42)
4. After restart, the interrupted job can be resumed via `--resume` in a new job submission

### 6.4 Host-Level Updater Script

**File:** `src/training-agent/updater.sh`

Runs on the host (outside Docker). Watches the container exit code:

```bash
#!/bin/bash
# updater.sh — runs on host, manages container lifecycle

IMAGE="<account>.dkr.ecr.eu-west-1.amazonaws.com/snout-spotter-training-agent"

while true; do
    # Run the container, capture exit code
    docker compose up trainer
    EXIT_CODE=$?

    if [ "$EXIT_CODE" -eq 42 ]; then
        echo "Agent requested update — pulling new image..."
        # Re-authenticate to ECR (tokens expire after 12h)
        aws ecr get-login-password --region eu-west-1 | \
            docker login --username AWS --password-stdin "$IMAGE"
        docker compose pull trainer
        echo "Restarting with new image..."
        # Loop continues → docker compose up runs new image
    elif [ "$EXIT_CODE" -eq 0 ]; then
        echo "Agent exited cleanly — stopping"
        break
    else
        echo "Agent crashed (code=$EXIT_CODE) — restarting in 10s..."
        sleep 10
        # Loop continues → docker compose up restarts
    fi
done
```

Exit code convention:
- `0` — clean shutdown (e.g. SIGTERM)
- `42` — update requested (pull new image and restart)
- Anything else — crash (restart after cooldown)

### 6.5 Docker Compose

**File:** `src/training-agent/docker-compose.yml`

```yaml
services:
  trainer:
    image: ${ECR_REGISTRY}/snout-spotter-training-agent:latest
    runtime: nvidia
    environment:
      - NVIDIA_VISIBLE_DEVICES=all
    volumes:
      - ./certs:/app/certs:ro                  # IoT certificates
      - ./config.yaml:/app/config.yaml:ro      # Agent config
      - trainer-data:/app/data                  # Datasets + checkpoints (persists across updates)
      - trainer-models:/app/models              # Trained models (persists across updates)
    deploy:
      resources:
        reservations:
          devices:
            - capabilities: [gpu]

volumes:
  trainer-data:
  trainer-models:
```

Key: `trainer-data` and `trainer-models` are named Docker volumes that **persist across image updates**. Datasets don't need to be re-downloaded and checkpoints survive container swaps.

---

## Phase 7: Initial Setup

### 7.1 Setup Script

**File:** `src/training-agent/setup.sh`

```bash
#!/bin/bash
# One-time setup for a new training agent machine

echo "=== SnoutSpotter Training Agent Setup ==="

# 1. Check prerequisites
check_command docker "Install Docker: https://docs.docker.com/get-docker/"
check_command nvidia-smi "Install NVIDIA drivers"
check_command aws "Install AWS CLI: https://aws.amazon.com/cli/"

# Verify nvidia-container-toolkit
docker run --rm --gpus all nvidia/cuda:12.1.1-base-ubuntu22.04 nvidia-smi || {
    echo "ERROR: nvidia-container-toolkit not working"
    echo "Install: https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/"
    exit 1
}

# 2. Collect config
read -rp "Agent name (e.g. desktop-gpu): " AGENT_NAME
read -rp "Pi Management API URL: " PI_MGMT_URL
read -rp "AWS Region [eu-west-1]: " AWS_REGION
AWS_REGION="${AWS_REGION:-eu-west-1}"

# 3. Register agent
RESPONSE=$(curl -s -X POST "$PI_MGMT_URL/api/devices/register" \
    -H "Content-Type: application/json" \
    -d "{\"name\": \"$AGENT_NAME\", \"type\": \"training-agent\"}")

# 4. Extract certs, IoT endpoint, credential provider endpoint
# (same pattern as Pi setup-pi.sh)

# 5. Save certs
mkdir -p certs
echo "$CERT_PEM" > certs/certificate.pem.crt
echo "$PRIVATE_KEY" > certs/private.pem.key
curl -s -o certs/AmazonRootCA1.pem "$ROOT_CA_URL"

# 6. Write config.yaml
cat > config.yaml << EOF
agent_name: "$AGENT_NAME"
iot:
  endpoint: "$IOT_ENDPOINT"
  thing_name: "snoutspotter-trainer-$AGENT_NAME"
  cert_path: /app/certs/certificate.pem.crt
  key_path: /app/certs/private.pem.key
  root_ca_path: /app/certs/AmazonRootCA1.pem
s3:
  bucket: "$BUCKET_NAME"
  region: $AWS_REGION
credentials_provider:
  endpoint: "$CREDENTIAL_ENDPOINT"
EOF

# 7. Write .env for docker-compose
ECR_REGISTRY="<account>.dkr.ecr.$AWS_REGION.amazonaws.com"
echo "ECR_REGISTRY=$ECR_REGISTRY" > .env

# 8. Authenticate to ECR and pull image
aws ecr get-login-password --region $AWS_REGION | \
    docker login --username AWS --password-stdin "$ECR_REGISTRY"
docker compose pull

# 9. Start agent
echo "Starting training agent..."
./updater.sh &

echo "=== Setup Complete ==="
echo "Agent: snoutspotter-trainer-$AGENT_NAME"
echo "Logs:  docker compose logs -f trainer"
```

---

## Phase 8: Dashboard Training UI

### 8.1 Training Agents Page

**File:** `src/web/src/pages/TrainingAgents.tsx` (new)

Shows registered training agents:
- Agent name, hostname, status (online/offline)
- GPU: name, VRAM, CUDA version, current temperature/utilization
- System: CPU, RAM, disk free, .NET/Python/PyTorch versions
- Current job (if any) with progress bar
- Last heartbeat
- Agent version + update status (idle / deferred / updating)
- "Update Agent" button (with version input)

### 8.2 Submit Training Job

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

### 8.3 Training Job Detail / Live Progress

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

### 8.4 Training Jobs List

**File:** `src/web/src/pages/TrainingJobs.tsx` (new)

Table of all training jobs:
- Job ID, status, dataset, epochs, best mAP50, duration, agent, created date
- Click to view detail
- Filter by status
- Resume button for interrupted/cancelled jobs with checkpoints

### 8.5 Navigation

Add to sidebar:
```
Training (new section)
  ├── Agents     → /training/agents
  ├── Jobs       → /training/jobs
  └── New Job    → /training/jobs/new
```

Or consolidate under existing Models page as a "Train" tab.

---

## Phase 9: Post-Training Model Activation

### 9.1 One-Click Activate

After training completes, the job detail page shows an "Activate Model" button:

```
1. Calls POST /api/ml/models/activate?version=v3.0
2. Copies model from versions/v3.0/best.onnx → models/dog-classifier/best.onnx
3. RunInference Lambda picks up new model on next cold start
4. Job status updated to "activated"
```

This reuses the existing model activation flow from the Models page.

### 9.2 Model Comparison

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
| 1 | 2.1 | Create training-jobs DynamoDB table + ECR repo | 1h | None |
| 2 | 1.1 | Extend PiMgmt registration for training-agent type | 2h | None |
| 3 | 1.2 | Add IoT policy for training agent things | 1h | Step 2 |
| 4 | 3.1 | Create TrainingController API endpoints | 3h | Step 1 |
| 5 | 4.1 | Build Docker image (multi-stage .NET + Python/CUDA) | 3h | None |
| 6 | 5.1-5.5 | Build .NET training agent (MQTT, job runner, progress parser, GPU info) | 8h | Steps 2, 3 |
| 7 | 6.2-6.4 | Update handler with mid-training deferral + updater.sh | 2h | Step 6 |
| 8 | 4.3 | GitHub Actions workflow for image build + ECR push | 1h | Step 5 |
| 9 | 7.1 | Setup script for agent registration + Docker setup | 2h | Steps 5, 6 |
| 10 | 8.1 | Training Agents page (agent status + GPU info + update) | 2h | Step 4 |
| 11 | 8.2 | Submit Training Job form | 2h | Step 4 |
| 12 | 8.3 | Training Job Detail with live progress | 3h | Step 4 |
| 13 | 8.4 | Training Jobs list page | 1h | Step 4 |
| 14 | 8.5 | Navigation + routing | 30m | Steps 10-13 |
| 15 | 9.1 | One-click model activation from job detail | 1h | Step 12 |
| 16 | — | End-to-end testing | 3h | All |

**Total estimated effort: ~35 hours across 16 steps**

---

## MQTT Topics

| Topic | Direction | Purpose |
|-------|-----------|---------|
| `$aws/things/{agentThing}/shadow/update` | Agent → Cloud | Report status, GPU info, heartbeat, update status |
| `$aws/things/{agentThing}/shadow/update/delta` | Cloud → Agent | New job, cancel command, or version update |
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
- Docker volumes for certs are mounted read-only

---

## Host Requirements

- **Docker:** Docker Engine with nvidia-container-toolkit
- **GPU:** NVIDIA with CUDA support (RTX 2060+ recommended, RTX 4080 Super ideal)
- **VRAM:** 6 GB minimum (8 GB+ for batch_size > 16)
- **Disk:** 20 GB free for Docker image + datasets + checkpoints
- **Network:** Reliable connection for S3 downloads/uploads and MQTT
- **AWS CLI:** For ECR authentication
- **OS:** Linux (native Docker GPU), Windows 10/11 (WSL2 + Docker Desktop), macOS (no NVIDIA GPU)

---

## Future Extensions

- **Multiple training agents** — register multiple desktops/servers, jobs auto-assigned to idle agents with best GPU
- **Job queue priority** — urgent jobs skip the queue
- **Hyperparameter sweeps** — submit multiple jobs with different configs, compare results in a table
- **Auto-training pipeline** — trigger training automatically when N new labels are confirmed
- **Training notifications** — SNS/email/Slack when training completes or fails
- **Checkpoint resume** — resume interrupted/cancelled jobs from last checkpoint (already supported in data model)
- **Distributed training** — split across multiple GPUs/machines (future, when dataset grows large)
- **Cost tracking** — estimate electricity cost based on GPU power draw and training duration
- **Cloud burst** — if local agent is busy, optionally spin up an EC2 g5 instance with the same Docker image for overflow jobs
