# SnoutSpotter

Motion-triggered video capture on a Raspberry Pi Zero 2 W, with cloud-based ML inference to detect dogs — and ultimately identify a specific dog.

## Architecture

```
Pi Zero 2 W ──► S3 (clips/) ──► IngestClip Lambda ──► DynamoDB
                                      │
                                 S3 (keyframes/)
                                      │
                                 RunInference Lambda ──► DynamoDB (detections)
                                      │
                              ASP.NET Core API ◄── React Dashboard
```

**Components:**

| Component | Tech | Location |
|-----------|------|----------|
| Infrastructure | AWS CDK (C#) | `src/infra/` |
| Ingest Lambda | .NET 8 | `src/lambdas/SnoutSpotter.Lambda.IngestClip/` |
| Inference Lambda | .NET 8 + ONNX Runtime | `src/lambdas/SnoutSpotter.Lambda.RunInference/` |
| API | ASP.NET Core 8 | `src/api/` |
| Web Dashboard | React + TypeScript + Tailwind | `src/web/` |
| Pi Scripts | Python 3 | `src/pi/` |
| ML Training | Python 3 (YOLOv8 + PyTorch) | `src/ml/` |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)
- [AWS CDK CLI](https://docs.aws.amazon.com/cdk/v2/guide/cli.html) (`npm i -g aws-cdk`)
- [Python 3.11+](https://www.python.org/) (for ML scripts)
- AWS account with credentials configured
- Raspberry Pi Zero 2 W with Pi Camera Module 3

## Getting Started

### 1. Build the .NET solution

```bash
dotnet build SnoutSpotter.sln
```

### 2. Deploy infrastructure

```bash
cd src/infra
cdk bootstrap   # first time only
cdk deploy --all
```

### 3. Run the web dashboard locally

```bash
cd src/web
npm install
npm run dev
```

The dashboard runs at `http://localhost:5173` and proxies API calls to `http://localhost:5000`.

### 4. Run the API locally

```bash
cd src/api
dotnet run
```

### 5. Set up the Pi

```bash
# On the Pi Zero 2 W
cd src/pi
pip install -r requirements.txt
# Edit config.yaml with your S3 bucket and AWS credentials
sudo cp systemd/*.service /etc/systemd/system/
sudo systemctl enable snoutspotter-motion snoutspotter-uploader snoutspotter-health
sudo systemctl start snoutspotter-motion snoutspotter-uploader snoutspotter-health
```

### 6. Train ML models

```bash
cd src/ml
pip install -r requirements.txt

# Train YOLOv8 dog detector
python train_detector.py --data /path/to/dog-dataset --epochs 50

# Train MobileNetV3 classifier (my_dog vs not_my_dog)
python train_classifier.py --data /path/to/classifier-data --epochs 30
```

## CI/CD

GitHub Actions workflows deploy each component independently:

- **deploy-infra** — CDK stacks on changes to `src/infra/`
- **deploy-api** — Docker build → ECR → ECS on changes to `src/api/`
- **deploy-lambdas** — Lambda deployment on changes to `src/lambdas/`
- **deploy-web** — Build → S3 + CloudFront on changes to `src/web/`
- **deploy-ml** — Package → S3 on changes to `src/ml/`

### Required GitHub Secrets

- `AWS_ROLE_ARN` — OIDC role for GitHub Actions
- `LAMBDA_ROLE_ARN` — IAM role for Lambda execution
- `API_URL` — API endpoint URL
- `WEB_BUCKET_NAME` — S3 bucket for the web frontend
- `CLOUDFRONT_DISTRIBUTION_ID` — CloudFront distribution ID
- `ML_BUCKET_NAME` — S3 bucket for ML artifacts

## License

Private — all rights reserved.
