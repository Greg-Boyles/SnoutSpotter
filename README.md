# SnoutSpotter

Motion-triggered video capture on a Raspberry Pi Zero 2 W, with cloud-based ML inference to detect dogs — and identify a specific dog. Protected by Okta authentication.

## Architecture

```
Pi Zero 2 W (Camera)
    │ motion detected → record clip
    ▼
S3 raw-clips/YYYY/MM/DD/
    │ S3 event
    ▼
Lambda: IngestClip ──► DynamoDB (clip metadata)
    │                  S3 keyframes/
    │                      │ S3 event
    │                      ▼
    │               Lambda: RunInference ──► DynamoDB (detections)
    │
React Dashboard ◄──► ASP.NET Core API (JWT auth via Okta)
                           │
                    IoT Core (device shadows, OTA)

Pi ◄──► IoT Core MQTT (snoutspotter-agent: heartbeat + OTA)
```

**Components:**

| Component | Tech | Location |
|-----------|------|----------|
| Infrastructure | AWS CDK (C#) | `src/infra/` |
| Ingest Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.IngestClip/` |
| Inference Lambda | .NET 8 + ONNX Runtime | `src/lambdas/SnoutSpotter.Lambda.RunInference/` |
| Pi Management Lambda | ASP.NET Core 8 | `src/lambdas/SnoutSpotter.Lambda.PiMgmt/` |
| API | ASP.NET Core 8 | `src/api/` |
| Web Dashboard | React + TypeScript + Tailwind | `src/web/` |
| Pi Scripts | Python 3 | `src/pi/` |
| ML Training | Python 3 (YOLOv8 + PyTorch) | `src/ml/` |
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
cp .env.example .env  # set VITE_API_URL, VITE_OKTA_ISSUER, VITE_OKTA_CLIENT_ID
npm run dev
```

### 6. Set up a Pi

```bash
# On the Pi Zero 2 W — run the automated setup script
cd src/pi
bash setup-pi.sh
```

The script registers the device via the Pi Management API, downloads IoT certificates, configures `config.yaml`, and installs all systemd services.

### 7. Train ML models

```bash
cd src/ml
pip install -r requirements.txt
python train_detector.py --data /path/to/dog-dataset --epochs 50
python train_classifier.py --data /path/to/classifier-data --epochs 30
```

## CI/CD Pipelines

| Workflow | Trigger | What it deploys |
|----------|---------|-----------------|
| `deploy.yml` | Push to `main` (path-filtered) | Infra + API + web + lambdas (only changed) |
| `deploy-okta.yml` | Push to `main` (`terraform/okta/**`) | Okta resources via Terraform |
| `package-pi.yml` | Push to `main` (`src/pi/**`) | Pi release tarball → S3, bumps version |

## License

Private — all rights reserved.
