# SnoutSpotter

Motion-triggered video capture on Raspberry Pi, with cloud-based ML inference to detect and identify dogs. A two-stage pipeline — YOLO detector finds dogs, MobileNetV3 classifier identifies which pet — feeds into a React dashboard for reviewing clips, labeling training data, managing pet profiles, and training custom models.

## Architecture

```
Pi (Camera + Motion Detection)
    │ motion detected → record clip → upload to S3
    ▼
S3 raw-clips/{device}/YYYY/MM/DD/
    │ EventBridge
    ▼
Lambda: IngestClip ──► DynamoDB (clip metadata)
    │                  S3 keyframes/
    │                      │ EventBridge
    │                      ▼
    │               Lambda: RunInference
    │                 ├─ YOLO detector (dog detection)
    │                 └─ MobileNetV3 classifier (pet identification)
    │                         ▼
    │                  DynamoDB (per-keyframe detections)
    │
React Dashboard ◄──► ASP.NET Core API (JWT auth via Okta)
    │                      │
    │                      ├── DynamoDB (clips, labels, pets, models, training jobs)
    │                      ├── S3 (presigned URLs, model storage)
    │                      ├── IoT Core (device shadows, OTA, commands)
    │                      └── SQS (training job queue, rerun inference)
    │
    └── Training Agent (GPU box, Docker)
            ├── Polls SQS for training jobs
            ├── Runs YOLOv8 / MobileNetV3 training
            └── Uploads models to S3

Pi ◄──MQTT──► IoT Core
    ├── Shadow reporting (heartbeat, camera, system health)
    ├── OTA updates (version-pinned, rollback support)
    ├── Remote config (24 settings, validated both sides)
    ├── Device commands (reboot, restart services, clear clips)
    ├── Log shipping → CloudWatch Logs
    └── Live streaming → Kinesis Video Streams
```

## Components

| Component | Tech | Location |
|-----------|------|----------|
| Infrastructure | AWS CDK (C#) | `src/infra/` |
| API | ASP.NET Core 8 | `src/api/` |
| Web Dashboard | React + TypeScript + Vite + Tailwind | `src/web/` |
| Ingest Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.IngestClip/` |
| Inference Lambda | .NET 8 + ONNX Runtime | `src/lambdas/SnoutSpotter.Lambda.RunInference/` |
| AutoLabel Lambda | .NET 8 + ONNX Runtime | `src/lambdas/SnoutSpotter.Lambda.AutoLabel/` |
| Export Dataset Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.ExportDataset/` |
| Stats Refresh Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.StatsRefresh/` |
| Training Progress Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.UpdateTrainingProgress/` |
| Command Ack Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.CommandAck/` |
| Log Ingestion Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.LogIngestion/` |
| Pi Management Lambda | ASP.NET Core 8 | `src/lambdas/SnoutSpotter.Lambda.PiMgmt/` |
| Pi Scripts | Python 3 | `src/pi/` |
| ML Training Scripts | Python 3 (YOLOv8 + PyTorch) | `src/ml/` |
| Training Agent | .NET 8 (Docker, GPU) | `src/training-agent/` |
| Okta IaC | Terraform | `terraform/okta/` |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)
- [AWS CDK CLI](https://docs.aws.amazon.com/cdk/v2/guide/cli.html) (`npm i -g aws-cdk`)
- [Terraform 1.5+](https://www.terraform.io/) (for Okta provisioning)
- AWS account with credentials configured
- Okta developer account
- Raspberry Pi Zero 2 W with Pi Camera Module 3

## Getting Started

### 1. Deploy infrastructure

```bash
cd src/infra
cdk bootstrap   # first time only
cdk deploy --all
```

### 2. Provision Okta

```bash
cd terraform/okta
terraform init
terraform apply
```

This creates the SnoutSpotter SPA app, `SnoutSpotter Users` group, access policy, and sign-on policy. Add users to the `SnoutSpotter Users` group in the Okta admin console to grant access.

### 3. Set up GitHub secrets/vars

| Name | Type | Value |
|------|------|-------|
| `AWS_ROLE_ARN` | Secret | OIDC role ARN (CDK output from CiCdStack) |
| `WEB_BUCKET_NAME` | Secret | S3 bucket for frontend |
| `CLOUDFRONT_DISTRIBUTION_ID` | Secret | CloudFront distribution ID |
| `API_URL` | Secret | Main API Gateway URL |
| `OKTA_API_TOKEN` | Secret | Okta API token for Terraform |
| `DATA_BUCKET_NAME` | Secret | Data S3 bucket name |
| `OKTA_ISSUER` | Var | `https://{org}.okta.com/oauth2/default` |
| `SNOUTSPOTTER_OKTA_CLIENT_ID` | Var | Okta app client ID (`terraform output okta_client_id`) |

### 4. Build and deploy

Push to `main` — the pipeline auto-detects which components changed and deploys only those.

Manual full deploy: trigger `Deploy` workflow via `workflow_dispatch`.

### 5. Run locally

```bash
# API
cd src/api && dotnet run

# Frontend
cd src/web
npm install
npm run dev
```

The frontend needs these environment variables (set in a `.env` file):

```
VITE_API_URL=http://localhost:5000/api
VITE_PI_MGMT_URL=http://localhost:5001
VITE_OKTA_ISSUER=https://{org}.okta.com/oauth2/default
VITE_OKTA_CLIENT_ID={okta_client_id}
```

### 6. Set up a Pi

```bash
# On the Pi — run the automated setup script
cd src/pi
bash setup-pi.sh
```

The script registers the device via the Pi Management API, downloads IoT certificates, configures `config.yaml`, and installs all systemd services (motion, uploader, agent, watchdog).

### 7. Set up the training agent

```bash
# On a machine with an NVIDIA GPU
cd src/training-agent

# Create .env
cat > .env <<EOF
ECR_REGISTRY={account-id}.dkr.ecr.eu-west-1.amazonaws.com
AGENT_NAME=my-gpu-box
EOF

# Pull and start
aws ecr get-login-password --region eu-west-1 | docker login --username AWS --password-stdin $ECR_REGISTRY
docker compose pull && ./updater.sh
```

On first start the agent self-registers with IoT Core. Subsequent starts skip registration. The updater script handles container lifecycle — exit code 42 triggers an image pull and restart.

### 8. Train models

Via the dashboard (recommended): Training page → select dataset export → configure hyperparameters → submit. The training agent picks up the job automatically.

Or manually:
```bash
cd src/ml
pip install -r requirements.txt
python train_detector.py --data /path/to/dataset --epochs 50
python train_classifier.py --data /path/to/dataset --epochs 30
```

## CI/CD

All pipelines use GitHub Actions with OIDC-based AWS auth (no long-lived credentials).

The main pipeline (`deploy.yml`) is path-filtered — only changed components are built and deployed:

| Path changed | What deploys |
|-------------|--------------|
| `src/infra/**` | CDK stacks + all downstream |
| `src/api/**` | API image → ECR → Lambda update |
| `src/web/**` | Build → S3 → CloudFront invalidation |
| `src/lambdas/*Ingest*/**` | Ingest image → ECR → Lambda update |
| `src/lambdas/*RunInference*/**` | Inference image → ECR → Lambda update |
| `src/lambdas/*AutoLabel*/**` | AutoLabel image → ECR → Lambda update |
| `src/lambdas/*ExportDataset*/**` | ExportDataset image → ECR → Lambda update |
| `src/lambdas/*PiMgmt*/**` | PiMgmt image → ECR → Lambda update |
| `src/lambdas/*StatsRefresh*/**` | StatsRefresh image → ECR → Lambda update |
| `src/lambdas/*UpdateTrainingProgress*/**` | UpdateTrainingProgress image → ECR → Lambda update |
| `src/lambdas/*CommandAck*/**` | CommandAck image → ECR → Lambda update |
| `src/lambdas/*LogIngestion*/**` | LogIngestion image → ECR → Lambda update |

Standalone workflows:

| Workflow | Trigger | What it deploys |
|----------|---------|-----------------|
| `deploy-okta.yml` | Push to `main` (`terraform/okta/**`) | Okta resources via Terraform |
| `package-pi.yml` | Push to `main` (`src/pi/**`) | Pi release tarball → S3, auto-bumps version |
| `package-ml-training.yml` | Manual dispatch | ML training scripts → S3 |
| `build-training-agent-image.yml` | Manual dispatch | Training agent Docker image → ECR |
| `build-kvssink.yml` | Manual dispatch | KVS GStreamer plugin .deb for ARM64 → S3 |

## License

Private — all rights reserved.
