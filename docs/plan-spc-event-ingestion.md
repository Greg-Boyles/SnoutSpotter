# SPC Connector Phase 2 — Event Ingestion (replaces mobile push notifications)

## Context

SPC's mobile app receives pushes for feeding / drinking / pet-door events. We want our server to learn about those same events so we can correlate with SnoutSpotter detections and drive UI. The question was: can our system *receive the pushes* instead of the app?

**No — not without impersonating the user's phone.** SPC's push-notification registration endpoint (`POST /api/me/client` with `{platform, token}`) binds a single APNs/FCM token to a single mobile install. Redirecting or duplicating that token is a policy violation and breaks the user's real app.

**But the underlying data is available via REST** — and that's what every push actually represents. Phase 2 polls SPC's timeline every ~1 minute, persists new events in DynamoDB keyed to our pets, and surfaces them in the UI. No push-forwarding.

### Confirmed from live V1 Swagger (cached at /tmp/spc_v1.json)

- `GET /api/timeline/household/{householdId}` — cursor-capable via `SinceId` / `BeforeId` / `PageSize`. Returns `{ id:int64, type:TimelineEventType, data:JSON, created_at, pets[], devices[], movements[] }`. **This is the feed we ingest.**
- `GET /api/notification` — page-only, simpler payload (`{id, type, text, created_at}`), no cursor. Nice-to-have for human-readable text but not the primary feed.
- No webhooks, no SSE, no WebSocket. REST-only.
- `TimelineEventType` enum has ~100 values; we categorize loosely in Phase 2 and keep raw `data` verbatim for later decoding.

## Scope

1. Poll every linked household's timeline on a 1-minute EventBridge schedule.
2. Persist new events to a new DynamoDB table keyed `household_id` + `{created_at}#{spc_event_id}`.
3. Expose the events at `GET /api/pets/{petId}/spc-events` on the main API.
4. Render a collapsible "Activity" list on each pet card in the existing Pets page.

## Architecture

### New Lambda: `snout-spotter-spc-poller`
- Project: `src/lambdas/SnoutSpotter.Lambda.SpcPoller/` — plain event-driven handler (mirror `SnoutSpotter.Lambda.AutoLabel`, **not** the Lambda Web Adapter pattern used by the connector).
- Trigger: EventBridge rule `Schedule.Rate(Duration.Minutes(1))`.
- Per tick:
  1. Scan `snout-spotter-households` with `FilterExpression = "spc_integration.#s = :linked"`.
  2. For each linked household, bounded parallel (max 5) via `SemaphoreSlim`:
     - Load access token from Secrets Manager (`snoutspotter/spc/{household_id}`).
     - Load cursor `spc_integration.last_timeline_id` from the household record.
     - `GET /api/timeline/household/{spc_hh}?SinceId={cursor}&PageSize=50`.
     - **First-ever poll** (cursor missing): `PageSize=1`, seed cursor from the max id, don't persist history.
     - Otherwise: map `spc_pet_id → pet-…` via `snout-spotter-pets`, write rows, advance cursor, update `last_sync_at`.
  3. On 401: `MarkTokenExpiredAsync` (already implemented in Phase 1). On 429 / upstream: `last_error = "rate_limited"`, skip rest of tick.
- `context.RemainingTime` guard: stop scheduling new households below 10s remaining; next tick picks them up (cursor only advances on success).

### Shared client extract
`src/lambdas/SnoutSpotter.Lambda.Spc/Services/SpcApiClient.cs` today is consumed only by the connector. The poller becomes the second consumer — the "extract when third Lambda needs it" rule now triggers with 2 consumers of external HTTP. Move to `src/shared/SnoutSpotter.Spc.Client/`:
- `ISpcApiClient.cs`, `SpcApiClient.cs`, `SpcExceptions.cs`
- `Models/SpcApiModels.cs` (all DTOs)

Add to the shared client:
```csharp
Task<List<SpcTimelineResource>> ListTimelineAsync(
    string accessToken, long spcHouseholdId, long? sinceId, int pageSize, CancellationToken ct);
```

New DTO `SpcTimelineResource` with `id`, `type`, `data` (as `JsonElement?` — preserved verbatim), `created_at`, `pets`, `devices`, `movements`.

`HouseholdIntegrationService`, `SpcSecretsStore`, `PetLinkService` stay duplicated between connector and poller for Phase 2 — they're thin wrappers around DynamoDB/Secrets Manager and their shape is the SSOT, not the code. Note in PR description; revisit if a 3rd consumer appears.

### Data model

**New table** `snout-spotter-spc-events` (added to `src/infra/Stacks/CoreStack.cs`):
- PK: `household_id` (S)
- SK: `created_at_event` (S) — composite `{created_at}#{spc_event_id}` for chronological Query even if SPC ids aren't strictly monotonic across households.
- No GSI in Phase 2. Query by `household_id` + `FilterExpression = "pet_id = :pid"`. Add `by-pet` GSI only when volume demands.
- PITR on, Retain policy.

**Attributes**:
| name              | type | notes                                       |
|-------------------|------|---------------------------------------------|
| `household_id`    | S    | PK                                          |
| `created_at_event`| S    | SK                                          |
| `spc_event_id`    | S    | stringified int64                           |
| `spc_event_type`  | N    | raw enum                                    |
| `event_category`  | S    | `feeding|drinking|movement|device_status|other` |
| `created_at`      | S    | ISO-8601                                    |
| `pet_id`          | S?   | our internal `pet-…` id                     |
| `spc_pet_id`      | S?   | first `pets[].id` if any                    |
| `device_id`       | S?   | first `devices[].id` if any                 |
| `raw_data`        | S    | `JsonElement.GetRawText()` from SPC         |

Write with `PutItem` (no condition) — natural PK+SK is idempotent under replay.

**Cursor storage**: new field `last_timeline_id` inside the existing `spc_integration` map on `snout-spotter-households`. No schema change — DynamoDB is schemaless.

### Event categorizer (loose, in `Services/EventCategorizer.cs` in poller)

```csharp
public static string Categorize(int eventType) => eventType switch
{
    >= 20000 and <= 20999 => "feeding",       // feeder / bowl
    >= 22000 and <= 22999 => "drinking",      // felaqua
    20 => "movement",                          // pet door passthrough
    >= 21000 and <= 21999 => "movement",
    >= 9000 and <= 19999 => "device_status",
    _ => "other"
};
```

Deliberately coarse. Raw `spc_event_type` is preserved; a full decoder lands later.

### Infra

**`src/infra/Stacks/CoreStack.cs` additions**:
- New ECR repo `snout-spotter-spc-poller`.
- New DynamoDB table `snout-spotter-spc-events` (above).
- New SSM param `/snoutspotter/core/spc-events-table-name`.

**New stack `src/infra/Stacks/SpcPollerStack.cs`**:
- `DockerImageFunction snout-spotter-spc-poller`, 512 MB, 1 min timeout.
- Env: table names + `SPC_BASE_URL=https://app-api.beta.surehub.io`.
- IAM: `secretsmanager:GetSecretValue` on `snoutspotter/spc/*`; `dynamodb:Scan + UpdateItem` on households; `dynamodb:Query` on pets; `dynamodb:PutItem + Query` on spc-events.
- `Rule` with `Schedule.Rate(Duration.Minutes(1))` → `LambdaFunction` target.

**`src/infra/Program.cs`**: register `SpcPollerStack` after `SpcConnectorStack`.

**CI**: new `build-spc-poller-image.yml`, `deploy-spc-poller.yml`; wire into `deploy.yml` paths-filter so shared-client changes rebuild both connector and poller images.

### Read endpoint (main API, not connector)

The events are already in our DynamoDB — no SPC call at read time. The main `snout-spotter-api` Lambda has Okta auth + household middleware + DynamoDB wired, so adding the route there is zero-cost.

- `src/api/Controllers/PetsController.cs`: new `GET {petId}/spc-events?limit=50&nextPageKey=…`.
- `src/api/Services/SpcEventsService.cs` + `Interfaces/ISpcEventsService.cs`: query by `household_id` with `ScanIndexForward = false` (newest first) and `FilterExpression = "pet_id = :pid"`. Cursor pagination via base64 `LastEvaluatedKey` — reuse the pattern in `ClipService`.
- `src/api/Models/SpcEventDto.cs`: PascalCase record; `RawData` exposed as opaque string.
- `src/api/AppConfig.cs`: add `SpcEventsTable`. `src/api/Program.cs`: bind env + register service.
- `src/infra/Stacks/ApiStack.cs`: add `SPC_EVENTS_TABLE` env from SSM + `dynamodb:Query` on `snout-spotter-spc-events`.

### Frontend

- `src/web/src/types.ts`: new `SpcEvent`, `SpcEventsPage`.
- `src/web/src/api.ts`: `listSpcEventsForPet(petId, nextPageKey?)` on `BASE` (not `SPC_INTEGRATION_BASE`).
- `src/web/src/pages/Pets.tsx`: new `<PetActivity petId={…} />` subcomponent behind an Activity toggle on each pet card. Lazy-loads on expand. Vertical list: icon by category (`Utensils` / `Droplets` / `DoorOpen` / `Plug` / `Circle`), relative time, derived sentence (Phase 2: `"{pet.name} {category_verb}"`; swap richer text later once we decode `raw_data`).
- No Integrations page changes — activity lives on the pet, not the connector card.

## Edge cases

- **First poll**: seed cursor, don't persist historical page (avoids wall of stale events).
- **Unmapped SPC pet**: persist with `pet_id = null`; will never render until user maps the pet. No data loss.
- **Duplicates**: natural PK+SK idempotency.
- **Token expiry**: `SpcUnauthorizedException` → `MarkTokenExpiredAsync` → next tick's Scan filter skips it.
- **Rate-limited (429)**: Polly handles short retries in the shared client; sustained 429 sets `last_error = "rate_limited"` without flipping status.
- **Scale**: Scan works up to ~100 linked households. TODO comment for SQS fan-out beyond.

## Critical files

- `src/shared/SnoutSpotter.Spc.Client/` **(new shared project)** — extracted SPC HTTP + DTOs, plus new `ListTimelineAsync`.
- `src/lambdas/SnoutSpotter.Lambda.SpcPoller/` **(new Lambda)** — Function.cs, EventCategorizer.cs, duplicated wrappers for secrets / household / pet-link services.
- `src/infra/Stacks/CoreStack.cs` — new ECR repo + events table + SSM param.
- `src/infra/Stacks/SpcPollerStack.cs` **(new)**.
- `src/infra/Program.cs` — wire new stack.
- `src/infra/Stacks/ApiStack.cs` — events-table env + Query IAM.
- `src/api/Controllers/PetsController.cs` — new route.
- `src/api/Services/SpcEventsService.cs` **(new)**.
- `src/api/Models/SpcEventDto.cs` **(new)**.
- `src/api/AppConfig.cs` + `Program.cs` — env binding + DI.
- `src/web/src/pages/Pets.tsx` — Activity panel.
- `src/web/src/api.ts` + `src/web/src/types.ts` — event types + fetcher.
- `.github/workflows/deploy.yml` + new `build-spc-poller-image.yml` / `deploy-spc-poller.yml`.

## Incremental delivery (2 PRs)

1. **PR5 — poller infra + backend.** Shared-client extract + `ListTimelineAsync`, new poller Lambda + stack, CoreStack additions, CI. Connector Lambda updated to consume the shared client (namespace-level change only). No user-visible UI.
2. **PR6 — API read endpoint + UI.** Main API `GET /api/pets/{petId}/spc-events`, Pets-page Activity section.

## Verification

- `dotnet build SnoutSpotter.sln`
- `cdk synth SnoutSpotter-Core SnoutSpotter-SpcPoller SnoutSpotter-Api`
- `cd src/web && npm run build`
- Beta runbook: link household, wait 2 min, feed the dog / trigger a door, confirm `snout-spotter-spc-events` row; open Pets page, expand Activity, confirm row renders.
- Token-expiry path: rotate the Secrets Manager secret to garbage, confirm `spc_integration.status = token_expired` within 1 tick and the household is skipped thereafter.

## Open items

- `TimelineEventType` ranges are approximate (sourced from community `surepy` reverse-engineering). Phase 2 category accuracy is "good enough"; proper decoder is a follow-up PR.
- `text` field not persisted in Phase 2. UI derives a sentence from category + pet name. If human-readable text is worth it, a second pass hitting `GET /api/notification` can enrich events (adds a second API call per tick — defer until demand).
- Per-household poll failure isolation: currently per-tick `last_error` gets overwritten across all pets in a household. Fine for Phase 2; consider a small `last_error_at` alongside.
