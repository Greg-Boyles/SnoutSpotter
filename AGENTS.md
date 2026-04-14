# SnoutSpotter — AI Knowledge Base

## Project Overview

SnoutSpotter is a motion-triggered video capture system running on Raspberry Pi devices, with cloud-based ML inference to detect dogs (and identify a specific dog). Clips are uploaded to AWS, processed through an ingest pipeline, analysed by ML models, and viewable via a React web dashboard protected by Okta authentication.

**Tech stack:** .NET 8 / C#, AWS CDK, Python 3, React + TypeScript + Vite + Tailwind CSS, Terraform, AWS (Lambda, DynamoDB, S3, IoT Core, CloudFront, API Gateway, ECR), Okta OIDC.

**Region:** `eu-west-1`

---

## Repository Structure

```
SnoutSpotter/
├── AGENTS.md                          # This file
├── README.md                          # Getting started guide
├── SnoutSpotter.sln                   # .NET solution file
├── docs/
├── terraform/
│   └── okta/                          # Terraform for Okta provisioning
│       ├── main.tf                    # App, group, access policy, sign-on policy
│       ├── variables.tf
│       └── outputs.tf                 # okta_client_id, okta_issuer
├── src/
│   ├── api/                           # ASP.NET Core 8 API (runs on Lambda via Web Adapter)
│   │   ├── Controllers/
│   │   │   ├── ClipsController.cs          # GET /api/clips, GET /api/clips/{id}
│   │   │   ├── DetectionsController.cs     # GET /api/detections
│   │   │   ├── DevicesController.cs        # GET /api/device/devices, status, shadow, config
│   │   │   ├── DeviceUpdatesController.cs  # POST /api/device/{thingName}/update, releases
│   │   │   ├── DeviceCommandsController.cs # POST /api/device/{thingName}/command, logs
│   │   │   ├── ExportsController.cs        # POST /api/ml/export, GET/DELETE /api/ml/exports
│   │   │   ├── LabelsController.cs         # GET/PUT /api/ml/labels, auto-label, upload, rerun-inference
│   │   │   ├── ModelsController.cs         # GET /api/ml/models, activate, upload-url
│   │   │   ├── TrainingAgentsController.cs # GET /api/training/agents, trigger update
│   │   │   ├── TrainingJobsController.cs   # POST/GET /api/training/jobs, cancel, delete
│   │   │   └── StatsController.cs          # GET /api/stats, GET /api/stats/activity, GET /api/stats/health
│   │   ├── Models/ClipModels.cs       # Record types for API responses
│   │   ├── Services/
│   │   │   ├── ClipService.cs         # DynamoDB queries for clips
│   │   │   ├── ExportService.cs       # Training dataset export trigger and management
│   │   │   ├── HealthService.cs       # CloudWatch heartbeat checks
│   │   │   ├── LabelService.cs        # Label CRUD, breed, stats, upload, backfill
│   │   │   ├── SettingsService.cs     # Server settings CRUD + validation (int/float/select)
│   │   │   ├── PiUpdateService.cs     # IoT shadow reads/writes, OTA triggers, config validation
│   │   │   ├── S3PresignService.cs    # Presigned URL generation
│   │   │   ├── S3UrlService.cs        # S3 URL helpers
│   │   │   └── StatsRefreshService.cs # Read/write pre-computed stats from snout-spotter-stats table; triggers async refresh when stale
│   │   ├── StatsRefreshRunner.cs      # Minimal host entry point for stats-refresh Lambda (APP_MODE=stats-refresh)
│   │   ├── Program.cs                 # DI setup, JWT Bearer auth, AWS client registration
│   │   └── Dockerfile
│   │
│   ├── infra/                         # AWS CDK infrastructure (C#)
│   │   ├── Program.cs                 # Stack instantiation and wiring
│   │   ├── cdk.json                   # CDK context (oktaIssuer, allowedOrigin)
│   │   └── Stacks/
│   │       ├── CoreStack.cs           # S3, DynamoDB, ECR repos
│   │       ├── IoTStack.cs            # IoT Thing Group, IoT Policy, Credentials Provider Role Alias
│   │       ├── IngestStack.cs         # IngestClip Lambda + S3 event trigger
│   │       ├── InferenceStack.cs      # RunInference Lambda + S3 event trigger
│   │       ├── ApiStack.cs            # API Lambda + HTTP API Gateway + Okta JWT env vars
│   │       ├── PiMgmtStack.cs         # Pi Management Lambda + HTTP API Gateway (no auth)
│   │       ├── WebStack.cs            # S3 static site + CloudFront distribution
│   │       ├── MonitoringStack.cs     # CloudWatch alarms
│   │       └── CiCdStack.cs           # OIDC role for GitHub Actions
│   │
│   ├── shared/
│   │   └── SnoutSpotter.Contracts/        # Shared message types, server settings, settings reader
│   │       ├── Messages.cs               # InferenceMessage, BackfillMessage
│   │       ├── ServerSettings.cs         # Setting keys, defaults, validation specs (shared by API + all Lambdas)
│   │       └── SettingsReader.cs         # DynamoDB settings reader with 5-minute cache (used by Lambdas)
│   │
│   ├── lambdas/
│   │   ├── SnoutSpotter.Lambda.IngestClip/    # Triggered by S3 raw-clips upload
│   │   │   ├── Function.cs                    # Extracts keyframes via FFmpeg, writes DynamoDB
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.RunInference/  # Triggered by S3 keyframes upload OR SQS rerun queue
│   │   │   ├── Function.cs                    # Two-stage inference: COCO YOLO dog detector + MobileNetV3 classifier (or single-stage legacy mode)
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.AutoLabel/     # COCO-pretrained YOLOv8 dog detection on keyframes
│   │   │   ├── Function.cs                    # ONNX inference (model key from server settings), writes labels to DynamoDB
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.ExportDataset/ # Training dataset packaging
│   │   │   ├── Function.cs                    # Queries labels, balances classes; detection (YOLO) or classification (crops) export; mergeClasses for single-class detector
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.UpdateTrainingProgress/ # IoT Rule target for trainer MQTT progress
│   │   │   └── Function.cs                    # Deserialises TrainingProgressMessage, patches snout-spotter-training-jobs DynamoDB table
│   │   └── SnoutSpotter.Lambda.PiMgmt/        # Pi device + trainer registration API (no Okta auth)
│   │       ├── Controllers/DevicesController.cs
│   │       ├── Controllers/TrainersController.cs  # POST /api/trainers/register, DELETE, GET
│   │       ├── Services/DeviceProvisioningService.cs
│   │       ├── Program.cs
│   │       └── Dockerfile
│   │
│   ├── web/                           # React frontend
│   │   ├── src/
│   │   │   ├── App.tsx                # Router, sidebar, auth gates, logout
│   │   │   ├── api.ts                 # API client with Bearer token injection
│   │   │   ├── types.ts               # TypeScript interfaces (Clip, PiDevice, CameraStatus, etc.)
│   │   │   ├── auth/
│   │   │   │   └── oktaConfig.ts      # OktaAuth instance (PKCE, scopes)
│   │   │   ├── pages/
│   │   │   │   ├── Dashboard.tsx      # Stats overview
│   │   │   │   ├── ClipsBrowser.tsx   # Paginated clip grid
│   │   │   │   ├── ClipDetail.tsx     # Video player + keyframes + detections
│   │   │   │   ├── Detections.tsx     # Detection results list
│   │   │   │   ├── Labels.tsx         # ML label review: auto/manual labels, breed, bulk actions
│   │   │   │   ├── TrainingExports.tsx# Training dataset export: config form (class balancing, background), list, download
│   │   │   │   ├── Models.tsx         # YOLOv8 detection model version management: upload, list, activate
│   │   │   │   ├── SubmitTraining.tsx # New training job form: dataset, hyperparams, prefill from prev job
│   │   │   │   ├── TrainingJobDetail.tsx # Training job detail: stage timeline, epoch progress, metrics, cancel
│   │   │   │   ├── TrainingAgentDetail.tsx # Agent detail: status, GPU, current job, job history
│   │   │   │   ├── ServerSettings.tsx # Server settings editor: grouped sections, numeric + select inputs
│   │   │   │   ├── PiPackages.tsx    # Pi release version list, delete, and management
│   │   │   │   ├── SystemHealth.tsx   # Landing page: API health + device summary table
│   │   │   │   ├── DeviceDetail.tsx   # Per-device detail: status, version selector, services, camera, system, actions
│   │   │   │   ├── DeviceConfig.tsx   # Per-device remote config editor (24 settings)
│   │   │   │   ├── DeviceLogs.tsx     # Per-device log viewer with filters
│   │   │   │   ├── DeviceShadow.tsx   # Raw IoT device shadow JSON viewer
│   │   │   │   └── CommandHistory.tsx # Per-device command history
│   │   │   ├── constants.ts            # Shared constants: DOG_BREEDS
│   │   │   └── components/
│   │   │       ├── BoundingBoxOverlay.tsx
│   │   │       ├── ErrorBoundary.tsx   # Global error boundary with fallback UI
│   │   │       ├── LabelBadge.tsx      # Shared LabelBadge and DetectionBadge components
│   │   │       └── health/            # Shared: StatusBadge, UsageBar, AddDeviceDialog, formatUptime
│   │   ├── vite.config.ts
│   │   └── package.json
│   │
│   ├── pi/                            # Raspberry Pi Python scripts
│   │   ├── agent.py                   # Thin orchestrator: MqttManager, event queue, shadow delta dispatch, main loop
│   │   ├── watchdog.py                # Service watchdog: monitors core services, restarts on failure, reboots on persistent failure
│   │   ├── health.py                  # System health gathering: CPU, memory, disk, camera, upload stats
│   │   ├── shadow.py                  # IoT shadow building and reporting
│   │   ├── ota.py                     # OTA update: download, checksum verify, extract, deps, service sync, rollback
│   │   ├── remote_config.py           # Remote config validation and application from shadow delta
│   │   ├── log_shipping.py            # Journald log collection and MQTT publish
│   │   ├── commands.py                # Device command execution via MQTT (restart, reboot, clear-clips, etc.)
│   │   ├── iot_credential_provider.py # IoT Credentials Provider: temp STS creds via X.509 certs
│   │   ├── motion_detector.py         # Frame-differencing motion detection + recording + status file
│   │   ├── stream_manager.py          # Live stream: GStreamer/kvssink pipeline to Kinesis Video Streams
│   │   ├── uploader.py                # S3 multipart upload with retry + status file + ledger pruning + disk quota
│   │   ├── config_loader.py           # Shared config loading: deep-merges defaults.yaml + config.yaml + schema validation
│   │   ├── config_schema.py           # Allow-list and validation for remotely configurable settings
│   │   ├── defaults.yaml              # Default config values (shipped via OTA)
│   │   ├── config.yaml                # Device-specific overrides (NOT included in OTA packages)
│   │   ├── system-deps.txt            # System apt packages (shipped via OTA, installed during updates)
│   │   ├── custom-debs.txt            # Custom .deb packages from S3 (e.g. kvssink .deb, installed during OTA)
│   │   ├── setup-pi.sh                # Full automated setup: deps, registration, certs, credential provider, services
│   │   ├── requirements.txt           # Python deps: boto3, opencv, awsiotsdk, pyyaml
│   │   └── version.json               # Current Pi software version (written by OTA agent)
│   │
│   ├── ml/                            # ML training and verification scripts (run locally or via training agent)
│   │   ├── train_detector.py          # Fine-tune YOLOv8n on exported dataset → best.onnx
│   │   ├── train_classifier.py        # Train MobileNetV3-Small classifier on classification exports → best.onnx
│   │   ├── verify_classifier.py       # Verify classifier ONNX: input [1,3,224,224], output [1,2]
│   │   ├── test_detector.py           # Test the deployed detection model against S3 keyframes or local images
│   │   └── verify_onnx.py             # Verify ONNX model is compatible with RunInference/Function.cs
│   │
│   ├── shared/                        # Shared .NET class library (SnoutSpotter.Shared.Training)
│   │   ├── SnoutSpotter.Shared.Training.csproj
│   │   ├── GpuStatus.cs               # GPU metrics reported in agent shadow
│   │   ├── AgentReportedState.cs      # Full shadow reported state (agent → API)
│   │   ├── AgentDesiredState.cs       # Shadow desired state (API → agent): job, cancel, update
│   │   ├── ShadowEnvelope.cs          # Generic ShadowDesiredUpdate<T>, ShadowReportedUpdate<T>, ShadowDeltaMessage<T>
│   │   ├── TrainingJobDesired.cs      # Shadow job dispatch payload + TrainingJobParams config
│   │   ├── TrainingProgress.cs        # Per-epoch MQTT metrics
│   │   ├── TrainingResult.cs          # Final training result metrics
│   │   └── TrainingProgressMessage.cs # MQTT envelope for snoutspotter/trainer/+/progress topic
│   │
│   └── training-agent/                # .NET training agent (runs in Docker on GPU machine)
│       ├── Dockerfile                 # Multi-stage: dotnet SDK build + nvidia/cuda:12.1.1 runtime + Python/ultralytics
│       ├── docker-compose.yml         # NVIDIA runtime, trainer-state volume, AGENT_NAME env var
│       ├── updater.sh                 # Host-side lifecycle: watches exit codes, pulls new image on code=42
│       └── SnoutSpotter.TrainingAgent/
│           ├── Program.cs             # Startup: self-register if no state, load config, connect MQTT, poll SQS
│           ├── RegistrationService.cs # First-run: calls /api/trainers/register, saves certs + config to /app/state/
│           ├── SqsJobConsumer.cs      # Polls SQS training queue, extends visibility, dispatches to JobRunner
│           ├── JobRunner.cs           # Download ML scripts + dataset, run training, upload model, publish progress
│           ├── MqttManager.cs         # AWS IoT Core MQTT client (TLS, auto-reconnect, QoS 1)
│           ├── GpuInfo.cs             # Calls nvidia-smi, returns GpuStatus
│           ├── ProgressParser.cs      # Regex parser for YOLO training stdout → TrainingProgress (strips ANSI escapes)
│           ├── ClassifierProgressParser.cs # Parser for classifier training output (EPOCH N/M accuracy/f1 format)
│           └── Models/                # Agent-only config models (AgentConfig, IoTConfig, S3Config, etc.)
│
└── .github/workflows/
    ├── deploy.yml                     # Main pipeline: path-filtered, only deploys changed components
    ├── deploy-infra.yml               # CDK deploy all stacks
    ├── build-api-image.yml            # Docker build → ECR push
    ├── build-ingest-image.yml
    ├── build-inference-image.yml
    ├── build-pi-mgmt-image.yml
    ├── deploy-api.yml                 # CDK deploy ApiStack
    ├── deploy-ingest.yml
    ├── deploy-inference.yml
    ├── deploy-pi-mgmt.yml
    ├── deploy-web.yml                 # npm build → S3 sync → CloudFront invalidation
    ├── deploy-ml.yml                  # Package models → S3
    ├── deploy-okta.yml                # Terraform apply for Okta resources (S3 remote state)
    ├── build-kvssink.yml              # Build kvssink GStreamer plugin .deb for ARM64 → S3
    └── package-pi.yml                 # Package Pi release → S3, auto-bumps version from manifest
```

---

## Architecture & Data Flow

```
[Raspberry Pi + Camera]
    │ motion detected → record clip
    │ status files → ~/.snoutspotter/*.json
    ▼
[S3: raw-clips/YYYY/MM/DD/timestamp.mp4]
    │ S3 event notification
    ▼
[Lambda: IngestClip]
    ├──► [DynamoDB: snout-spotter-clips] (clip metadata)
    └──► [S3: keyframes/YYYY/MM/DD/timestamp_N.jpg]
              │ S3 event notification
              ▼
         [Lambda: RunInference]
              │ Custom YOLOv8 detection (my_dog / other_dog / no_dog + bounding boxes)
              ▼
         [DynamoDB: keyframe_detections list updated on clip record]

[React Dashboard] ──Okta JWT──► [API Lambda + API Gateway]
                                      │
                                      ├── DynamoDB (clips, detections)
                                      ├── S3 (presigned URLs for video/images)
                                      ├── CloudWatch (heartbeat metrics)
                                      └── IoT Core (device shadows for status/OTA)

[Pi Management Lambda + API Gateway]  ← no auth (called by Pi devices)
    │ Device registration/deregistration
    └── IoT Core (create thing, certificates, policy attachment)

[Pi snoutspotter-agent] ◄──MQTT──► [IoT Core]
    │ Single connection handles:
    ├── Shadow reporting (heartbeat, camera, system health)
    ├── OTA updates (watches shadow delta, self-updates)
    └── Log shipping (batches journald logs → snoutspotter/{thingName}/logs topic)
                                            ↓ (IoT Rule)
                                   CloudWatch Logs (/snoutspotter/pi-logs)
                                            ↓
                                   [API Lambda] ──► [React Dashboard: Device Logs page]
```

### Multi-Pi Device Management

- Devices are registered as IoT Things in the `snoutspotter-pis` thing group
- Each device gets unique X.509 certificates for MQTT connectivity and AWS credential exchange via IoT Credentials Provider
- Device shadows track: version, hostname, heartbeat, update status, service states, camera health, system metrics, upload stats, log shipping status
- OTA updates triggered by writing `desired.version` to the device shadow
- Pi agent requests current shadow on startup to catch any pending delta (not just push notifications)
- `config.yaml` is excluded from OTA packages — device-specific settings are preserved across updates

---

## Authentication

### Frontend (Okta OIDC/PKCE)
- All routes gated by `RequiredAuth` — redirects to Okta login if unauthenticated
- PKCE flow via `@okta/okta-react` + `@okta/okta-auth-js`
- Access tokens injected into all API calls via `setAuthGetter` in `api.ts`
- Logout button in sidebar

### API (JWT Bearer)
- All controllers have `[Authorize]` — validates Okta JWT on every request
- `Program.cs` configures `AddAuthentication().AddJwtBearer()` with Okta issuer/audience
- **Pi Management API has no auth** — Pi devices cannot authenticate to Okta

### Pi Devices (IoT Credentials Provider)
- Pi devices use X.509 certificates to obtain temporary STS credentials via the IoT Credentials Provider
- `iot_credential_provider.py` calls the credential provider HTTPS endpoint with the device cert, returns a `boto3.Session` with auto-refreshing `RefreshableCredentials`
- Used by `uploader.py` (S3 PutObject) and `agent.py` (CloudWatch PutMetricData, S3 GetObject for OTA)
- Falls back to default boto3 credentials if `credentials_provider.endpoint` is not configured
- The credential provider endpoint is returned during device registration and stored in `config.yaml`
- Can also be pushed to existing devices via the shadow config system (`credentials_provider.endpoint` key)

### Okta Terraform resources
- `okta_app_oauth` — SPA app (PKCE, `authorization_code`)
- `okta_group` — `SnoutSpotter Users` (add users here to grant access)
- `okta_app_group_assignment` — assigns group to app
- `okta_auth_server_policy` + rule — allows token issuance for the app
- `okta_app_signon_policy` + rule — password only, no MFA (`factor_mode = "1FA"`)
- State stored in S3 at `terraform/okta/terraform.tfstate`

---

## AWS Infrastructure (CDK Stacks)

All stacks are defined in `src/infra/Stacks/` and wired in `src/infra/Program.cs`.

**Stack dependency order:**
1. **CoreStack** — foundational resources (no dependencies)
2. **IoTStack** — IoT resources (depends on CoreStack for DataBucket)
3. **IngestStack** — depends on CoreStack
4. **InferenceStack** — depends on CoreStack
5. **AutoLabelStack** — depends on CoreStack
6. **ExportDatasetStack** — depends on CoreStack
7. **TrainingStack** — depends on CoreStack (SQS queue, UpdateTrainingProgress Lambda, IoT Rule)
8. **ApiStack** — depends on CoreStack, reads CDK context + SSM params from other stacks
9. **PiMgmtStack** — depends on CoreStack
10. **WebStack** — standalone (S3 + CloudFront)
11. **MonitoringStack** — depends on CoreStack
12. **CiCdStack** — standalone (OIDC role, S3 permissions include `terraform/*`)

**IMPORTANT — No cross-stack dependencies between Lambda stacks.** Each Lambda stack must only depend on CoreStack (for ECR repos, tables, buckets). Never pass outputs from one Lambda stack to another via CDK props — this creates deploy-time coupling where deploying stack A forces CDK to also deploy stack B, which fails if stack B's Docker image wasn't built for the current commit. Instead, use SSM parameters: the source stack writes to `/snoutspotter/{stack}/{param}` and the consuming stack reads via `StringParameter.ValueForStringParameter()`, which resolves at CloudFormation deploy time without a CDK dependency. See IoTStack → PiMgmtStack and AutoLabelStack → ApiStack for examples.

**Key resources by stack:**

| Stack | Resources |
|-------|-----------|
| CoreStack | S3 `snout-spotter-{account}`, DynamoDB `snout-spotter-clips` + `snout-spotter-labels` + `snout-spotter-exports` + `snout-spotter-settings` + `snout-spotter-training-jobs` + `snout-spotter-stats`, ECR repos |
| IoTStack | Thing Group `snoutspotter-pis`, IoT Policy `snoutspotter-pi-policy`, IAM Role `snoutspotter-pi-credentials`, Role Alias `snoutspotter-pi-role-alias`, CloudWatch Log Group `/snoutspotter/pi-logs`, IoT Topic Rule `snoutspotter_pi_logs` |
| ApiStack | Docker Lambda `snout-spotter-api`, HTTP API Gateway, Okta JWT env vars; also hosts `snout-spotter-stats-refresh` Lambda (same image, `APP_MODE=stats-refresh`) invoked on-demand |
| PiMgmtStack | Docker Lambda `snout-spotter-pi-mgmt`, HTTP API Gateway |
| IngestStack | Docker Lambda triggered by S3 `raw-clips/` events |
| InferenceStack | Docker Lambda triggered by S3 `keyframes/` events + SQS `snout-spotter-rerun-inference` queue (BatchSize=1, MaxConcurrency=3) |
| WebStack | S3 static site bucket, CloudFront distribution |
| AutoLabelStack | Docker Lambda (2 GB) + SQS backfill queue (BatchSize=1, MaxConcurrency=2) + settings table read |
| ExportDatasetStack | Docker Lambda for training dataset packaging (class balancing, background filtering) |
| TrainingStack | SQS job queue, UpdateTrainingProgress Lambda (IoT Rule target), training-jobs DynamoDB table |
| CiCdStack | GitHub Actions OIDC role with S3, ECR, Lambda, CloudFront, CFn permissions |

---

## API Endpoints

All main API endpoints require a valid Okta JWT Bearer token.

### Main API (`snout-spotter-api` Lambda)

**Clips:**
- `GET /api/clips?limit=20&nextPageKey=xxx&date=YYYY/MM/DD` — list clips (cursor pagination)
- `GET /api/clips/{id}` — get clip detail with presigned video/keyframe URLs

**Stats:**
- `GET /api/stats` — dashboard stats (total clips, today's clips, detections, Pi online status). Served from `snout-spotter-stats` cache; triggers async refresh via `snout-spotter-stats-refresh` Lambda when stale (>5 min). Falls back to live DynamoDB queries when cache is cold.
- `GET /api/stats/activity?days=14` — 14-day clip histogram. Cached same as above (cache covers 14-day window only).
- `GET /api/stats/health` — multi-device health (all Pi shadows with camera, system, upload stats)

**Detections:**
- `GET /api/detections?type=my_dog&limit=50` — list detection results

**ML Labels & Training:**
- `POST /api/ml/auto-label` — trigger auto-labeling for keyframes
- `GET /api/ml/labels/stats` — label counts + breed distribution (cached in `snout-spotter-stats`, same stale-while-revalidate pattern)
- `GET /api/ml/labels?reviewed=false&confirmedLabel=my_dog&breed=Labrador+Retriever` — paginated labels (filterable)
- `PUT /api/ml/labels/{keyframeKey}` — update label (`confirmedLabel`, optional `breed`)
- `POST /api/ml/labels/bulk-confirm` — bulk confirm labels with breed
- `POST /api/ml/labels/upload?label=other_dog&breed=Chihuahua` — upload training images
- `POST /api/ml/labels/backfill-breed` — set breed on existing labels missing it
- `POST /api/ml/export` — trigger training dataset export (optional body: `maxPerClass`, `includeBackground`, `backgroundRatio` for class balancing)
- `GET /api/ml/exports` — list exports
- `GET /api/ml/exports/{exportId}/download` — presigned download URL
- `DELETE /api/ml/exports/{exportId}` — delete export

**ML Models (DynamoDB-backed registry):**
- `GET /api/ml/models?type=detector` — list model versions from `snout-spotter-models` table (includes source, metrics, training job link)
- `POST /api/ml/models/upload-url?version=v2.0&type=classifier` — presigned PUT URL + pre-registers model in DynamoDB with `source: "upload"`
- `POST /api/ml/models/activate?version=v2.0&type=detector` — sets model as active in DDB, deactivates previous, copies S3 to `best.onnx`
- `DELETE /api/ml/models/{type}/{version}` — delete a model (rejects active models)
- `POST /api/ml/rerun-inference` — bulk re-run inference on clips (optional `dateFrom`/`dateTo`), queues to SQS

**Training Agents & Jobs:**
- `GET /api/training/agents` — list registered training agents with online status (from IoT shadow)
- `GET /api/training/agents/{thingName}` — single agent status + full reported shadow
- `POST /api/training/agents/{thingName}/update` — trigger agent container update (`{"version": "1.2.0"}`)
- `POST /api/training/jobs` — submit a training job (dispatched via SQS to an idle agent)
- `GET /api/training/jobs?status=running&limit=50` — list training jobs
- `GET /api/training/jobs/{jobId}` — get job detail (config, progress, result as native typed objects)
- `POST /api/training/jobs/{jobId}/cancel` — request job cancellation
- `DELETE /api/training/jobs/{jobId}` — delete a training job record

**Server Settings:**
- `GET /api/settings` — all settings with current values, defaults, specs, and options
- `PUT /api/settings/{key}` — update a setting (validates type, range, and select options)
- `POST /api/settings/reset` — reset all settings to defaults

**Device Management (OTA + Config + Commands):**
- `GET /api/device/devices` — list all Pi devices with full shadow state
- `GET /api/device/{thingName}/status` — single device status
- `GET /api/device/{thingName}/shadow` — raw IoT device shadow JSON
- `GET /api/device/{thingName}/config` — current configurable settings
- `POST /api/device/{thingName}/config` — update device config (validated API-side + Pi-side)
- `POST /api/device/{thingName}/update` — trigger OTA update for one device (optional `version` body param for specific version)
- `POST /api/device/update-all` — trigger OTA update for all devices (optional `version` body param)
- `GET /api/device/releases` — list all Pi release versions from S3 with size, date, and isLatest flag
- `DELETE /api/device/releases/{version}` — delete a release tarball from S3 (cannot delete latest)
- `POST /api/device/{thingName}/command` — send command (reboot, restart-*, clear-clips, clear-backups)
- `GET /api/device/{thingName}/command/{commandId}` — poll command result
- `GET /api/device/{thingName}/commands` — command history
- `GET /api/device/{thingName}/logs?minutes=60&level=INFO&service=motion&limit=200` — query device logs from CloudWatch

### Pi Management API (`snout-spotter-pi-mgmt` Lambda — no auth)

- `GET /api/devices` — list registered device thing names
- `POST /api/devices/register` — register new device (`{"name": "garden"}`) → returns certs, IoT endpoint, credential provider endpoint
- `DELETE /api/devices/{thingName}` — deregister device
- `GET /api/trainers` — list registered training agents
- `POST /api/trainers/register` — register new training agent (`{"name": "gregs-pc"}`) → returns certs + endpoints; thing name: `snoutspotter-trainer-{name}`
- `DELETE /api/trainers/{thingName}` — deregister training agent

---

## DynamoDB Schema

**Table:** `snout-spotter-clips` | **Billing:** Pay-per-request

| Attribute | Type | Description |
|-----------|------|-------------|
| `clip_id` (PK) | String | ISO timestamp identifier |
| `s3_key` | String | S3 key for the raw clip |
| `timestamp` | Number | Unix timestamp |
| `duration_s` | Number | Clip duration in seconds |
| `date` | String | `YYYY/MM/DD` |
| `keyframe_count` | Number | Number of extracted keyframes |
| `keyframe_keys` | String Set | S3 keys for keyframe images |
| `detection_type` | String | `pending`, `my_dog`, `other_dog`, `no_dog`, `none` |
| `detection_count` | Number | Number of detections found |
| `keyframe_detections` | List | List of Maps: per-keyframe detection results (see below) |
| `created_at` | String | ISO 8601 timestamp |
| `inference_at` | String | ISO 8601 timestamp of last inference run |

**`keyframe_detections` structure** (List of Maps):
```json
[
  {
    "keyframeKey": "keyframes/2026/03/27/clip_0001.jpg",
    "label": "my_dog",
    "detections": [
      {
        "label": "my_dog",
        "confidence": 0.92,
        "boundingBox": { "x": 100, "y": 50, "width": 200, "height": 300 }
      }
    ]
  }
]
```

**GSIs:**
- `all-by-time` — PK: fixed value, SK: `timestamp` (server-side ordering across all clips)
- `by-date` — PK: `date`, SK: `timestamp`
- `by-device` — PK: `device`, SK: `timestamp`
- `by-detection` — PK: `detection_type`, SK: `timestamp`

### Labels Table

**Table:** `snout-spotter-labels` | **Billing:** Pay-per-request

| Attribute | Type | Description |
|-----------|------|-------------|
| `keyframe_key` (PK) | String | S3 key for the keyframe image |
| `clip_id` | String | Source clip reference (or "uploaded" for manual uploads) |
| `auto_label` | String | `dog`, `no_dog` (ML detection result) |
| `confirmed_label` | String | `my_dog`, `other_dog`, `no_dog` (human review) |
| `breed` | String | Dog breed (e.g., "Labrador Retriever", "Chihuahua") |
| `confidence` | Number | ML detection confidence score |
| `bounding_boxes` | String | JSON array of detection boxes |
| `reviewed` | String | `"true"` or `"false"` |
| `labelled_at` | String | ISO 8601 timestamp |
| `reviewed_at` | String | ISO 8601 timestamp |

**GSIs:**
- `by-review` — PK: `reviewed`, SK: `labelled_at`
- `by-label` — PK: `auto_label`, SK: `labelled_at`
- `by-confirmed-label` — PK: `confirmed_label`, SK: `labelled_at`

### Exports Table

**Table:** `snout-spotter-exports` | **Billing:** Pay-per-request

| Attribute | Type | Description |
|-----------|------|-------------|
| `export_id` (PK) | String | Unique export identifier |
| `status` | String | `running`, `complete`, `failed` |
| `created_at` | String | ISO 8601 timestamp |
| `config` | Map (M) | Export options: `include_background` (BOOL), `background_ratio` (N), `max_per_class` (N, optional) |
| `s3_key` | String | S3 key for the exported zip |
| `total_images` | Number | Total images included (my_dog + other_dog with boxes + no_dog) |
| `my_dog_count` | Number | Count of my_dog labels included |
| `not_my_dog_count` | Number | Count of other_dog labels included |
| `no_dog_count` | Number | Count of no_dog labels included (background images) |
| `skipped_my_dog_count` | Number | my_dog labels excluded — no bounding box |
| `skipped_other_dog_count` | Number | other_dog labels excluded — no bounding box |
| `train_count` | Number | Training split count |
| `val_count` | Number | Validation split count |
| `size_mb` | Number | Zip file size |

### Training Jobs Table

**Table:** `snout-spotter-training-jobs` | **Billing:** Pay-per-request

| Attribute | Type | Description |
|-----------|------|-------------|
| `job_id` (PK) | String | Unique job identifier (`tj-YYYYMMDD-HHmmss-xxxx`) |
| `status` | String | `pending`, `downloading`, `scanning`, `training`, `uploading`, `complete`, `failed`, `cancelled` |
| `agent_thing_name` | String | IoT thing name of the agent running the job |
| `export_id` | String | Source dataset export ID |
| `export_s3_key` | String | S3 key of the dataset zip |
| `config` | Map (M) | Training hyperparameters (`epochs`, `batch_size`, `image_size`, `learning_rate`, `workers`, `model_base`, `resume_from`) |
| `progress` | Map (M) | Latest progress (`epoch`, `total_epochs`, `train_loss`, `mAP50`, `best_mAP50`, `elapsed_seconds`, `eta_seconds`, `gpu_util_percent`, `gpu_temp_c`, `download_bytes`, `download_total_bytes`, `download_speed_mbps`) |
| `result` | Map (M) | Final result (`model_s3_key`, `model_size_mb`, `final_mAP50`, `final_mAP50_95`, `precision`, `recall`, `total_epochs`, `best_epoch`, `training_time_seconds`, `dataset_images`, `classes`) |
| `error` | String | Error message if failed |
| `failed_stage` | String | Which stage failed (`preparing`, `downloading`, `extracting`, `scanning`, `training`, `uploading`) |
| `checkpoint_s3_key` | String | S3 key of last.pt checkpoint (saved on cancel/interrupt) |
| `created_at` | String | ISO 8601 |
| `started_at` | String | ISO 8601 — set on first `downloading` or `scanning` or `training` status |
| `completed_at` | String | ISO 8601 — set on `complete`, `failed`, `cancelled`, `interrupted` |
| `updated_at` | String | ISO 8601 — updated on every status change |

**Important:** `config`, `progress`, and `result` are stored as native DynamoDB Maps (not JSON strings). The API deserialises these directly — no JSON-in-JSON. `TrainingService.cs` has `ToMap`/`FromConfigMap`/`FromProgressMap`/`FromResultMap` helpers.

### Models Table

**Table:** `snout-spotter-models` | **Billing:** Pay-per-request

| Attribute | Type | Description |
|-----------|------|-------------|
| `model_id` (PK) | String | `{type}#{version}` e.g. `detector#v20250413-120345` |
| `model_type` | String | `"detector"` or `"classifier"` |
| `version` | String | e.g. `v20250413-120345` or `v2.0` |
| `s3_key` | String | Full S3 key to the `.onnx` file |
| `size_bytes` | Number | File size in bytes |
| `status` | String | `"uploaded"`, `"active"`, `"inactive"` |
| `created_at` | String | ISO 8601 timestamp |
| `source` | String | `"training"` (from training agent) or `"upload"` (manual via UI) |
| `training_job_id` | String | (optional) Link to the training job that produced this model |
| `export_id` | String | (optional) Link to the dataset export used for training |
| `notes` | String | (optional) User-provided or auto-generated description |
| `metrics` | Map (M) | (optional) Final metrics: `final_mAP50`, `precision`, `recall` (detector) or `accuracy`, `f1_score`, `precision`, `recall` (classifier) |

**GSI:** `by-type` — PK: `model_type`, SK: `created_at` (list all detector/classifier models sorted by date)

**Service:** `ModelService.cs` manages CRUD. Activation deactivates the previous active model, sets new status to `"active"`, and copies S3 object to `best.onnx` for RunInference compatibility. Training agent auto-registers models after upload with `source: "training"` and backfilled metrics.

### Stats Table

**Table:** `snout-spotter-stats` | **Billing:** Pay-per-request

Holds pre-computed dashboard metrics. Written by `snout-spotter-stats-refresh` Lambda; read by `StatsRefreshService` in the API.

| `stat_id` (PK) | Contents |
|----------------|----------|
| `"dashboard"` | `total_clips`, `clips_today`, `total_detections`, `my_dog_detections`, `last_upload_time`, `pi_online_count`, `pi_total_count`, `refreshed_at` (all Number or String) |
| `"activity"` | `data` (JSON string: `[{date, count}]` for 14 days), `refreshed_at` |
| `"label_stats"` | `data` (JSON string of label stats object from `LabelService.GetStatsAsync()`), `refreshed_at` |

**Stale-while-revalidate:** When `refreshed_at` is older than 5 minutes, `StatsRefreshService.TriggerRefreshIfStale()` fires a `lambda:InvokeFunction` (Event type, async) to `snout-spotter-stats-refresh`. The API returns cached data immediately; the next request after the Lambda completes gets fresh data. A per-instance 4-minute cooldown prevents duplicate invocations.

---

## Pi Software

**Language:** Python 3 with `picamera2`, `opencv`, `boto3`, `awsiotsdk`
**Config:** Two-layer system — `defaults.yaml` (shipped via OTA, all default values) merged with `config.yaml` (device-specific overrides, excluded from OTA). `config_loader.py` deep-merges them at load time.
**System deps:** `system-deps.txt` lists apt packages; OTA installs new/changed packages automatically.
**Custom debs:** `custom-debs.txt` lists S3 paths to `.deb` packages (e.g., the kvssink GStreamer plugin). OTA downloads and installs them via `dpkg -i` when the list changes.

**Services (systemd):**

| Service | Script | Purpose |
|---------|--------|---------|
| `snoutspotter-motion` | `motion_detector.py` | Motion detection, records 1080p H.264 clips, writes `~/.snoutspotter/motion-status.json` |
| `snoutspotter-uploader` | `uploader.py` | Uploads clips to S3, writes `~/.snoutspotter/uploader-status.json`, ledger pruning, disk quota |
| `snoutspotter-agent` | `agent.py` | Single MQTT connection (MqttManager): shadow reporting, OTA updates, remote config, commands, live stream control |
| `snoutspotter-watchdog` | `watchdog.py` | Monitors core services every 30s, restarts on failure (60s cooldown), reboots if all fail 5+ times |

**Status files** (`~/.snoutspotter/`):
- `motion-status.json` — `cameraOk`, `lastMotionAt`, `lastRecordingStartedAt`, `recordingsToday`
- `uploader-status.json` — `lastUploadAt`, `uploadsToday`, `failedToday`
- `watchdog-status.json` — `failureCounts`, `totalRestarts`, `totalReboots`, `recentEvents`

**IoT shadow reported state:**
```json
{
  "state": {
    "reported": {
      "version": "1.0.2",
      "hostname": "snoutspotter-01",
      "lastHeartbeat": "2026-03-29T10:00:00Z",
      "updateStatus": "idle",
      "services": {"motion": "active", "uploader": "active", "agent": "active", "watchdog": "active"},
      "camera": {
        "connected": true,
        "healthy": true,
        "sensor": "imx708",
        "resolution": "2304x1296",
        "recordResolution": "1920x1080"
      },
      "lastMotionAt": "2026-03-29T09:45:00Z",
      "lastUploadAt": "2026-03-29T09:46:00Z",
      "uploadStats": {"uploadsToday": 5, "failedToday": 0, "totalUploaded": 120},
      "clipsPending": 0,
      "logShipping": true,
      "streaming": false,
      "system": {
        "cpuTempC": 52.3,
        "memUsedPercent": 45.2,
        "diskUsedPercent": 23.1,
        "diskFreeGb": 11.4,
        "uptimeSeconds": 86400,
        "loadAvg": [0.5, 0.3, 0.2],
        "piModel": "Raspberry Pi Zero 2 W Rev 1.0",
        "ipAddress": "192.168.2.227",
        "wifiSignalDbm": -42,
        "wifiSsid": "MyNetwork",
        "pythonVersion": "3.11.2"
      }
    }
  }
}
```

**OTA process:**
1. Dashboard triggers update → API writes `desired.version` to shadow
2. Agent receives delta (or detects it on startup via `shadow/get`)
3. Downloads `releases/pi/v{version}.tar.gz` from S3
4. Verifies SHA256 checksum against S3 object metadata (warns if no metadata present)
5. Backs up current version to `~/.snoutspotter/backups/{version}/`
6. Extracts package with path traversal protection (skipping `config.yaml`; `defaults.yaml` is updated)
7. Installs system apt packages if `system-deps.txt` has changed (compares against backup)
8. Downloads and installs custom `.deb` packages from S3 if `custom-debs.txt` has changed
9. Installs Python dependencies from `requirements.txt`
10. Syncs systemd service files — creates and enables any new services from `SERVICE_MANIFEST`
11. Restarts services, waits 30s, checks health
12. Reports `updateStatus: success` or rolls back on failure

**Pi version bumping:** `package-pi.yml` reads the current version from the S3 manifest and increments the patch number — guarantees a unique version every run.

### Remote Config (29 settings)

Settings are validated in two places: API-side (`PiUpdateService.ConfigurableKeys`) and Pi-side (`config_schema.CONFIGURABLE_KEYS`). Both must match.

| Section | Settings |
|---------|----------|
| Motion | `threshold`, `blur_kernel`, `min_area` |
| Camera | `detection_fps`, `preview_resolution`, `record_resolution`, `record_fps`, `encoding_bitrate` |
| Recording | `max_clip_length`, `pre_buffer`, `pre_buffer_enabled`, `post_motion_buffer`, `ffmpeg_timeout_seconds` |
| Upload | `max_retries`, `retry_delay`, `delete_after_upload`, `file_stability_seconds`, `min_free_disk_mb`, `ledger_retention_days` |
| Health | `interval_seconds` |
| Log Shipping | `enabled`, `batch_interval_seconds`, `max_lines_per_batch`, `min_level` |
| Streaming | `timeout_seconds`, `resolution`, `framerate`, `bitrate` |
| Credentials | `credentials_provider.endpoint` |

Config changes are written to the IoT shadow desired state, picked up by the Pi agent via delta, validated, applied to `config.yaml`, and signalled to affected services via touch-files.

---

## ML Training Workflow

**Inference pipeline:** Two modes controlled by `inference.pipeline_mode` server setting:
- **Single-stage (legacy):** Fine-tuned YOLOv8n with 2 classes (`my_dog`=0, `other_dog`=1). Output `[1, 6, 8400]`.
- **Two-stage (recommended):** COCO-pretrained YOLO detects all dogs (class 16), then a MobileNetV3-Small classifier identifies `my_dog` vs `other_dog` on each cropped patch. Combined confidence = detector × classifier. Classifier input: `[1,3,224,224]`, output: `[1,2]` (softmax).

**S3 model paths:**
- Detector: `models/dog-detector/best.onnx` (active), `models/dog-detector/versions/{version}/best.onnx`
- Classifier: `models/dog-classifier/best.onnx` (active), `models/dog-classifier/versions/{version}/best.onnx`
- Model type is parameterized via `?type=detector|classifier` on model API endpoints and Models page tabs.

**AutoLabel** uses COCO-pretrained YOLOv8 models (`models/yolov8{n,s,m}.onnx`) selected via the `autolabel.model_key` server setting (default: `yolov8m.onnx`) for generic dog detection to generate labels.

### Training Agent (recommended)

A .NET agent (`src/training-agent/`) runs in Docker on a GPU machine, registers itself with IoT Core, and receives jobs via IoT shadow.

**First-run setup (one time):**
```bash
# src/training-agent/.env
ECR_REGISTRY=<account-id>.dkr.ecr.eu-west-1.amazonaws.com
IMAGE_TAG=v1.0.6
AGENT_NAME=gregs-pc   # becomes IoT thing: snoutspotter-trainer-gregs-pc

aws ecr get-login-password --region eu-west-1 | docker login --username AWS --password-stdin $ECR_REGISTRY
docker compose pull && docker compose up -d
```
On first start the agent calls `POST /api/trainers/register`, saves certs + `config.yaml` to the `trainer-state` Docker volume, and connects. Subsequent starts skip registration.

**Dispatch a job:** Submit via the dashboard (Training page) → select job type (detector or classifier) → API dispatches via SQS with `jobType` → agent downloads ML scripts + dataset from S3, runs `train_detector.py` or `train_classifier.py` based on job type, uploads `best.onnx` to `models/dog-detector/versions/` or `models/dog-classifier/versions/`, then registers the model in the `snout-spotter-models` DynamoDB table with `source: "training"`, linked `training_job_id`, and final metrics.

**Job stages (in order):**

| Status | Description |
|--------|-------------|
| `pending` | Job queued, not yet picked up |
| `downloading` | Agent downloading ML scripts and dataset zip from S3 |
| `scanning` | YOLO scanning all training/validation images and labels before first epoch |
| `training` | Training epochs running |
| `uploading` | Uploading best.onnx to S3 |
| `complete` | Model uploaded, result published |
| `failed` | Failed — `error` and `failed_stage` fields indicate where |
| `cancelled` | Cancelled by user request |

**Progress:** Published to `snoutspotter/trainer/{thingName}/progress` → IoT Rule → `UpdateTrainingProgress` Lambda → DynamoDB → dashboard. For detector jobs, `ProgressParser` publishes on each new epoch number, then updates with mAP50 when the `all` metrics line arrives. For classifier jobs, `ClassifierProgressParser` parses `EPOCH N/M accuracy=X f1=X` lines. The Python process is launched with `python3 -u` to disable stdout buffering.

**Failed stage tracking:** `JobRunner` tracks `currentStage` and calls `PublishError(jobId, error, stage)` with the stage name. The `UpdateTrainingProgress` Lambda stores it as `failed_stage` in DynamoDB. The UI timeline highlights the failed node in red.

**Self-update:** API writes `desired.agentVersion` → agent exits with code 42 → `updater.sh` pulls new image and restarts.

**Docker notes:**
- `shm_size: '8gb'` — required for PyTorch DataLoader workers; the default 64MB `/dev/shm` causes Bus error with multiple workers
- Docker image includes: `torch`, `torchvision`, `ultralytics`, `onnx`, `onnxruntime`, `boto3`, `Pillow`, `libxcb1`, `libgl1`, `libglib2.0-0` (OpenCV/ultralytics dependencies)
- ML scripts are downloaded from S3 (`releases/ml-training/latest.json`) at job start; bump version via `package-ml-training.yml` workflow dispatch

**ML script notes (`src/ml/train_detector.py`):**
- When called with `--data <dir>` (agent's path), `output_dir` is set to `<dir>/runs` (absolute). This prevents ultralytics from nesting the save dir under its default `runs/detect/` prefix, which would break the `best.pt` lookup.
- ONNX export uses `opset=12, simplify=False` — required for RunInference's tensor parsing.

### Manual training (scripts only)

Training scripts in `src/ml/` can be run directly if preferred.

**End-to-end flow:**
1. Pi records clips → keyframes extracted → AutoLabel Lambda detects dogs + bounding boxes
2. Human reviews labels in dashboard (Labels page) — confirms `my_dog`/`other_dog`/`no_dog` + breed
3. Export dataset from dashboard (Training Exports page):
   - **Detection export:** YOLO format with bounding box labels. Use `mergeClasses=true` for single-class dog detector.
   - **Classification export:** Crops bounding boxes into `train/my_dog/`, `train/other_dog/` folders for classifier training.
4. Train via dashboard (submit training job) or manually:
   - Detector: `python src/ml/train_detector.py --data <dir>` → outputs `best.onnx`
   - Classifier: `python src/ml/train_classifier.py --data <dir>` → outputs `best.onnx`
5. Verify: `python src/ml/verify_onnx.py` (detector) or `python src/ml/verify_classifier.py` (classifier)
6. Upload via Models page (select Detector or Classifier tab) → activate → RunInference picks up on next cold start

**Training script flags:**
- `train_detector.py`: `--data <dir>`, `--zip <file>`, `--resume <last.pt>`, `--epochs`, `--batch`, `--imgsz`, `--workers`
- `train_classifier.py`: `--data <dir>`, `--zip <file>`, `--epochs` (50), `--batch` (32), `--imgsz` (224), `--lr` (0.001), `--workers`, `--patience` (10)

**Export options** (configured per-export via the UI or `POST /api/ml/export` body):
- `exportType` — `"detection"` (YOLO format) or `"classification"` (cropped image folders)
- `maxPerClass` — target count per class; oversamples minority (duplicates), undersamples majority (random pick)
- `includeBackground` — whether to include `no_dog` images in detection exports (default true)
- `backgroundRatio` — max no_dog images as fraction of total dog images (default 1.0)
- `cropPadding` — extra padding around bounding box as fraction of box size for classification exports (default 0.1)
- `mergeClasses` — when true, all dog labels use class 0 ("dog") for training a single-class detector (default false)

**Detection dataset format** (from ExportDataset Lambda):
```
dataset.yaml          ← names: { 0: my_dog, 1: other_dog } or { 0: dog } when mergeClasses=true
images/train/
images/val/
labels/train/         ← YOLO format: class cx cy w h (normalised)
labels/val/
manifest.json
labels.csv
```

**Classification dataset format:**
```
train/my_dog/*.jpg
train/other_dog/*.jpg
val/my_dog/*.jpg
val/other_dog/*.jpg
manifest.json
```

### Shared types (`src/shared/SnoutSpotter.Shared.Training`)

All IoT shadow and MQTT message types are defined once and shared between the training agent, API, and UpdateTrainingProgress Lambda. Key types: `AgentReportedState`, `AgentDesiredState`, `TrainingJobDesired`, `TrainingProgressMessage`, `TrainingProgress`, `TrainingResult`, `GpuStatus`, and the generic `ShadowDesiredUpdate<T>` / `ShadowReportedUpdate<T>` / `ShadowDeltaMessage<T>` envelope helpers.

---

## CI/CD

**Platform:** GitHub Actions with OIDC-based AWS auth (no long-lived credentials).

**Main pipeline** (`deploy.yml`) — path-filtered, only runs jobs for changed components:

| Path changed | Jobs triggered |
|-------------|----------------|
| `src/infra/**` | infra + all downstream |
| `src/api/**` | build API image → deploy API |
| `src/web/**` | deploy web |
| `src/lambdas/Ingest/**` | build ingest image → deploy ingest |
| `src/lambdas/RunInference/**` | build inference image → deploy inference |
| `src/lambdas/PiMgmt/**` | build pi-mgmt image → deploy pi-mgmt |
| `src/pi/**` | nothing (handled by `package-pi.yml`) |
| `terraform/okta/**` | nothing (handled by `deploy-okta.yml`) |

**Required GitHub secrets:**

| Secret | Description |
|--------|-------------|
| `AWS_ROLE_ARN` | OIDC role ARN (CiCdStack output) |
| `WEB_BUCKET_NAME` | S3 bucket for frontend assets |
| `CLOUDFRONT_DISTRIBUTION_ID` | For cache invalidation |
| `API_URL` | Main API Gateway URL |
| `DATA_BUCKET_NAME` | Data S3 bucket name |
| `OKTA_API_TOKEN` | Okta API token for Terraform |

**Required GitHub vars (non-secret):**

| Var | Description |
|-----|-------------|
| `OKTA_ISSUER` | `https://{org}.okta.com/oauth2/default` |
| `SNOUTSPOTTER_OKTA_CLIENT_ID` | Okta app client ID |

---

## Development Conventions

- **.NET 8** with top-level statements in `Program.cs`
- **Lambdas** use Docker images — ASP.NET Core ones use Lambda Web Adapter (`AWS_LWA_PORT=8080`)
- **CDK stacks** pass dependencies via typed `*Props` record classes
- **Pi Management API has no auth** — Pi devices connect directly, cannot use Okta
- **CORS** is locked to `allowedOrigin` on main API; open on Pi Management
- **Naming:** IoT things prefixed `snoutspotter-` (e.g. `snoutspotter-garden`)
- **S3 layout:** `raw-clips/YYYY/MM/DD/`, `keyframes/YYYY/MM/DD/`, `training-uploads/`, `training-exports/`, `models/dog-classifier/versions/` + `best.onnx` + `active.json`, `models/yolov8n.onnx` + `yolov8s.onnx` + `yolov8m.onnx` (COCO pretrained for AutoLabel), `releases/pi/`, `releases/ml-training/`, `training-checkpoints/`, `terraform/`

---

## Known Gotchas

1. **`AmazonIotDataClient` requires `ServiceURL`**, not `RegionEndpoint` — obtained via `iot:DescribeEndpoint` at startup.

2. **`iot:DescribeEndpoint` needs `Resource: "*"`** — global IAM action, cannot be scoped.

3. **All IoT IAM actions use `iot:` prefix** — not `iot-data:` or `iotdata:`. This applies to data plane actions like `GetThingShadow` too.

4. **OTA delta notifications are not replayed** — the agent must call `shadow/get` on startup to catch any delta that arrived while it was offline.

5. **Only one MQTT connection per client ID** — the old separate `health.py` + `ota_agent.py` conflicted. The merged `agent.py` uses a single connection via `MqttManager`, which tracks connection state and re-subscribes on reconnect.

6. **`config.yaml` must not be in OTA packages** — it contains device-specific overrides (bucket, certs, IoT endpoint) that differ per device. Excluded via `--exclude='config.yaml'` in `package-pi.yml` and filtered in `apply_update()`. Default values live in `defaults.yaml` (shipped via OTA); `config_loader.py` deep-merges defaults + overrides at load time.

7. **Okta Terraform state is in S3** — without remote state, each pipeline run creates duplicate apps. State lives at `s3://snout-spotter-{account}/terraform/okta/terraform.tfstate`.

8. **DynamoDB `Scan` with `Limit`** limits items *evaluated*, not items *returned*. Use cursor pagination with `nextPageKey`.

9. **CloudFront caches aggressively** — always invalidate `/*` after deploying new web assets.

10. **Lambda Web Adapter** requires `AWS_LWA_PORT=8080` environment variable.

11. **ECR repos are created in CoreStack** — must exist before any image build. Deploy CoreStack first.

12. **IoT Credentials Provider endpoint cannot be derived from the data endpoint** — they have different prefixes. The credential provider endpoint must be obtained via `aws iot describe-endpoint --endpoint-type iot:CredentialProvider` or from the PiMgmt registration response. It is stored in `config.yaml` under `credentials_provider.endpoint`.

13. **Config validation exists in TWO places** — both `PiUpdateService.cs` (`ConfigurableKeys` dict) and `config_schema.py` (`CONFIGURABLE_KEYS` dict) validate config changes. The API validates before writing to the shadow; the Pi validates again when it receives the delta. Both must be updated when adding new configurable settings.

14. **DynamoDB `FilterExpression` with `Limit`** — `Limit` applies *before* `FilterExpression`. A query with `Limit=30` and a filter may return 0 results even when matching items exist. Use the `by-confirmed-label` GSI for direct queries instead of filtering on the `by-review` GSI.

15. **Training label types** — Three confirmed labels: `my_dog`, `other_dog`, `no_dog`. The `auto_label` field uses `dog`/`no_dog` (binary). `my_dog` and `other_dog` both map to `auto_label=dog`. In single-stage mode, the detector trains on 2 YOLO classes: `my_dog` (class 0) and `other_dog` (class 1). In two-stage mode, the detector trains on 1 class: `dog` (class 0, merged), and the classifier trains on 2 classes: `my_dog`, `other_dog` from cropped patches. Training exports support both detection format (YOLO labels) and classification format (cropped image folders).

16. **Breed data on labels** — Breed is required when confirming dog labels (my_dog/other_dog). My dog defaults to "Labrador Retriever". 120 breeds from ImageNet/Stanford Dogs dataset are supported. Breed is stored in DynamoDB and included in training exports via `labels.csv`.

17. **System Health is split across two pages** — `/health` is a clean landing page with device summary table. `/device/:thingName` is the full device detail page. Sub-pages (config, logs, commands, shadow) link back to the detail page, not `/health`.

18. **Model deployment** — RunInference supports two pipeline modes: **single-stage** uses `models/dog-classifier/best.onnx` (fine-tuned 2-class YOLO), **two-stage** uses `models/dog-detector/best.onnx` (COCO YOLO or single-class fine-tuned) + `models/dog-classifier/best.onnx` (MobileNetV3 classifier). Models are managed via the Models page with Detector/Classifier tabs, versioned uploads, and activate. AutoLabel uses COCO-pretrained YOLOv8 models (`models/yolov8{n,s,m}.onnx`) selected via the `autolabel.model_key` server setting — a warm Lambda reloads the model if the setting changes.

19. **Custom YOLOv8 output format** — A fine-tuned YOLOv8 with 2 classes outputs tensor shape `[1, 6, 8400]` (4 bbox coords + 2 class scores). COCO-pretrained outputs `[1, 84, 8400]` (80 classes). In two-stage mode, the detector uses COCO class 16 (dog) for detection, then the classifier (`[1,3,224,224]` → `[1,2]` softmax) identifies my_dog vs other_dog. The RunInference Lambda dynamically reads the number of classes from the output tensor dimensions.

20. **Keyframe detections are DynamoDB native** — Detection results are stored as a DynamoDB List of Maps (`keyframe_detections`) on the clips table, not as a JSON string. Each entry contains the keyframe key, overall label, and a list of detection boxes with bounding coordinates. The API parses these directly into typed DTOs.

21. **Live streaming stops motion detection** — When `desired.streaming = true` is written to the shadow, `agent.py` stops `snoutspotter-motion` before starting `stream_manager.py` (both need exclusive camera access). Motion is restarted when streaming stops or times out. The stream runs via GStreamer + kvssink → Kinesis Video Streams. Stream name: `snoutspotter-{device}-live`.

22. **GStreamer libcamerasrc does not output I420** — The `libcamerasrc` capsfilter must NOT include `format=I420`. libcamera outputs NV12/BGR natively; constraining to I420 before `videoconvert` causes caps negotiation failure and immediate pipeline exit. The correct pipeline lets `videoconvert` handle the format conversion before `x264enc`.

23. **streaming.resolution config is stored as a string** — `config_schema.py` stores `streaming.resolution` as a string (`"640x480"`) but `defaults.yaml` stores it as a list `[640, 480]`. `stream_manager.py` handles both formats. Do not change one without the other.

24. **RunInference supports two pipeline modes** — Controlled by `inference.pipeline_mode` server setting. **Single-stage** (default): uses the fine-tuned 2-class YOLO model at `models/dog-classifier/best.onnx` (640×640 resize, RGB /255, output `[1, 6, 8400]`). **Two-stage**: uses a COCO YOLO detector (`models/dog-detector/best.onnx`) for dog detection (class 16), then a MobileNetV3-Small classifier (`models/dog-classifier/best.onnx`, 224×224, ImageNet-style normalisation, output `[1, 2]` softmax) on each cropped dog patch. Two-stage was added to address the low recall (~0.14) of single-stage YOLO for fine-grained my_dog vs other_dog classification. Related settings: `inference.classifier_confidence_threshold` (0.5), `inference.classifier_input_size` (224), `inference.crop_padding_ratio` (0.1).

25. **ONNX export must use simplify=False** — Exporting YOLOv8 with `simplify=True` changes the output tensor layout and breaks RunInference's `[1, 4+num_classes, 8400]` parsing. Always export with `opset=12, simplify=False`.

26. **Agent uses MqttManager + event queue** — `agent.py` wraps the CRT MQTT connection in `MqttManager` which tracks connection state and re-subscribes on reconnect. All MQTT callbacks enqueue typed events to a `queue.Queue`; the main loop drains sequentially. No mutable function attributes.

27. **OTA can deliver new systemd services** — `ota.py` has a `SERVICE_MANIFEST` dict mapping service names to scripts. `sync_service_files()` runs after extraction and creates/enables any missing `.service` files in `/etc/systemd/system/` before restarting. New services (e.g. watchdog) are delivered via OTA without re-running `setup-pi.sh`.

28. **Config loader validates schema on startup** — `config_loader.py` checks all required sections, keys, and types after merging `defaults.yaml` + `config.yaml`. Raises `ValueError` with all errors listed if validation fails. Only checks structure — value ranges are validated by `config_schema.py`.

29. **Uploader uses file mtime for S3 key path** — `get_s3_key()` uses the file's `st_mtime` instead of `datetime.now()`. Clips queued during WiFi outages land in the correct `YYYY/MM/DD` folder based on when they were recorded.

30. **Motion detector discards invalid clips** — After FFmpeg remux, `ffprobe` validates the output MP4. If remux or validation fails, both raw H.264 and partial MP4 are deleted. No more unplayable files getting uploaded.

31. **Watchdog reboots only when ALL monitored services fail** — `snoutspotter-watchdog` tracks per-service failure counts independently. Individual services are restarted with 60s cooldown. A device-wide reboot only triggers when all three core services (motion, uploader, agent) fail 5+ consecutive times.

32. **SQS message types live in SnoutSpotter.Contracts** — All SQS queue messages use concrete record types from `src/shared/SnoutSpotter.Contracts/Messages.cs`: `InferenceMessage(ClipId)` for the rerun-inference queue, `BackfillMessage(KeyframeKeys)` for the backfill-boxes queue. Both producers (API) and consumers (Lambdas) reference this shared project. Never use anonymous types or raw JSON for SQS messages.

33. **RunInference Lambda has three input modes** — Handles SQS Records (from rerun queue), EventBridge events (from S3 keyframe upload), and direct invocation (`{ ClipId }`). SQS is checked first in `ParseInput`. All three paths converge on the same clip processing logic.

34. **Training agent requires `shm_size: '8gb'` in docker-compose** — PyTorch DataLoader with multiple workers uses POSIX shared memory (`/dev/shm`). Docker's default is 64MB, which causes `Bus error (core dumped)` when workers try to share tensor data. The docker-compose.yml sets `shm_size: '8gb'`.

35. **Python stdout is block-buffered when piped** — When the training agent spawns `python3` with `RedirectStandardOutput = true`, Python uses an 8KB block buffer (not line-buffered). This delays all training output until the buffer fills or the process exits. Fix: always launch with `python3 -u` (unbuffered). `JobRunner.cs` includes this flag.

36. **ultralytics saves to `runs/detect/{name}` by default** — If `project` passed to `yolo.train()` is a relative path (e.g. `"runs"`), ultralytics may prepend its default `runs/detect` prefix, creating `runs/detect/runs/{name}`. The `train_detector.py` script sets `output_dir` to an absolute path (`dataset_dir / "runs"` when called with `--data`) so the save location is deterministic.

37. **Server settings system** — `SnoutSpotter.Contracts/ServerSettings.cs` defines all settings centrally with `SettingSpec(Label, Default, Type, Min, Max, Description, Options)`. Types: `int`, `float`, `select`. The `select` type uses an `Options` string array for allowed values (e.g. model key). `SettingsReader` (used by Lambdas) caches all values for 5 minutes. Settings are stored in DynamoDB `snout-spotter-settings` (PK: `setting_key`). The API (`SettingsService`) validates on write; the UI renders dropdowns for `select` type and numeric inputs for `int`/`float`.

38. **IoT Shadow null serialization for agent state** — `AgentReportedState.CurrentJobId` and `CurrentJobProgress` must NOT have `[JsonIgnore(WhenWritingNull)]`. IoT Shadow uses merge-patch semantics — omitting a field leaves the old value in the shadow. To clear `currentJobId` after a job finishes, the agent must explicitly serialize `null`. Only truly static fields (like `hostname`) use `WhenWritingNull`.
