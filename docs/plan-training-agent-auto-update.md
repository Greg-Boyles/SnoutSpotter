# Training Agent UI-Driven Auto-Update

## Context

The training agent is a .NET app that runs in Docker on a GPU box, pulling training jobs from SQS. Right now, upgrading it to a new Docker image means SSH'ing to the GPU box — there's no way to roll forward (or roll back) from the dashboard.

Most of the plumbing is already in place:

- **Host updater:** `src/training-agent/updater.sh:44-48` already handles exit code 42 by running `docker compose pull trainer` and restarting.
- **Agent shadow listener:** `src/training-agent/SnoutSpotter.TrainingAgent/Program.cs:107-112,174-196` already reads `agentVersion` from shadow delta, defers on active training jobs, and exits with code 42.
- **Shared state model:** `src/shared/SnoutSpotter.Shared.Training/AgentDesiredState.cs` already has `AgentVersion` + `ForceUpdate` fields.
- **API endpoint:** `POST /api/training/agents/{thingName}/update` exists at `src/api/Controllers/TrainingAgentsController.cs:35-43` and writes the version to the shadow.
- **CI pipeline:** `.github/workflows/build-training-agent-image.yml` pushes `:v{version}`, `:{sha}`, and `:latest` tags to ECR `snout-spotter-training-agent`, and writes a manifest at `s3://.../releases/training-agent/manifest.json`.
- **UI page:** `src/web/src/pages/TrainingAgentDetail.tsx` renders agent status and deferred-update banners.

**Two gaps remain:**

1. **Host updater always pulls `:latest`.** `docker-compose.yml:3` references `${IMAGE_TAG:-latest}`, but nothing ever sets `IMAGE_TAG` — so a shadow request for `v1.2.3` still pulls whatever happens to be tagged `:latest`. Version-pinning doesn't actually work end-to-end.
2. **No way to list versions or trigger an update from the UI.** `TrainingAgentDetail.tsx` has no update button; `api.ts:244` has a `triggerAgentUpdate` client but nothing calls it; there's no service method or endpoint to enumerate available versions.

Outcome: click a version in the dashboard → agent pulls that exact image tag → restarts on the new version. Mirrors the existing Pi OTA picker (`DeviceDetail.tsx:242-264`).

---

## Plan

### 1. Host updater — honor the requested version tag

The agent knows the target version (from the shadow delta); the host updater needs to know it too, before `docker compose pull`. Use a small bind-mounted state file.

**`src/training-agent/docker-compose.yml`:** add a bind mount so the container and host can exchange a small state file:
```yaml
volumes:
  - ./host-state:/app/host-state:rw
  ...existing volumes...
```

**`src/training-agent/SnoutSpotter.TrainingAgent/Program.cs` (~line 186, both exit sites at 188 and 265):** just before each `Environment.Exit(ExitCodeUpdate)`, write the pending version to the bind mount:
```csharp
Directory.CreateDirectory("/app/host-state");
File.WriteAllText("/app/host-state/pending-version", pendingUpdate);
```

**`src/training-agent/updater.sh` (~line 44-48):** on exit code 42, read the file, export `IMAGE_TAG=v{ver}`, pull, then delete the file:
```bash
if [ "$EXIT_CODE" -eq 42 ]; then
    if [ -f host-state/pending-version ]; then
        export IMAGE_TAG="v$(cat host-state/pending-version)"
        echo "Agent requested update to $IMAGE_TAG"
        rm -f host-state/pending-version
    fi
    ecr_login
    docker compose pull trainer
fi
```
Create `host-state/` dir at script start if missing; add to `.gitignore`.

### 2. API — list training-agent releases

Source of truth: ECR image tags (matches the build pipeline and supports rollback to any previously pushed version). The S3 manifest gives us the "latest" flag.

**`src/api/Services/Interfaces/ITrainingService.cs`** — add:
```csharp
Task<List<TrainingAgentRelease>> ListAgentReleasesAsync();
Task<string?> GetLatestAgentVersionAsync();
```

**`src/api/Services/TrainingService.cs`** — inject `IAmazonECR` via constructor, then:
- `GetLatestAgentVersionAsync()`: read `s3://{bucket}/releases/training-agent/manifest.json` (same pattern as `PiUpdateService.GetLatestVersionAsync` at `PiUpdateService.cs:167-192`); cache 5 min; parse `version` field.
- `ListAgentReleasesAsync()`: call `ECR.DescribeImages(repositoryName: "snout-spotter-training-agent")`; filter to tags matching `^v\d+\.\d+\.\d+$`; return `TrainingAgentRelease(version, imagePushedAt, isLatest)` records sorted by push date desc.

Add `TrainingAgentRelease` record alongside existing `TrainerAgentSummary` in whatever models file it lives in.

**`src/api/Controllers/TrainingAgentsController.cs`** — add:
```csharp
[HttpGet("agents/releases")]
public async Task<ActionResult> ListReleases() {
    var releases = await _trainingService.ListAgentReleasesAsync();
    var latest = await _trainingService.GetLatestAgentVersionAsync();
    return Ok(new { releases, latestVersion = latest });
}
```

**`src/api/Program.cs`** — register `IAmazonECR` (`services.AddAWSService<IAmazonECR>()`).

### 3. Infra — grant ECR read to the API Lambda

**`src/infra/Stacks/ApiStack.cs`** — add a policy statement following the pattern at line 94 onwards:
```csharp
apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps {
    Actions = new[] { "ecr:DescribeImages", "ecr:ListImages" },
    Resources = new[] { $"arn:aws:ecr:{Region}:{Account}:repository/snout-spotter-training-agent" }
}));
```
Also grant `s3:GetObject` on `releases/training-agent/manifest.json` if not already covered by existing bucket-wide read perms (check existing S3 statements first).

### 4. Web UI — version dropdown + Update button

**`src/web/src/api.ts`** — add alongside `triggerAgentUpdate` at line 244:
```ts
listTrainingAgentReleases: () =>
  fetchJson<{ releases: { version: string; imagePushedAt: string; isLatest: boolean }[]; latestVersion: string | null }>("/training/agents/releases"),
```

**`src/web/src/pages/TrainingAgentDetail.tsx`** — in the "Agent Info" card (around line 199-243), add a version selector + Deploy button, mirroring `DeviceDetail.tsx:242-264`:
- Fetch releases in the existing `load()` Promise.all.
- `selectedVersion` state, defaulting to the latest release version.
- Render `<select>` of releases (mark `(latest)` and `— current`) and a Deploy button that calls `api.triggerAgentUpdate(thingName, version)` (strip leading `v` for the API payload, same as `DeviceDetail.tsx:103`).
- Disable the button when `selectedVersion === "v" + r.agentVersion` or `r.updateStatus === "updating"`.
- Reuse existing `deferredVersion` / `deferReason` banner already rendered at lines 236-241.

---

## Critical files

- `src/training-agent/updater.sh` — host update loop
- `src/training-agent/docker-compose.yml` — bind mount for pending-version handoff
- `src/training-agent/SnoutSpotter.TrainingAgent/Program.cs` (lines 186-188, 261-265) — write pending version before exit
- `src/api/Services/TrainingService.cs` — new list-releases + latest-version methods
- `src/api/Services/Interfaces/ITrainingService.cs` — interface additions
- `src/api/Controllers/TrainingAgentsController.cs` — new GET releases endpoint
- `src/api/Program.cs` — register `IAmazonECR`
- `src/infra/Stacks/ApiStack.cs` — ECR read perms
- `src/web/src/api.ts` — new client method
- `src/web/src/pages/TrainingAgentDetail.tsx` — version selector UI

## Verification

1. `dotnet build SnoutSpotter.sln` — API + shared libs compile.
2. `dotnet build src/infra/SnoutSpotter.Infra.csproj` — CDK compiles.
3. `npm run build` from `src/web/` — frontend compiles.
4. Deploy via GitHub Actions: `deploy-infra` (for new IAM), `deploy-api`, `deploy-web`.
5. On the GPU box: `git pull` the updated `updater.sh` + `docker-compose.yml`, stop the current agent, restart via `./updater.sh` (with the new bind-mount volume).
6. In the dashboard → Training → agent detail: confirm the version dropdown lists ECR tags and the latest badge matches the manifest.
7. Click an older `vX.Y.Z` → click Deploy → confirm in the shadow (Raw shadow viewer) that `desired.agentVersion` is set → watch agent logs: `host-state/pending-version` appears → container exits 42 → `docker compose pull` uses the pinned tag → container restarts → shadow `reported.agentVersion` matches the selected version.
8. Repeat to roll forward back to latest.
9. Trigger an update while a training job is running → confirm the "deferred" banner appears on the page (existing behavior), and that the update applies after job completion (agent logic at `Program.cs:260-266`).
