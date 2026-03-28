# SnoutSpotter — AI Knowledge Base

## Project Overview

SnoutSpotter is a motion-triggered video capture system running on Raspberry Pi devices, with cloud-based ML inference to detect dogs (and identify a specific dog). Clips are uploaded to AWS, processed through an ingest pipeline, analysed by ML models, and viewable via a React web dashboard.

**Tech stack:** .NET 8 / C#, AWS CDK, Python 3, React + TypeScript + Vite + Tailwind CSS, AWS (Lambda, DynamoDB, S3, IoT Core, CloudFront, API Gateway, ECR).

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
├── src/
│   ├── api/                           # ASP.NET Core 8 API (runs on Lambda via Web Adapter)
│   │   ├── Controllers/
│   │   │   ├── ClipsController.cs     # GET /api/clips, GET /api/clips/{id}
│   │   │   ├── DetectionsController.cs# GET /api/detections
│   │   │   ├── PiController.cs        # GET /api/pi/devices, POST /api/pi/{thingName}/update
│   │   │   └── StatsController.cs     # GET /api/stats, GET /api/stats/health
│   │   ├── Models/ClipModels.cs       # Record types for API responses
│   │   ├── Services/
│   │   │   ├── ClipService.cs         # DynamoDB queries for clips
│   │   │   ├── HealthService.cs       # CloudWatch heartbeat checks
│   │   │   ├── PiUpdateService.cs     # IoT shadow reads/writes, OTA triggers
│   │   │   ├── S3PresignService.cs    # Presigned URL generation
│   │   │   └── S3UrlService.cs        # S3 URL helpers
│   │   ├── Program.cs                 # DI setup, AWS client registration
│   │   └── Dockerfile                 # Lambda Web Adapter based image
│   │
│   ├── infra/                         # AWS CDK infrastructure (C#)
│   │   ├── Program.cs                 # Stack instantiation and wiring
│   │   ├── cdk.json
│   │   └── Stacks/
│   │       ├── CoreStack.cs           # S3, DynamoDB, ECR repos, IAM
│   │       ├── IoTStack.cs            # IoT Thing Group, IoT Policy
│   │       ├── IngestStack.cs         # IngestClip Lambda + S3 event trigger
│   │       ├── InferenceStack.cs      # RunInference Lambda + S3 event trigger
│   │       ├── ApiStack.cs            # API Lambda + HTTP API Gateway
│   │       ├── PiMgmtStack.cs         # Pi Management Lambda + HTTP API Gateway
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
│   │   └── SnoutSpotter.Lambda.PiMgmt/       # Pi device registration API
│   │       ├── Controllers/DevicesController.cs
│   │       ├── Services/DeviceProvisioningService.cs
│   │       ├── Program.cs
│   │       └── Dockerfile
│   │
│   ├── web/                           # React frontend
│   │   ├── src/
│   │   │   ├── App.tsx                # Router + sidebar navigation
│   │   │   ├── api.ts                 # API client (main API + Pi Management API)
│   │   │   ├── types.ts              # TypeScript interfaces
│   │   │   ├── pages/
│   │   │   │   ├── Dashboard.tsx      # Stats overview
│   │   │   │   ├── ClipsBrowser.tsx   # Paginated clip grid
│   │   │   │   ├── ClipDetail.tsx     # Video player + keyframes + detections
│   │   │   │   ├── Detections.tsx     # Detection results list
│   │   │   │   └── SystemHealth.tsx   # Pi device status, add/remove devices, OTA updates
│   │   │   └── components/
│   │   │       └── BoundingBoxOverlay.tsx
│   │   ├── vite.config.ts
│   │   └── package.json
│   │
│   ├── pi/                            # Raspberry Pi Python scripts
│   │   ├── motion_detector.py         # Frame-differencing motion detection + recording
│   │   ├── uploader.py               # S3 multipart upload with retry
│   │   ├── health.py                  # CloudWatch heartbeat + IoT shadow reporting
│   │   ├── ota_agent.py              # OTA updates via IoT shadow desired/reported
│   │   ├── config.yaml               # Pi configuration (thresholds, S3 bucket, IoT settings)
│   │   ├── setup-pi.sh               # Full automated setup: deps, registration, certs, services
│   │   ├── requirements.txt          # Python deps: boto3, opencv, awsiotsdk, pyyaml
│   │   └── version.json              # Current Pi software version
│   │
│   └── ml/                            # ML training scripts
│       ├── train_detector.py          # YOLOv8n fine-tuning for dog detection
│       └── train_classifier.py        # MobileNetV3 binary classifier (my_dog vs not_my_dog)
│
└── .github/workflows/
    ├── deploy.yml                     # Main pipeline: orchestrates all sub-workflows
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
    └── package-pi.yml                 # Package Pi release → S3
```

---

## Architecture & Data Flow

```
[Raspberry Pi + Camera]
    │ motion detected → record clip
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

[React Dashboard] ◄──► [API Lambda + API Gateway]
                              │
                              ├── DynamoDB (clips, detections)
                              ├── S3 (presigned URLs for video/images)
                              ├── CloudWatch (heartbeat metrics)
                              └── IoT Core (device shadows for status/OTA)

[Pi Management Lambda + API Gateway]
    │ Device registration/deregistration
    └── IoT Core (create thing, certificates, policy attachment)
```

### Multi-Pi Device Management

- Devices are registered as IoT Things in the `snoutspotter-pis` thing group
- Each device gets unique X.509 certificates for MQTT connectivity
- Device shadows track: version, hostname, heartbeat timestamp, update status, service states
- OTA updates are triggered by writing `desired.version` to the device shadow
- The Pi's `ota_agent.py` watches for shadow changes and self-updates

---

## AWS Infrastructure (CDK Stacks)

All stacks are defined in `src/infra/Stacks/` and wired in `src/infra/Program.cs`.

**Stack dependency order:**
1. **CoreStack** — foundational resources (no dependencies)
2. **IoTStack** — IoT resources (no dependencies)
3. **IngestStack** — depends on CoreStack (S3 bucket, DynamoDB table, ECR repo)
4. **InferenceStack** — depends on CoreStack (S3 bucket, DynamoDB table, ECR repo)
5. **ApiStack** — depends on CoreStack (S3 bucket, DynamoDB table, ECR repo)
6. **PiMgmtStack** — depends on CoreStack (ECR repo), IoTStack (thing group name, policy name)
7. **WebStack** — standalone (S3 + CloudFront)
8. **MonitoringStack** — depends on CoreStack (S3 bucket)
9. **CiCdStack** — standalone (OIDC role)

**Key resources by stack:**

| Stack | Resources |
|-------|-----------|
| CoreStack | S3 `snout-spotter-{account}`, DynamoDB `snout-spotter-clips`, 4 ECR repos (api, ingest, inference, pi-mgmt), IAM user `snout-spotter-pi` |
| IoTStack | Thing Group `snoutspotter-pis`, IoT Policy `snoutspotter-pi-policy` |
| ApiStack | Docker Lambda `snout-spotter-api`, HTTP API Gateway |
| PiMgmtStack | Docker Lambda `snout-spotter-pi-mgmt`, HTTP API Gateway |
| IngestStack | Docker Lambda triggered by S3 `raw-clips/` events |
| InferenceStack | Docker Lambda triggered by S3 `keyframes/` events |
| WebStack | S3 static site bucket, CloudFront distribution |

**CDK conventions:**
- All stacks use typed `*Props` classes for dependency injection
- ECR repos are created in CoreStack and passed to consuming stacks
- `IMAGE_TAG` environment variable controls which Docker tag to deploy (defaults to `latest`)
- All resources tagged with `Project: SnoutSpotter`

---

## API Endpoints

### Main API (`snout-spotter-api` Lambda)

**Clips:**
- `GET /api/clips?limit=20&nextPageKey=xxx&date=YYYY/MM/DD` — list clips (cursor pagination)
- `GET /api/clips/{id}` — get clip detail with presigned video/keyframe URLs
- `GET /api/clips/{id}/video` — get presigned video URL
- `GET /api/clips/{id}/keyframes` — get presigned keyframe URLs

**Stats:**
- `GET /api/stats` — dashboard stats (total clips, today's clips, detections, Pi online status)
- `GET /api/stats/health` — multi-device health (all Pi devices with shadow state, versions, update status)

**Detections:**
- `GET /api/detections?type=my_dog&limit=50` — list detection results

**Pi Management (OTA):**
- `GET /api/pi/devices` — list all Pi devices with shadow state
- `GET /api/pi/{thingName}/status` — single device status
- `POST /api/pi/{thingName}/update` — trigger OTA update for one device
- `POST /api/pi/update-all` — trigger OTA update for all devices

### Pi Management API (`snout-spotter-pi-mgmt` Lambda — separate API Gateway)

- `GET /api/devices` — list registered device thing names
- `POST /api/devices/register` — register new device (body: `{"name": "garden"}`)
  - Creates IoT Thing `snoutspotter-{name}`, generates certificates, returns credentials
- `DELETE /api/devices/{thingName}` — deregister device (deletes thing, certs, policy attachments)

---

## DynamoDB Schema

**Table:** `snout-spotter-clips`
**Billing:** Pay-per-request

| Attribute | Type | Description |
|-----------|------|-------------|
| `clip_id` (PK) | String | ISO timestamp identifier |
| `s3_key` | String | S3 key for the raw clip |
| `timestamp` | Number | Unix timestamp |
| `duration_s` | Number | Clip duration in seconds |
| `date` | String | `YYYY/MM/DD` format |
| `keyframe_count` | Number | Number of extracted keyframes |
| `keyframe_keys` | String Set | S3 keys for keyframe images |
| `detection_type` | String | `pending`, `my_dog`, `other_dog`, `no_dog` |
| `detection_count` | Number | Number of detections found |
| `detections` | String | JSON string of detection details |
| `labeled` | Boolean | Whether manually labeled |
| `created_at` | String | ISO 8601 timestamp |
| `inference_at` | String | ISO 8601 timestamp of inference run |

**GSIs:**
- `by-date` — PK: `date`, SK: `timestamp` (newest-first queries by date)
- `by-detection` — PK: `detection_type`, SK: `timestamp` (query by detection type)

---

## Frontend

**Stack:** React 18 + TypeScript + Vite + Tailwind CSS
**Hosted:** S3 + CloudFront

**Pages:**
- **Dashboard** (`/`) — stats cards: total clips, today's clips, detections, Pi status
- **Clips Browser** (`/clips`) — paginated grid with thumbnails, cursor-based pagination via `nextPageKey`
- **Clip Detail** (`/clips/:id`) — video player, keyframe gallery, detection results with bounding boxes
- **Detections** (`/detections`) — filterable detection results list
- **System Health** (`/health`) — multi-Pi device status, "Add Pi" dialog, "Remove" button, OTA update triggers

**API client** (`src/web/src/api.ts`):
- Two base URLs: `VITE_API_URL` (main API) and `VITE_PI_MGMT_URL` (Pi Management API)
- `fetchJson`, `postJson`, `deleteJson` helper functions
- All API calls throw on non-2xx responses

**Environment variables:**
- `VITE_API_URL` — main API Gateway URL (e.g. `https://xxx.execute-api.eu-west-1.amazonaws.com/api`)
- `VITE_PI_MGMT_URL` — Pi Management API Gateway URL

---

## Pi Software

**Language:** Python 3 with `picamera2`, `opencv`, `boto3`, `awsiotsdk`
**Config:** `src/pi/config.yaml`

**Services (systemd):**
| Service | Script | Purpose |
|---------|--------|---------|
| `snoutspotter-motion` | `motion_detector.py` | Frame-differencing motion detection, records 1080p H.264 clips |
| `snoutspotter-uploader` | `uploader.py` | Watches for new clips, uploads to S3 with multipart + retry |
| `snoutspotter-health` | `health.py` | Sends CloudWatch heartbeat metrics, updates IoT device shadow |
| `snoutspotter-ota` | `ota_agent.py` | Watches IoT shadow for desired version changes, self-updates |

**Setup:** `setup-pi.sh` automates everything:
1. Installs system and Python dependencies
2. Calls Pi Management API to register the device
3. Saves IoT certificates to `~/.snoutspotter/certs/`
4. Configures AWS credentials and `config.yaml`
5. Installs and starts all systemd services

**IoT shadow reported state:**
```json
{
  "state": {
    "reported": {
      "version": "1.2.0",
      "hostname": "snoutspotter-garden",
      "lastHeartbeat": "2026-03-28T10:00:00Z",
      "updateStatus": "idle",
      "services": {
        "motion": "running",
        "uploader": "running",
        "health": "running",
        "ota": "running"
      }
    }
  }
}
```

---

## CI/CD

**Platform:** GitHub Actions with OIDC-based AWS authentication (no long-lived credentials).

**Main pipeline** (`deploy.yml`) — triggered on push to `main` affecting `src/**`:
1. **Stage 1:** Deploy infrastructure (CDK all stacks)
2. **Stage 2:** Build Docker images in parallel (api, ingest, inference, pi-mgmt) → push to ECR
3. **Stage 3:** Deploy application stacks (api, ingest, inference, pi-mgmt, web) — each via CDK

**Required GitHub secrets:**
- `AWS_ROLE_ARN` — OIDC role ARN for GitHub Actions
- `WEB_BUCKET_NAME` — S3 bucket for frontend assets
- `CLOUDFRONT_DISTRIBUTION_ID` — for cache invalidation after web deploy

**Docker images:** All Lambdas use Docker-based deployment via ECR. Images are tagged with the git SHA.

---

## Development Conventions

- **.NET 8** with top-level statements in `Program.cs`
- **Lambdas** use Docker images — ASP.NET Core ones use Lambda Web Adapter (`AWS_LWA_PORT=8080`)
- **CDK stacks** pass dependencies via typed `*Props` record classes
- **No authentication** on APIs currently (single-user system)
- **CORS** is open (`AllowAnyOrigin`) on both API and Pi Management Lambda
- **Naming:** IoT things are prefixed `snoutspotter-` (e.g. `snoutspotter-garden`)
- **S3 layout:** `raw-clips/YYYY/MM/DD/`, `keyframes/YYYY/MM/DD/`, `models/`, `releases/pi/`

---

## Known Gotchas

1. **`AmazonIotDataClient` cannot use `RegionEndpoint`** — it requires a `ServiceURL` obtained via `iot:DescribeEndpoint` (Data-ATS endpoint type). This is called once at DI registration time in the API Lambda.

2. **`iot:DescribeEndpoint` needs `Resource: "*"`** — it's a global IAM action and cannot be scoped to a specific resource ARN.

3. **DynamoDB `Scan` with `Limit`** limits items *evaluated*, not items *returned*. Always use cursor-based pagination with `nextPageKey` / `ExclusiveStartKey`.

4. **CloudFront caches aggressively** — always invalidate `/*` after deploying new web assets.

5. **Pi setup script requires Pi Management API to be deployed first** — the script calls the registration endpoint during setup.

6. **Lambda Web Adapter** requires `AWS_LWA_PORT` environment variable set to the port the ASP.NET Core app listens on (8080).

7. **IoT Policy uses `${iot:Connection.Thing.ThingName}` variable** — this scopes MQTT permissions to the connecting device's own thing name. Don't hardcode thing names in the policy.

8. **ECR repos are created in CoreStack** — they must exist before any image build or stack deployment that references them. Always deploy CoreStack first.
