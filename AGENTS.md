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
│   └── PLAN.md                        # Original system design document
├── terraform/
│   └── okta/                          # Terraform for Okta provisioning
│       ├── main.tf                    # App, group, access policy, sign-on policy
│       ├── variables.tf
│       └── outputs.tf                 # okta_client_id, okta_issuer
├── src/
│   ├── api/                           # ASP.NET Core 8 API (runs on Lambda via Web Adapter)
│   │   ├── Controllers/
│   │   │   ├── ClipsController.cs     # GET /api/clips, GET /api/clips/{id}
│   │   │   ├── DetectionsController.cs# GET /api/detections
│   │   │   ├── LabelsController.cs    # ML labeling: auto-label, review, bulk confirm, upload, export
│   │   │   ├── PiController.cs        # GET /api/pi/devices, POST /api/pi/{thingName}/update
│   │   │   └── StatsController.cs     # GET /api/stats, GET /api/stats/health
│   │   ├── Models/ClipModels.cs       # Record types for API responses
│   │   ├── Services/
│   │   │   ├── ClipService.cs         # DynamoDB queries for clips
│   │   │   ├── ExportService.cs       # Training dataset export trigger and management
│   │   │   ├── HealthService.cs       # CloudWatch heartbeat checks
│   │   │   ├── LabelService.cs        # Label CRUD, breed, stats, upload, backfill
│   │   │   ├── PiUpdateService.cs     # IoT shadow reads/writes, OTA triggers, config validation
│   │   │   ├── S3PresignService.cs    # Presigned URL generation
│   │   │   └── S3UrlService.cs        # S3 URL helpers
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
│   ├── lambdas/
│   │   ├── SnoutSpotter.Lambda.IngestClip/    # Triggered by S3 raw-clips upload
│   │   │   ├── Function.cs                    # Extracts keyframes via FFmpeg, writes DynamoDB
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.RunInference/  # Triggered by S3 keyframes upload
│   │   │   ├── Function.cs                    # ONNX inference (YOLOv8 + MobileNetV3)
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.AutoLabel/     # YOLOv8 dog detection on keyframes
│   │   │   ├── Function.cs                    # ONNX inference, writes labels to DynamoDB
│   │   │   └── Dockerfile
│   │   ├── SnoutSpotter.Lambda.ExportDataset/ # Training dataset packaging
│   │   │   ├── Function.cs                    # Queries labels, downloads images, creates zip + labels.csv
│   │   │   └── Dockerfile
│   │   └── SnoutSpotter.Lambda.PiMgmt/        # Pi device registration API (no Okta auth)
│   │       ├── Controllers/DevicesController.cs
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
│   │   │   │   ├── TrainingExports.tsx# Training dataset export list and download
│   │   │   │   ├── SystemHealth.tsx   # Landing page: API health + device summary table
│   │   │   │   ├── DeviceDetail.tsx   # Per-device detail: status, services, camera, system, actions
│   │   │   │   ├── DeviceConfig.tsx   # Per-device remote config editor (24 settings)
│   │   │   │   ├── DeviceLogs.tsx     # Per-device log viewer with filters
│   │   │   │   ├── DeviceShadow.tsx   # Raw IoT device shadow JSON viewer
│   │   │   │   └── CommandHistory.tsx # Per-device command history
│   │   │   └── components/
│   │   │       ├── BoundingBoxOverlay.tsx
│   │   │       └── health/            # Shared: StatusBadge, UsageBar, AddDeviceDialog, formatUptime
│   │   ├── vite.config.ts
│   │   └── package.json
│   │
│   └── pi/                            # Raspberry Pi Python scripts
│       ├── agent.py                   # Thin orchestrator: MQTT connection, shadow delta dispatch, main loop
│       ├── health.py                  # System health gathering: CPU, memory, disk, camera, upload stats
│       ├── shadow.py                  # IoT shadow building and reporting
│       ├── ota.py                     # OTA update: download, extract, deps, rollback, service restart
│       ├── remote_config.py           # Remote config validation and application from shadow delta
│       ├── log_shipping.py            # Journald log collection and MQTT publish
│       ├── iot_credential_provider.py # IoT Credentials Provider: temp STS creds via X.509 certs
│       ├── motion_detector.py         # Frame-differencing motion detection + recording + status file
│       ├── uploader.py                # S3 multipart upload with retry + status file
│       ├── config_loader.py           # Shared config loading: deep-merges defaults.yaml + config.yaml
│       ├── config_schema.py           # Allow-list and validation for remotely configurable settings
│       ├── defaults.yaml              # Default config values (shipped via OTA)
│       ├── config.yaml                # Device-specific overrides (NOT included in OTA packages)
│       ├── system-deps.txt            # System apt packages (shipped via OTA, installed during updates)
│       ├── custom-debs.txt            # Custom .deb packages from S3 (installed during OTA)
│       ├── setup-pi.sh                # Full automated setup: deps, registration, certs, credential provider, services
│       ├── requirements.txt           # Python deps: boto3, opencv, awsiotsdk, pyyaml
│       └── version.json               # Current Pi software version (written by OTA agent)
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
              │ YOLOv8 dog detection → MobileNetV3 classification
              ▼
         [DynamoDB: detection results updated on clip record]

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
5. **ApiStack** — depends on CoreStack, reads CDK context for `oktaIssuer`/`allowedOrigin`
6. **PiMgmtStack** — depends on CoreStack, IoTStack
7. **WebStack** — standalone (S3 + CloudFront)
8. **MonitoringStack** — depends on CoreStack
9. **CiCdStack** — standalone (OIDC role, S3 permissions include `terraform/*`)

**Key resources by stack:**

| Stack | Resources |
|-------|-----------|
| CoreStack | S3 `snout-spotter-{account}`, DynamoDB `snout-spotter-clips` + `snout-spotter-labels` + `snout-spotter-exports`, 7 ECR repos |
| IoTStack | Thing Group `snoutspotter-pis`, IoT Policy `snoutspotter-pi-policy`, IAM Role `snoutspotter-pi-credentials`, Role Alias `snoutspotter-pi-role-alias`, CloudWatch Log Group `/snoutspotter/pi-logs`, IoT Topic Rule `snoutspotter_pi_logs` |
| ApiStack | Docker Lambda `snout-spotter-api`, HTTP API Gateway, Okta JWT env vars |
| PiMgmtStack | Docker Lambda `snout-spotter-pi-mgmt`, HTTP API Gateway |
| IngestStack | Docker Lambda triggered by S3 `raw-clips/` events |
| InferenceStack | Docker Lambda triggered by S3 `keyframes/` events |
| WebStack | S3 static site bucket, CloudFront distribution |
| AutoLabelStack | Docker Lambda triggered by auto-label API call |
| ExportDatasetStack | Docker Lambda for training dataset packaging |
| CiCdStack | GitHub Actions OIDC role with S3, ECR, Lambda, CloudFront, CFn permissions |

---

## API Endpoints

All main API endpoints require a valid Okta JWT Bearer token.

### Main API (`snout-spotter-api` Lambda)

**Clips:**
- `GET /api/clips?limit=20&nextPageKey=xxx&date=YYYY/MM/DD` — list clips (cursor pagination)
- `GET /api/clips/{id}` — get clip detail with presigned video/keyframe URLs

**Stats:**
- `GET /api/stats` — dashboard stats (total clips, today's clips, detections, Pi online status)
- `GET /api/stats/health` — multi-device health (all Pi shadows with camera, system, upload stats)

**Detections:**
- `GET /api/detections?type=my_dog&limit=50` — list detection results

**ML Labels & Training:**
- `POST /api/ml/auto-label` — trigger auto-labeling for keyframes
- `GET /api/ml/labels/stats` — label counts + breed distribution
- `GET /api/ml/labels?reviewed=false&confirmedLabel=my_dog&breed=Labrador+Retriever` — paginated labels (filterable)
- `PUT /api/ml/labels/{keyframeKey}` — update label (`confirmedLabel`, optional `breed`)
- `POST /api/ml/labels/bulk-confirm` — bulk confirm labels with breed
- `POST /api/ml/labels/upload?label=other_dog&breed=Chihuahua` — upload training images
- `POST /api/ml/labels/backfill-breed` — set breed on existing labels missing it
- `POST /api/ml/export` — trigger training dataset export
- `GET /api/ml/exports` — list exports
- `GET /api/ml/exports/{exportId}/download` — presigned download URL
- `DELETE /api/ml/exports/{exportId}` — delete export

**Pi Management (OTA + Config + Commands):**
- `GET /api/pi/devices` — list all Pi devices with full shadow state
- `GET /api/pi/{thingName}/status` — single device status
- `GET /api/pi/{thingName}/shadow` — raw IoT device shadow JSON
- `GET /api/pi/{thingName}/config` — current configurable settings
- `POST /api/pi/{thingName}/config` — update device config (validated API-side + Pi-side)
- `POST /api/pi/{thingName}/update` — trigger OTA update for one device
- `POST /api/pi/update-all` — trigger OTA update for all devices
- `POST /api/pi/{thingName}/command` — send command (reboot, restart-*, clear-clips, clear-backups)
- `GET /api/pi/{thingName}/command/{commandId}` — poll command result
- `GET /api/pi/{thingName}/commands` — command history
- `GET /api/pi/{thingName}/logs?minutes=60&level=INFO&service=motion&limit=200` — query device logs from CloudWatch

### Pi Management API (`snout-spotter-pi-mgmt` Lambda — no auth)

- `GET /api/devices` — list registered device thing names
- `POST /api/devices/register` — register new device (`{"name": "garden"}`) → returns certs, IoT endpoint, credential provider endpoint
- `DELETE /api/devices/{thingName}` — deregister device

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
| `detection_type` | String | `pending`, `my_dog`, `other_dog`, `no_dog` |
| `detection_count` | Number | Number of detections found |
| `detections` | String | JSON string of detection details |
| `created_at` | String | ISO 8601 timestamp |

**GSIs:**
- `all-by-time` — PK: fixed value, SK: `timestamp` (server-side ordering across all clips)
- `by-date` — PK: `date`, SK: `timestamp`
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
| `s3_key` | String | S3 key for the exported zip |
| `total_images` | Number | Total image count |
| `my_dog_count` | Number | Count of my_dog labels |
| `not_my_dog_count` | Number | Count of other_dog + no_dog labels |
| `train_count` | Number | Training split count |
| `val_count` | Number | Validation split count |
| `size_mb` | Number | Zip file size |

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
| `snoutspotter-uploader` | `uploader.py` | Uploads clips to S3, writes `~/.snoutspotter/uploader-status.json` |
| `snoutspotter-agent` | `agent.py` | Single MQTT connection: shadow reporting (health + camera + system metrics) + OTA updates |

**Status files** (`~/.snoutspotter/`):
- `motion-status.json` — `cameraOk`, `lastMotionAt`, `lastRecordingStartedAt`, `recordingsToday`
- `uploader-status.json` — `lastUploadAt`, `uploadsToday`, `failedToday`

**IoT shadow reported state:**
```json
{
  "state": {
    "reported": {
      "version": "1.0.2",
      "hostname": "snoutspotter-01",
      "lastHeartbeat": "2026-03-29T10:00:00Z",
      "updateStatus": "idle",
      "services": {"motion": "active", "uploader": "active", "agent": "active"},
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
4. Backs up current version to `~/.snoutspotter/backups/{version}/`
5. Extracts package (skipping `config.yaml` to preserve device-specific overrides; `defaults.yaml` is updated)
6. Installs system apt packages if `system-deps.txt` has changed (compares against backup)
7. Downloads and installs custom `.deb` packages from S3 if `custom-debs.txt` has changed
8. Installs Python dependencies from `requirements.txt`
9. Restarts services, waits 30s, checks health
10. Reports `updateStatus: success` or rolls back on failure

**Pi version bumping:** `package-pi.yml` reads the current version from the S3 manifest and increments the patch number — guarantees a unique version every run.

### Remote Config (24 settings)

Settings are validated in two places: API-side (`PiUpdateService.ConfigurableKeys`) and Pi-side (`config_schema.CONFIGURABLE_KEYS`). Both must match.

| Section | Settings |
|---------|----------|
| Motion | `threshold`, `blur_kernel`, `min_area` |
| Camera | `detection_fps`, `preview_resolution`, `record_resolution`, `record_fps` |
| Recording | `max_clip_length`, `pre_buffer`, `pre_buffer_enabled`, `post_motion_buffer` |
| Upload | `max_retries`, `retry_delay`, `delete_after_upload` |
| Health | `interval_seconds` |
| Log Shipping | `enabled`, `batch_interval_seconds`, `max_lines_per_batch`, `min_level` |
| Streaming | `timeout_seconds`, `resolution`, `framerate`, `bitrate` |
| Credentials | `credentials_provider.endpoint` |

Config changes are written to the IoT shadow desired state, picked up by the Pi agent via delta, validated, applied to `config.yaml`, and signalled to affected services via touch-files.

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
- **S3 layout:** `raw-clips/YYYY/MM/DD/`, `keyframes/YYYY/MM/DD/`, `training-uploads/`, `training-exports/`, `models/`, `releases/pi/`, `terraform/`

---

## Known Gotchas

1. **`AmazonIotDataClient` requires `ServiceURL`**, not `RegionEndpoint` — obtained via `iot:DescribeEndpoint` at startup.

2. **`iot:DescribeEndpoint` needs `Resource: "*"`** — global IAM action, cannot be scoped.

3. **All IoT IAM actions use `iot:` prefix** — not `iot-data:` or `iotdata:`. This applies to data plane actions like `GetThingShadow` too.

4. **OTA delta notifications are not replayed** — the agent must call `shadow/get` on startup to catch any delta that arrived while it was offline.

5. **Only one MQTT connection per client ID** — the old separate `health.py` + `ota_agent.py` conflicted. The merged `agent.py` uses a single connection.

6. **`config.yaml` must not be in OTA packages** — it contains device-specific overrides (bucket, certs, IoT endpoint) that differ per device. Excluded via `--exclude='config.yaml'` in `package-pi.yml` and filtered in `apply_update()`. Default values live in `defaults.yaml` (shipped via OTA); `config_loader.py` deep-merges defaults + overrides at load time.

7. **Okta Terraform state is in S3** — without remote state, each pipeline run creates duplicate apps. State lives at `s3://snout-spotter-{account}/terraform/okta/terraform.tfstate`.

8. **DynamoDB `Scan` with `Limit`** limits items *evaluated*, not items *returned*. Use cursor pagination with `nextPageKey`.

9. **CloudFront caches aggressively** — always invalidate `/*` after deploying new web assets.

10. **Lambda Web Adapter** requires `AWS_LWA_PORT=8080` environment variable.

11. **ECR repos are created in CoreStack** — must exist before any image build. Deploy CoreStack first.

12. **IoT Credentials Provider endpoint cannot be derived from the data endpoint** — they have different prefixes. The credential provider endpoint must be obtained via `aws iot describe-endpoint --endpoint-type iot:CredentialProvider` or from the PiMgmt registration response. It is stored in `config.yaml` under `credentials_provider.endpoint`.

13. **Config validation exists in TWO places** — both `PiUpdateService.cs` (`ConfigurableKeys` dict) and `config_schema.py` (`CONFIGURABLE_KEYS` dict) validate config changes. The API validates before writing to the shadow; the Pi validates again when it receives the delta. Both must be updated when adding new configurable settings.

14. **DynamoDB `FilterExpression` with `Limit`** — `Limit` applies *before* `FilterExpression`. A query with `Limit=30` and a filter may return 0 results even when matching items exist. Use the `by-confirmed-label` GSI for direct queries instead of filtering on the `by-review` GSI.

15. **Training label types** — Three confirmed labels: `my_dog`, `other_dog`, `no_dog`. The `auto_label` field uses `dog`/`no_dog` (binary). `my_dog` and `other_dog` both map to `auto_label=dog`. The classifier trains on `my_dog` vs `not_my_dog` (other_dog + no_dog combined).

16. **Breed data on labels** — Breed is required when confirming dog labels (my_dog/other_dog). My dog defaults to "Labrador Retriever". 120 breeds from ImageNet/Stanford Dogs dataset are supported. Breed is stored in DynamoDB and included in training exports via `labels.csv`.

17. **System Health is split across two pages** — `/health` is a clean landing page with device summary table. `/device/:thingName` is the full device detail page. Sub-pages (config, logs, commands, shadow) link back to the detail page, not `/health`.
