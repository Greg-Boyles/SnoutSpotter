# Plan: Multi-Household Isolation

## Context

This plan extends SnoutSpotter to support multiple households, each with their own devices, pets, clips, labels, trained models, and data isolation. A prerequisite is the multi-pet profiles work (see `plan-multi-pet-profiles.md`) which replaces hardcoded `my_dog`/`other_dog` with dynamic pet IDs.

Currently the system is single-tenant — one Okta login, one S3 bucket, shared DynamoDB tables, one global model, all devices in one IoT thing group. This plan adds `household_id` scoping across every layer.

### What stays global (admin-level)

- **Settings** (`snout-spotter-settings`) — system-wide configuration (inference mode, autolabel model key, etc.). The settings UI is an admin surface.
- **Stats** (`snout-spotter-stats`) — dashboard metrics remain system-wide. The stats-refresh Lambda and stale-while-revalidate mechanism stay unchanged.
- **AutoLabel COCO models** — `models/yolov8{n,s,m}.onnx` remain at the bucket root, shared across all households.
- **Training agents** — agent registration, shadow, and version management stay global. The training agent pool is shared; jobs carry `household_id` to scope data access.

### What gets scoped per household

Clips, labels, pets, models, exports, training jobs, devices, and all S3 data paths.

## Approach: Household ID on Every Record

Every API request resolves the active household from a user record in DynamoDB. Every DynamoDB write includes `household_id`. Every query filters by it. S3 paths are prefixed by it. Each household gets its own trained model.

Okta handles authentication only — identity (who you are), not authorization (which household you belong to). Household membership is managed entirely in DynamoDB, decoupled from the auth provider.

---

## Phase 1: Auth & User Management

### Okta — No Changes

Okta stays as-is. The JWT `sub` claim (already present) identifies the user. No custom profile attributes, no claim mappings, no Okta admin workflow for household assignment.

### Users Table

**New DynamoDB table:** `snout-spotter-users` (PK: `user_id`)

| Attribute | Type | Description |
|-----------|------|-------------|
| `user_id` (PK) | String | Okta `sub` claim |
| `email` | String | From Okta JWT `email` claim |
| `name` | String | From Okta JWT `name` claim |
| `households` | List of Maps | `[{ householdId, role, joinedAt }]` |
| `created_at` | String | ISO 8601 |
| `last_login_at` | String | ISO 8601 |

A user can belong to 1+ households. Each membership entry:
```json
{ "householdId": "hh-smith-a3f2", "role": "owner", "joinedAt": "2026-04-17T..." }
```

Roles: `owner` (can invite/remove members, delete household) and `member` (standard access). Role enforcement is a future concern — for V1 all members have equal access.

**Modified files:**
- `src/infra/Stacks/CoreStack.cs` — add table

### Households Table

**New DynamoDB table:** `snout-spotter-households` (PK: `household_id`)

| Attribute | Type | Description |
|-----------|------|-------------|
| `household_id` (PK) | String | e.g. `hh-smith-a3f2` |
| `name` | String | Display name (e.g. "Smith Family") |
| `created_at` | String | ISO 8601 |

Display names and future metadata (billing, plan tier, etc.).

**Modified files:**
- `src/infra/Stacks/CoreStack.cs` — add table

### API Middleware

**File:** `src/api/Program.cs`

On every authenticated request:

1. Extract `sub` from the Okta JWT (already available via `context.User`)
2. Look up the user record from `snout-spotter-users` (cached in-memory per Lambda instance, 5-min TTL — same pattern as `SettingsReader`)
3. Read the `X-Household-Id` header from the request
4. Validate the header value is in the user's `households` list
5. Set `context.Items["HouseholdId"]` for downstream use

```csharp
app.Use(async (context, next) =>
{
    var userId = context.User?.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(userId)) { context.Response.StatusCode = 401; return; }

    var user = await userService.GetOrCreateAsync(userId, context.User);
    var householdId = context.Request.Headers["X-Household-Id"].FirstOrDefault();

    if (string.IsNullOrEmpty(householdId) && user.Households.Count == 1)
        householdId = user.Households[0].HouseholdId;

    if (string.IsNullOrEmpty(householdId) || !user.Households.Any(h => h.HouseholdId == householdId))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Invalid or missing household");
        return;
    }

    context.Items["HouseholdId"] = householdId;
    context.Items["UserId"] = userId;
    await next();
});
```

If the user has exactly one household, the header is optional (auto-selected). If they have multiple, the frontend must send it.

### User Auto-Provisioning

**New service:** `src/api/Services/UserService.cs`

`GetOrCreateAsync(userId, claimsPrincipal)`:
- `GetItem` on the users table (with in-memory cache)
- If no record exists, create one from JWT claims (`email`, `name`) with an empty `households` list
- Update `last_login_at` on each call (debounced — skip if last update was <1 hour ago)

This means the first login creates the user record automatically. Household assignment is a separate step (admin API or future self-service).

### Household Management API

**New controller:** `src/api/Controllers/HouseholdsController.cs`

- `GET /api/households` — list the current user's households (from their user record)
- `POST /api/households` — create a new household, add the current user as `owner`
- `GET /api/households/{householdId}/members` — list members (query users table — or denormalise into households table)
- `POST /api/households/{householdId}/members` — invite a user by email (admin/owner only, future)

V1 only needs the first two. Member management can come later.

**New file:** `src/api/Extensions/HttpContextExtensions.cs`

```csharp
public static string GetHouseholdId(this HttpContext context)
    => context.Items["HouseholdId"] as string
       ?? throw new UnauthorizedAccessException("No household_id");

public static string GetUserId(this HttpContext context)
    => context.Items["UserId"] as string
       ?? throw new UnauthorizedAccessException("No user_id");
```

---

## Phase 1.5: Data Migration (DynamoDB backfill)

**Must be run after Phase 1 deploy and before Phase 2 deploy.** Phase 2 scopes all queries by `household_id` — if existing records don't have it, they become invisible.

This phase only backfills the DynamoDB attribute. S3 path migration (moving objects to `{household_id}/...` prefixes) and labels table PK rewrite are deferred to Phase 3 when S3 paths actually change.

### Migration Script

A standalone CLI script (or one-shot Lambda) that:

1. Creates a `hh-default` household in `snout-spotter-households`
2. Auto-provisions the current Okta user(s) with `hh-default` membership (on next login via existing middleware — or via a direct DDB write for known users)
3. Scans each table and stamps `household_id = "hh-default"` on every record that doesn't have one:
   - `snout-spotter-clips` — scan, UpdateItem SET household_id
   - `snout-spotter-labels` — scan, UpdateItem SET household_id
   - `snout-spotter-exports` — scan, UpdateItem SET household_id
   - `snout-spotter-training-jobs` — scan, UpdateItem SET household_id
   - `snout-spotter-models` — scan, UpdateItem SET household_id
4. Updates `snout-spotter-pets` — existing records use `household_id = "default"`, needs re-keying to `"hh-default"` (delete + re-insert since it's the PK)

The script must be **idempotent** — safe to re-run if interrupted. Each UpdateItem uses a `ConditionExpression: attribute_not_exists(household_id)` so already-migrated records are skipped.

### Verification

After running, spot-check:
- `aws dynamodb scan --table snout-spotter-clips --filter-expression "attribute_not_exists(household_id)" --select COUNT` should return 0
- Same for labels, exports, training-jobs, models
- `GET /api/pets` still returns pets (re-keyed from `"default"` to `"hh-default"`)

**New file:** `scripts/migrate-household-backfill.sh` (or `.cs` / `.py`)

---

## Phase 2: DynamoDB — Household Scoping

Every table needs a `household_id` attribute and a GSI for querying by household.

### Clips Table (`snout-spotter-clips`)

**New attribute:** `household_id` (String)

**New GSI:** `by-household` — PK: `household_id`, SK: `timestamp`

All existing GSIs (`all-by-time`, `by-date`, `by-detection`, `by-device`) remain but their queries must add a `FilterExpression` for `household_id`, or new compound GSIs must be created.

**Recommended compound GSIs:**
- `by-household-date` — PK: `household_id#date` (composite string), SK: `timestamp`
- Or: keep existing GSIs + always filter by household_id post-query (simpler, slightly less efficient)

**Modified files:**
- `src/infra/Stacks/CoreStack.cs` — add GSI
- `src/api/Services/ClipService.cs` — every method adds household_id to queries
- `src/api/Controllers/ClipsController.cs` — extract household_id from HttpContext, pass to service

### Labels Table (`snout-spotter-labels`)

**New attribute:** `household_id` (String)

**New GSI:** `by-household-review` — PK: `household_id`, SK: `labelled_at`

**Modified files:**
- `src/api/Services/LabelService.cs` — all queries scoped by household_id
- `src/api/Controllers/LabelsController.cs` — extract household_id, pass to service

### Pets Table (`snout-spotter-pets`)

**No schema change needed.** Already uses `household_id` as PK and `pet_id` as SK. `PetService.cs` already queries by `household_id`. Currently hardcoded to `"default"` — just needs to accept the real value from the JWT.

**Modified files:**
- `src/api/Services/PetService.cs` — replace hardcoded `"default"` with real household_id from context
- `src/api/Controllers/PetsController.cs` — extract household_id, pass to service

### Exports Table (`snout-spotter-exports`)

**New attribute:** `household_id` (String)

Exports scoped to household — each household exports only their labels.

### Training Jobs Table (`snout-spotter-training-jobs`)

**New attribute:** `household_id` (String)

Training jobs scoped — a household trains on their own data.

### Models Table (`snout-spotter-models`)

**New attribute:** `household_id` (String)

Currently PK: `model_id`, GSI: `by-type` (PK: `model_type`, SK: `created_at`). Models need household scoping so each household trains, uploads, and activates their own models independently.

**New GSI:** `by-household-type` — PK: `household_id`, SK: `created_at` with filter on `model_type`. Or compound PK: `household_id#model_type`.

**Modified files:**
- `src/infra/Stacks/CoreStack.cs` — add GSI
- `src/api/Services/ModelService.cs` — all queries/writes scoped by household_id
- `src/api/Controllers/ModelsController.cs` — extract household_id, pass to service

### Commands Table (`snout-spotter-commands`)

Already scoped by `thing_name`. If devices belong to households (Phase 4), this is indirectly scoped.

---

## Phase 3: S3 — Household-Prefixed Paths

All S3 keys gain a household prefix. This is the highest-effort change.

### New Path Layout

```
{household_id}/raw-clips/{device}/YYYY/MM/DD/clip.mp4
{household_id}/keyframes/{device}/YYYY/MM/DD/frame.jpg
{household_id}/models/dog-classifier/best.onnx
{household_id}/models/dog-classifier/class_map.json
{household_id}/models/dog-classifier/versions/{version}.onnx
{household_id}/models/dog-classifier/active.json
{household_id}/training-exports/{export_id}.zip
{household_id}/training-uploads/{filename}.jpg
```

### Files That Construct S3 Keys (all need household prefix)

| File | S3 key construction |
|------|-------------------|
| `src/pi/uploader.py` | `raw-clips/{device}/YYYY/MM/DD/filename` |
| `src/lambdas/IngestClip/Function.cs` | `keyframes/YYYY/MM/DD/filename` |
| `src/lambdas/RunInference/Function.cs` | reads `models/dog-classifier/best.onnx` + `class_map.json` |
| `src/lambdas/AutoLabel/Function.cs` | reads `models/yolov8n.onnx` (global, no household prefix needed) |
| `src/lambdas/ExportDataset/Function.cs` | writes `training-exports/` + `models/dog-classifier/class_map.json` |
| `src/api/Services/S3UrlService.cs` | presigned URLs for clips/keyframes |
| `src/api/Services/S3PresignService.cs` | presigned URLs for uploads |
| `src/api/Controllers/LabelsController.cs` | model paths (`models/dog-classifier/versions/`) |
| `src/api/Services/LabelService.cs` | `training-uploads/` path |
| `src/api/Services/ExportService.cs` | `training-exports/` path |

### EventBridge Prefix Filters (breaking change)

Both `IngestStack.cs` and `InferenceStack.cs` use EventBridge rules with S3 prefix filters (`raw-clips/`, `keyframes/`). Once paths become `{household_id}/raw-clips/...`, **these filters will no longer match**.

**Options:**
- Remove the prefix filter and match on suffix instead (e.g. `.mp4`, `.jpg`) — simpler but noisier
- Use a broader prefix filter and let the Lambda parse the key to extract household_id and the path type
- Use multiple EventBridge rules — impractical with dynamic household IDs

**Recommended:** Remove the prefix-based filter. Instead, filter by suffix (`.mp4` for ingest, `.jpg` for inference) and have the Lambda parse the household_id from the S3 key's first path segment.

**Modified files:**
- `src/infra/Stacks/IngestStack.cs` — update EventBridge rule
- `src/infra/Stacks/InferenceStack.cs` — update EventBridge rule

### SQS Message Types

`src/shared/SnoutSpotter.Contracts/Messages.cs` defines `InferenceMessage(ClipId)`, `BackfillMessage(KeyframeKeys)`, and `TrainingJobMessage`. None carry `household_id`. Either:
- Add `household_id` to each message type (preferred — avoids extra DynamoDB lookups in consumers)
- Or have consuming Lambdas resolve household from the clip/job record

**Modified file:** `src/shared/SnoutSpotter.Contracts/Messages.cs`

### How Lambdas Know the Household

- **IngestClip**: Triggered by S3 event. Parse household_id from the S3 key's first segment (`{household_id}/raw-clips/...`). Write it to the clip's DynamoDB record.
- **RunInference**: Receives clip_id (+ household_id if added to SQS message). Reads the clip record from DynamoDB which has `household_id`. Uses it to construct the model S3 path.
- **ExportDataset**: Receives household_id as input parameter (passed by the API when triggering export).
- **Training agent**: Receives household_id in the `TrainingJobMessage`. Uses it for model upload S3 prefix.

### Pi Device Upload Path

The Pi `uploader.py` needs to know its household_id to construct the correct S3 prefix. Options:
- Store `household_id` in the device's `config.yaml` (set during registration)
- Or: the device uploads to a staging prefix (`uploads/{device}/`) and IngestClip moves it to the household path

**Note:** Phase 4 (IoT device registration) must be completed before Pi devices can upload to household-prefixed paths — see dependency note below.

---

## Phase 4: IoT — Devices Belong to Households

**Dependency:** Phase 3 (S3 paths) requires the Pi to know its `household_id`, which comes from registration in this phase. Implement Phase 4 device registration before Phase 3 Pi upload path changes, or use the staging prefix approach as a bridge.

### Device Registration

**File:** `src/lambdas/SnoutSpotter.Lambda.PiMgmt/Controllers/DevicesController.cs`

- `POST /api/devices/register` accepts `{ name, household_id }` 
- Stores household_id as an IoT Thing attribute
- Writes household_id to the device's config (returned in registration response)
- Pi `setup-pi.sh` writes household_id to `config.yaml`

### Device Listing

- `GET /api/pi/devices` filters by household_id (from JWT)
- Only shows devices belonging to the authenticated user's household
- Uses IoT Thing attributes or a device→household mapping in DynamoDB

### Shadow Access

- Shadow read/write scoped: API only accesses shadows for devices in the user's household
- Validate `thingName` belongs to household before any shadow operation

### Stream Access

`StreamService.cs` constructs KVS stream names as `snoutspotter-{thingName}-live`. Stream names are globally unique (tied to thing name), so no naming change is needed. However, the API must validate that the device belongs to the requesting household before starting/stopping a stream — add household ownership check in the stream endpoints.

---

## Phase 5: Per-Household Models

### Model Storage

Each household has its own model directory in S3:
```
{household_id}/models/dog-classifier/best.onnx
{household_id}/models/dog-classifier/class_map.json
{household_id}/models/dog-classifier/versions/{version}.onnx
{household_id}/models/dog-classifier/active.json
```

### RunInference Lambda

- Receives clip_id → reads clip record → gets household_id
- Constructs model path: `{household_id}/models/dog-classifier/best.onnx`
- Model caching in `/tmp` keyed by household: `/tmp/{household_id}_best.onnx`
- If serving many households, cache eviction needed (LRU or just re-download)

### Model Upload/Activation

- `POST /api/ml/models/upload-url` scoped to household
- `POST /api/ml/models/activate` scoped to household
- `GET /api/ml/models` lists only household's models
- All S3 operations use `{household_id}/models/...` prefix

### Training Pipeline

- ExportDataset: exports only the household's labels
- Training jobs: scoped to household, use household's export
- Training agent: receives household_id in `TrainingJobMessage` (added in Phase 3), uses it to construct the model upload S3 prefix (`{household_id}/models/...`)

**Modified files for training agent:**
- `src/shared/SnoutSpotter.Shared.Training/TrainingJobDesired.cs` — add `HouseholdId` field
- `src/training-agent/SnoutSpotter.TrainingAgent/JobRunner.cs` — use household prefix for model/class_map upload paths

---

## Phase 6: Frontend — Household Context

### Login Flow

1. User authenticates via Okta (unchanged)
2. Frontend calls `GET /api/households` to get the user's household list
3. If one household → auto-select, store in localStorage
4. If multiple → show a household picker before loading the dashboard
5. All subsequent API calls include `X-Household-Id` header

**Modified files:**
- `src/web/src/api.ts` — add `X-Household-Id` header to all requests (read from localStorage or context)
- `src/web/src/App.tsx` — add household resolution step after auth, before rendering routes

### Household Picker

If the user belongs to multiple households, show a simple picker page (or sidebar dropdown):
- List household names from `GET /api/households`
- Selected household stored in `localStorage` as `activeHouseholdId`
- Switching households reloads the dashboard data

### Sidebar

- Show household name in the sidebar header (below the logo)
- If multi-household, show a switcher dropdown
- User name/email from `GET /api/households` response (or JWT claims already available client-side)

---

## Phase 7: S3 & Labels PK Migration

**Note:** DynamoDB `household_id` backfill is handled in Phase 1.5 (prerequisite for Phase 2). This phase covers S3 path migration and the labels PK rewrite needed for Phase 3.

### Labels Table PK Rewrite

The labels table uses `keyframe_key` (an S3 path like `keyframes/2026/03/27/frame.jpg`) as its PK. Once S3 paths become `{household_id}/keyframes/...`, the PK values must change. **DynamoDB does not allow updating a PK** — every label record must be deleted and re-inserted with the new key. This is more expensive than a simple attribute backfill.

### S3 Migration

Every S3 object needs copying to a new key:
```
raw-clips/device/2026/... → hh-default/raw-clips/device/2026/...
keyframes/device/2026/... → hh-default/keyframes/device/2026/...
models/dog-classifier/... → hh-default/models/dog-classifier/...
```

Can be done with a migration script using `aws s3 cp --recursive` or a Lambda.

DynamoDB records that store S3 keys (`s3_key`, `keyframe_keys`, `export_s3_key`, `model_s3_key`, `checkpoint_s3_key`) also need updating to include the household prefix.

### Device Re-registration

Update existing IoT thing attributes with `household_id`. Push `household_id` to Pi `config.yaml` via shadow config or OTA update.

---

## Phase 8: Documentation

- `AGENTS.md` — update all sections with household scoping
- `CLAUDE.md` — add household isolation rules
- Update all API endpoint docs to note household scoping

---

## Effort Estimate

| Phase | Description | Deploy independently? | Effort |
|-------|------------|----------------------|--------|
| 1 | Auth (users table, middleware, households API, auto-provisioning) | Yes — middleware is non-blocking | 2-3 days |
| 1.5 | DynamoDB backfill (`household_id` on all existing records) | Yes — run after Phase 1 | 0.5 day |
| 2 | DynamoDB scoping (7 tables + all queries) | Yes — after Phase 1.5 | 3-5 days |
| 3 | S3 path prefixing (all Lambdas + API + EventBridge + SQS messages) | After Phase 7 (S3 migration) | 3-4 days |
| 4 | IoT device → household + stream validation | Yes — after Phase 2 | 1-2 days |
| 5 | Per-household models + inference + training agent | After Phase 3 | 2-3 days |
| 6 | Frontend household context (picker, sidebar, header) | After Phase 2 | 1 day |
| 7 | S3 & labels PK migration | Run before Phase 3 | 2-3 days |
| 8 | Documentation | Any time | 1 day |
| | **Total** | | **~19-25 days** |

**Safe deploy order:** 1 → 1.5 → 2 → 4 → 6 → 7 → 3 → 5 → 8

This is on top of the multi-pet profiles work (~7 phases, prerequisite).

---

## Prerequisites

1. **Multi-pet profiles** (`plan-multi-pet-profiles.md`) must be completed first. The dynamic pet_id system and class_map.json approach are designed to be household-ready.

## Risks

- **S3 migration volume** — moving thousands of objects is slow and error-prone. Need idempotent migration with progress tracking.
- **Labels table PK migration** — `keyframe_key` is an S3 path that changes with household prefixing. Every label record must be deleted and re-inserted (DynamoDB doesn't allow PK updates). Must be atomic per-record to avoid data loss.
- **EventBridge filter change** — removing S3 prefix filters means Lambdas may be invoked for unrelated S3 events (e.g. release tarballs, terraform state). Lambda code must validate the key structure and exit early for non-matching paths.
- **RunInference multi-model caching** — Lambda `/tmp` is 512MB-10GB. Multiple household models may not fit. Need eviction strategy or ephemeral storage.
- **Cross-household data leaks** — every query must be audited for household filtering. A single missed filter = data leak. Consider automated tests that verify household isolation.
- **Pi device re-provisioning** — existing devices need their config updated with household_id. May require a Pi OTA update + config push.
- **User without a household** — after auto-provisioning, a new user has an empty households list. The API must handle this gracefully (403 with a clear message, or a "no household" landing page). Admin assigns them to a household, or self-service creation is added later.
- **Phase ordering dependency** — Pi devices can't upload to household-prefixed S3 paths (Phase 3) until they know their household_id (Phase 4). Either implement Phase 4 registration first, or use a staging prefix as a bridge.

## Future Extensions

- **Household self-service** — users create their own households, invite members
- **Role-based access** — admin vs viewer within a household
- **Cross-household admin** — super-admin view across all households (for support/debugging)
- **Per-household billing** — track S3/DynamoDB usage per household
- **Shared models** — a marketplace where households can share trained models
