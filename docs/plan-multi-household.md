# Plan: Multi-Household Isolation

## Context

This plan extends SnoutSpotter to support multiple households, each with their own devices, pets, clips, labels, trained models, and data isolation. A prerequisite is the multi-pet profiles work (see `plan-multi-pet-profiles.md`) which replaces hardcoded `my_dog`/`other_dog` with dynamic pet IDs.

Currently the system is single-tenant — one Okta login, one S3 bucket, shared DynamoDB tables, one global model, all devices in one IoT thing group. This plan adds `household_id` scoping across every layer.

## Approach: Household ID on Every Record

Every API request extracts `household_id` from the Okta JWT. Every DynamoDB write includes it. Every query filters by it. S3 paths are prefixed by it. Each household gets its own trained model.

---

## Phase 1: Auth — Household ID in JWT

### Okta Configuration

**File:** `terraform/okta/main.tf`

- Add a custom user profile attribute: `household_id` (String)
- Add a claim mapping to the authorization server: `household_id` → `user.household_id`
- Each user is assigned a household_id when provisioned in Okta

### API Middleware

**File:** `src/api/Program.cs`

Add middleware that extracts `household_id` from the JWT on every request:

```csharp
app.Use(async (context, next) =>
{
    var householdId = context.User?.FindFirst("household_id")?.Value;
    if (string.IsNullOrEmpty(householdId))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Missing household_id claim");
        return;
    }
    context.Items["HouseholdId"] = householdId;
    await next();
});
```

**New file:** `src/api/Extensions/HttpContextExtensions.cs`

Helper to extract household_id cleanly in controllers/services:

```csharp
public static string GetHouseholdId(this HttpContext context)
    => context.Items["HouseholdId"] as string
       ?? throw new UnauthorizedAccessException("No household_id");
```

### Household Management

**New DynamoDB table:** `snout-spotter-households` (PK: `household_id`)

| Attribute | Type | Description |
|-----------|------|-------------|
| `household_id` | String (PK) | e.g. `hh-smith-a3f2` |
| `name` | String | Display name (e.g. "Smith Family") |
| `created_at` | String | ISO 8601 |

Simple table — mainly for display names and future metadata. Not strictly required if household_id comes from Okta, but useful for the UI.

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

**New GSI:** `by-household` — PK: `household_id`, SK: `created_at`

Pets already have a small dataset — a scan with filter works, but a GSI is cleaner.

**Modified files:**
- `src/api/Services/PetService.cs` — ListPets queries by household_id

### Exports Table (`snout-spotter-exports`)

**New attribute:** `household_id` (String)

Exports scoped to household — each household exports only their labels.

### Training Jobs Table (`snout-spotter-training-jobs`)

**New attribute:** `household_id` (String)

Training jobs scoped — a household trains on their own data.

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

### How Lambdas Know the Household

- **IngestClip**: Triggered by S3 event. The S3 key already contains the device name. A device→household lookup (DynamoDB or IoT Thing attribute) resolves the household.
- **RunInference**: Receives clip_id. Reads the clip record from DynamoDB which has `household_id`. Uses it to construct the model S3 path.
- **ExportDataset**: Receives household_id as input parameter (passed by the API when triggering export).

### Pi Device Upload Path

The Pi `uploader.py` needs to know its household_id to construct the correct S3 prefix. Options:
- Store `household_id` in the device's `config.yaml` (set during registration)
- Or: the device uploads to a staging prefix (`uploads/{device}/`) and IngestClip moves it to the household path

---

## Phase 4: IoT — Devices Belong to Households

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
- Training agent: receives household_id in job config, uploads model to household's S3 prefix

---

## Phase 6: Frontend — Household Context

### Single Household Per Login (Recommended for V1)

- JWT contains household_id — everything auto-scoped
- User never sees other households' data
- No household switcher needed
- Household name shown in sidebar header

### Future: Household Switcher

- User can belong to multiple households (Okta group per household)
- Dropdown in sidebar to switch active household
- Requires the API to accept household_id as a header/parameter rather than always from JWT

---

## Phase 7: Data Migration

### Default Household

- Create a default household for existing data (e.g. `hh-default`)
- Backfill `household_id` on all existing clips, labels, exports, training jobs, pets
- Update all existing S3 keys to include the household prefix (S3 copy + delete)
- Update device registrations with household_id

### S3 Migration

This is the most expensive part — every S3 object needs copying to a new key:
```
raw-clips/device/2026/... → hh-default/raw-clips/device/2026/...
keyframes/device/2026/... → hh-default/keyframes/device/2026/...
models/dog-classifier/... → hh-default/models/dog-classifier/...
```

Can be done with a migration script using `aws s3 cp --recursive` or a Lambda.

DynamoDB records that store S3 keys (`s3_key`, `keyframe_keys`) also need updating.

---

## Phase 8: Documentation

- `AGENTS.md` — update all sections with household scoping
- `CLAUDE.md` — add household isolation rules
- Update all API endpoint docs to note household scoping

---

## Effort Estimate

| Phase | Description | Effort |
|-------|------------|--------|
| 1 | Auth (Okta claim + middleware) | 1-2 days |
| 2 | DynamoDB scoping (6 tables + all queries) | 3-4 days |
| 3 | S3 path prefixing (all Lambdas + API) | 2-3 days |
| 4 | IoT device → household | 1 day |
| 5 | Per-household models + inference | 2 days |
| 6 | Frontend household context | 1 day |
| 7 | Data migration | 1-2 days |
| 8 | Documentation | 1 day |
| | **Total** | **~15-20 days** |

This is on top of the multi-pet profiles work (~7 phases, prerequisite).

---

## Prerequisites

1. **Multi-pet profiles** (`plan-multi-pet-profiles.md`) must be completed first. The dynamic pet_id system and class_map.json approach are designed to be household-ready.
2. **Okta access** to add custom user profile attributes and claim mappings.

## Risks

- **S3 migration volume** — moving thousands of objects is slow and error-prone. Need idempotent migration with progress tracking.
- **RunInference multi-model caching** — Lambda `/tmp` is 512MB-10GB. Multiple household models may not fit. Need eviction strategy or ephemeral storage.
- **Cross-household data leaks** — every query must be audited for household filtering. A single missed filter = data leak. Consider automated tests that verify household isolation.
- **Pi device re-provisioning** — existing devices need their config updated with household_id. May require a Pi OTA update + config push.
- **Okta user management** — assigning users to households requires admin workflow. No self-service household creation initially.

## Future Extensions

- **Household self-service** — users create their own households, invite members
- **Role-based access** — admin vs viewer within a household
- **Cross-household admin** — super-admin view across all households (for support/debugging)
- **Per-household billing** — track S3/DynamoDB usage per household
- **Shared models** — a marketplace where households can share trained models
