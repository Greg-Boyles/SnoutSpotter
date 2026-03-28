# SnoutSpotter — Claude Project Rules

Read `AGENTS.md` for full project context (architecture, file layout, data flow, API endpoints, DynamoDB schema).

## Key Rules

- Region is `eu-west-1`. Never hardcode AWS account IDs — use CDK `Account`/`Region` tokens.
- .NET 8 with top-level statements. Use record types for API models.
- CDK stacks pass dependencies via typed `*Props` classes. ECR repos live in CoreStack only.
- Lambdas are Docker-based via ECR. ASP.NET Core ones use Lambda Web Adapter (`AWS_LWA_PORT=8080`).
- `AmazonIotDataClient` must use `ServiceURL` (not `RegionEndpoint`) — obtain via `iot:DescribeEndpoint`.
- IoT control plane IAM actions (e.g. `iot:DescribeEndpoint`) need `Resource: "*"`.
- Frontend is React + TypeScript + Vite + Tailwind in `src/web/`. API client in `api.ts` uses two base URLs.
- Pagination is cursor-based (`nextPageKey`), not offset-based. DynamoDB Scan order is not guaranteed — sort client-side.
- Pi scripts are Python in `src/pi/`. IoT thing names prefixed `snoutspotter-`.
- CI/CD is GitHub Actions with OIDC. Main pipeline: `deploy.yml`. Always invalidate CloudFront after web deploys.

## Verification Commands

- C# changes: `dotnet build SnoutSpotter.sln`
- Frontend changes: `npm run build` (from `src/web/`)
- CDK changes: `dotnet build src/infra/SnoutSpotter.Infra.csproj`

## Naming Conventions

- S3 bucket: `snout-spotter-{account-id}`
- DynamoDB table: `snout-spotter-clips`
- Lambda functions: `snout-spotter-api`, `snout-spotter-pi-mgmt`
- ECR repos: `snout-spotter-api`, `snout-spotter-ingest`, `snout-spotter-inference`, `snout-spotter-pi-mgmt`
- IoT Thing Group: `snoutspotter-pis`
- IoT Things: `snoutspotter-{device-name}`
