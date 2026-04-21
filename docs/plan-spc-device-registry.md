# SnoutSpotter Device Registry + SPC Device Linking

## Context

Today SnoutSpotter has no first-class "device" record. Pi cameras live only as IoT Things with a `household_id` attribute; SPC devices are fetched live from Sure Pet Care on demand. The user wants:

1. A single household-wide view listing every device (both our Pi cameras and the household's SPC bowls / pet doors / feeders / hubs).
2. Human-friendly `display_name` + `notes` on each device (our Pis have never had these).
3. The ability to say "this Pi watches that SPC bowl", with **many-to-many** cardinality — a bowl can be filmed by two Pis, a Pi can film several bowls.

This is Phase 1 of a broader device-registry effort. Out of scope here: device events/telemetry (that's the separate Phase 2 plan at `docs/plan-spc-event-ingestion.md`), rooms/tags, physical coordinates.

## Decisions locked

- **Single physical DynamoDB table** `snout-spotter-devices`, household-scoped (matches our other household-scoped tables like `snout-spotter-pets`).
- **Three SK prefixes** — no nesting, so many-to-many links don't duplicate device metadata:
  - `snoutspotter#{thing_name}` — our Pi row
  - `spc#{spc_device_id}` — SPC device row
  - `link#spc#{spc_device_id}#snoutspotter#{thing_name}` — thin many-to-many link row
- **Source is called `snoutspotter`**, not `pi`, in code, SK prefix, and UI.
- **SPC static fields mirrored locally**: `spc_product_id`, `spc_name`, `serial_number`, set at first-touch and refreshable. Dynamic fields (`last_activity_at`, battery etc.) stay live-fetched via the existing `/api/integrations/spc/devices`.
- **User-curated fields** on both row types: `display_name`, `notes`.
- **`snoutspotter#` rows are lazy-created** on the first authenticated main-API read. PiMgmt (`/register`) stays **unchanged** — it's unauthed and doesn't know the household. The main API lists IoT Things for the caller's household, diffs against existing `snoutspotter#` rows, and upserts any missing ones with `display_name = thing_name`, empty notes. Idempotent by SK.
- **`spc#` rows are lazy-created** on first user edit or first link. `POST /api/integrations/spc/link` (the Phase 1 wizard) does **not** pre-populate the table.

## Data model

Definition pattern mirrors `PetsTable` in `src/infra/Stacks/CoreStack.cs:365`.

- PK: `household_id` (STRING)
- SK: `sk` (STRING) — matches one of the three prefixes above
- Billing: `PAY_PER_REQUEST`, `RemovalPolicy.RETAIN`, PITR on
- No GSIs for Phase 1

Attributes per row type:

| Row | Attributes |
|---|---|
| `snoutspotter#{thing}` | `household_id`, `sk`, `source="snoutspotter"`, `thing_name`, `display_name`, `notes`, `created_at`, `updated_at` |
| `spc#{spc_id}` | `household_id`, `sk`, `source="spc"`, `spc_device_id`, `spc_product_id`, `spc_name`, `serial_number`, `display_name`, `notes`, `last_refreshed_at`, `created_at`, `updated_at` |
| `link#spc#{id}#snoutspotter#{thing}` | `household_id`, `sk`, `spc_device_id`, `thing_name`, `created_at`, `created_by` |

Query patterns — all served by the primary key, no GSI needed:

- **All devices + links for a household**: `Query(PK=hh)` — one round-trip, three SK prefixes, partition client-side.
- **Which Pis watch SPC device X**: `Query(PK=hh, SK begins_with "link#spc#X#snoutspotter#")`.
- **Which SPC devices does Pi T watch**: fall out of the first query, filter `link#` rows by `thing_name`. In-household fan-out is tiny.

Example rows (household `hh-42`, Pi `snoutspotter-kitchen` watching bowls 111 + 222; unlinked garage Pi; unlinked pet door 333):

```
(hh-42, snoutspotter#snoutspotter-kitchen)       display_name="Kitchen Cam"
(hh-42, snoutspotter#snoutspotter-garage)        display_name="snoutspotter-garage"
(hh-42, spc#111)                                 spc_name="Smart Bowl A", display_name="Bowl A"
(hh-42, spc#222)                                 spc_name="Smart Bowl B"
(hh-42, spc#333)                                 spc_name="Front Door",   display_name="Cat Door"
(hh-42, link#spc#111#snoutspotter#snoutspotter-kitchen)
(hh-42, link#spc#222#snoutspotter#snoutspotter-kitchen)
```

## Where writes and reads live

All registry endpoints on the **main API** (`src/api/`). PiMgmt is untouched.

- `GET /api/devices` — `Query(PK=hh)`, reconcile against IoT Things (lazy-create missing `snoutspotter#` rows), return `{ snoutspotter, spc, links }`.
- `PUT /api/devices/snoutspotter/{thing}` — update `display_name`, `notes`. Asserts Pi ownership via `DeviceOwnershipService.cs` (already caches thing → household).
- `PUT /api/devices/spc/{spcId}` — upsert `spc#` row (lazy-create with mirrored fields if missing). Sets `display_name`, `notes`.
- `POST /api/devices/spc/{spcId}/refresh` — re-fetch `spc_product_id`, `spc_name`, `serial_number` from SPC, write to the row. Updates `last_refreshed_at`.
- `POST /api/devices/links` — body `{ spcDeviceId, snoutspotterThingName }`. Validates Pi ownership; lazy-creates `spc#` row if absent; writes `link#` row.
- `DELETE /api/devices/links/{spcDeviceId}/{thing}` — single-item delete.

### SPC outbound calls from the main API

The main API needs to call Sure Pet Care directly (for refresh + lazy-create). Chosen approach: **extract the SPC HTTP client into a shared class library** `src/shared/SnoutSpotter.Spc.Client/` referenced by both the SPC Lambda (today's sole consumer) and the main API.

Why this over `lambda:InvokeFunction` against the SPC Lambda:
1. The client is ~300 LOC of HTTP + DTOs already fronted by `ISpcApiClient`.
2. One network hop, no IAM sprawl, no auth pass-through (main API would otherwise have to forge a household-scoped JWT).
3. Sets up cleanly for Phase 2's poller, which will also need to read the SPC secret server-side.

The main API gets `secretsmanager:GetSecretValue` on `arn:aws:secretsmanager:*:*:secret:snoutspotter/spc/*` and uses the shared `SpcSecretsStore` to read the household's SPC token when refreshing.

### SPC Lambda unlink sweep

`DELETE /api/integrations/spc` today (`src/lambdas/SnoutSpotter.Lambda.Spc/Controllers/SpcIntegrationController.cs:223`) clears the secret, household integration map, and pet attrs. Add two sweeps:

- `Query(PK=hh, SK begins_with "spc#")` → `BatchWriteItem` deletes
- `Query(PK=hh, SK begins_with "link#spc#")` → `BatchWriteItem` deletes

`snoutspotter#` rows are untouched on SPC unlink.

## Infra

- `src/infra/Stacks/CoreStack.cs` — new `DevicesTable` next to `PetsTable` (around line 374), expose as public property, `StringParameter` at `/snoutspotter/core/devices-table-name`.
- `src/infra/Stacks/ApiStack.cs`:
  - Add `DevicesTable` to props; `props.DevicesTable.GrantReadWriteData(apiFunction)`.
  - `["DEVICES_TABLE"] = props.DevicesTable.TableName` env.
  - New IAM: `secretsmanager:GetSecretValue` on `secret:snoutspotter/spc/*`.
- `src/infra/Stacks/SpcConnectorStack.cs`:
  - `props.DevicesTable.GrantReadWriteData(spcFunction)` (Query + BatchWriteItem for unlink sweep).
  - `DEVICES_TABLE` env var.
- `src/infra/Program.cs` — pass `DevicesTable` into both stacks.
- **No new stacks, no new ECR repos. PiMgmt stack is untouched.**

## Main API code

- `src/api/Services/Interfaces/IDeviceRegistryService.cs` + `src/api/Services/DeviceRegistryService.cs`:
  - `ListAsync(householdId)` — Query, reconcile missing Pi rows, return typed DTOs.
  - `UpsertSnoutSpotterAsync`, `UpsertSpcAsync`, `RefreshSpcFromSpcAsync`, `LinkAsync`, `UnlinkAsync`.
  - Uses existing `IAmazonDynamoDB` singleton (`src/api/Program.cs:49`) + new `AppConfig.DevicesTable`.
- `src/api/Controllers/DevicesRegistryController.cs` — route `api/devices`. Kept separate from the existing `DevicesController` (which is the live IoT-shadow surface at `api/device/...`). `[Authorize]` + `HttpContext.GetHouseholdId()`.
- `src/api/Models/DeviceRegistryModels.cs` — `DeviceListResponse`, `SnoutSpotterDeviceDto`, `SpcDeviceDto`, `DeviceLinkDto`, `UpdateDeviceRequest`, `CreateLinkRequest`.
- `src/api/AppConfig.cs` — add `DevicesTable`. `src/api/Program.cs` — bind `DEVICES_TABLE` env (mirror pattern at line 41), register `IDeviceRegistryService` + shared `ISpcApiClient` + `ISpcSecretsStore`.

## Shared library

New project `src/shared/SnoutSpotter.Spc.Client/` containing (moved from `src/lambdas/SnoutSpotter.Lambda.Spc/`):

- `ISpcApiClient.cs`, `SpcApiClient.cs`, `SpcExceptions.cs`
- `Services/SpcSecretsStore.cs` + `ISpcSecretsStore.cs`
- `Models/SpcApiModels.cs` (DTOs)

The SPC Lambda switches to a `ProjectReference`; namespace moves from `SnoutSpotter.Lambda.Spc.Services` / `SnoutSpotter.Lambda.Spc.Models` to `SnoutSpotter.Spc.Client`. Pure move, no behaviour change.

## Frontend

- New page `src/web/src/pages/Devices.tsx` modelled on `src/web/src/pages/Pets.tsx`:
  - Two grouped lists — "SnoutSpotter cameras" and "SPC devices" — with source chips.
  - Inline-editable `display_name`, collapsible `notes`, link-count badge.
  - "Manage links" drawer: list SPC devices with a checkbox per row; toggle calls `devices.link` / `devices.unlink`.
  - Per-SPC-row "Refresh from SPC" button.
- Route `/devices`, sidebar entry near "Pets".
- `src/web/src/api.ts` — new `api.devices` namespace: `list`, `updateSnoutSpotter`, `updateSpc`, `refreshSpc`, `link`, `unlink` — all on `BASE`.
- `src/web/src/pages/DeviceDetail.tsx` — surface + edit `display_name` and `notes` (falls back to `thingName` if row is somehow missing).
- `src/web/src/pages/Integrations.tsx` — add a small "Manage device mapping" link pointing at `/devices`. No structural change.
- `src/web/src/types.ts` — new DTOs.

## Interaction with existing SPC flow

- `POST /api/integrations/spc/link` (Phase 1 wizard) — **no change**. Rows are lazy, not pre-populated.
- `DELETE /api/integrations/spc` — add the sweep described in "SPC Lambda unlink sweep" above.
- Nothing in Phase 2's `docs/plan-spc-event-ingestion.md` depends on this table yet; Phase 2 polling keys events to `spc_pet_id` on pets, not devices.

## Critical files

- `src/infra/Stacks/CoreStack.cs` — add `DevicesTable` + SSM param.
- `src/infra/Stacks/ApiStack.cs`, `src/infra/Stacks/SpcConnectorStack.cs`, `src/infra/Program.cs` — wire the table and env through.
- `src/shared/SnoutSpotter.Spc.Client/` **(new project)** — extracted client + secrets + models.
- `src/lambdas/SnoutSpotter.Lambda.Spc/` — switch to the shared client; add unlink sweep.
- `src/api/Services/DeviceRegistryService.cs` **(new)**, `Controllers/DevicesRegistryController.cs` **(new)**, `Models/DeviceRegistryModels.cs` **(new)**.
- `src/api/AppConfig.cs`, `src/api/Program.cs` — env + DI.
- `src/web/src/pages/Devices.tsx` **(new)**, `pages/DeviceDetail.tsx` (edit), `pages/Integrations.tsx` (one-line edit).
- `src/web/src/api.ts`, `src/web/src/types.ts`.

## Reuse from existing code

- `src/api/Services/DeviceOwnershipService.cs` (line 42) — Pi → household ownership check, cached.
- `src/infra/Stacks/CoreStack.cs` PetsTable (line 365) — table-definition convention to copy.
- `src/api/Program.cs` household middleware (line 131–188) — already extracts `HouseholdId` into `HttpContext.Items`; new controller just calls `GetHouseholdId()`.
- `src/web/src/pages/Pets.tsx` — page pattern (list + inline edit + empty state).

## PR split (3 PRs, stacked)

1. **Infra + shared SPC client extract.** New `DevicesTable` in `CoreStack`; SSM param; props+env+grants on API + SPC stacks. Extract `SpcApiClient` / `SpcSecretsStore` / DTOs into `src/shared/SnoutSpotter.Spc.Client/`; switch the SPC Lambda to consume it (pure move). Ships safely — table unused until PR 2.
2. **Backend registry.** `DeviceRegistryService`, `DevicesRegistryController`, all five endpoints, `api/devices` surface, SPC-unlink sweep. Endpoints reachable via curl; no UI yet.
3. **Frontend.** `Devices.tsx`, sidebar entry, `api.ts` methods, `DeviceDetail.tsx` edits, `Integrations.tsx` link. Immediately user-visible on deploy.

## Verification

- `dotnet build SnoutSpotter.sln` — full solution, including new shared project and the moved references.
- `cd src/infra && cdk synth SnoutSpotter-Core SnoutSpotter-Api SnoutSpotter-Spc` — confirm `DevicesTable` resource (PK `household_id` / SK `sk`, PITR), grants on both Lambda roles, `DEVICES_TABLE` env.
- `cd src/web && npm run build`.
- **Beta end-to-end**:
  1. Register a new Pi → `GET /api/devices` auto-creates the `snoutspotter#` row.
  2. `POST /api/devices/links` twice to link two SPC bowls → verify two `link#` rows and two `spc#` rows (lazy-created) via `aws dynamodb query`.
  3. `DELETE /api/devices/links/.../...` removes one link only; other `link#` and both `spc#` rows stay.
  4. `PUT /api/devices/snoutspotter/{thing}` sets display_name + notes; trigger Pi re-registration → verify edits survive (idempotent upsert).
  5. `POST /api/devices/spc/{id}/refresh` → `spc_product_id`, `spc_name`, `serial_number`, `last_refreshed_at` update.
  6. `DELETE /api/integrations/spc` → all `spc#` and `link#spc#` rows gone; `snoutspotter#` rows untouched.
  7. Cross-household isolation: household B cannot read household A rows via any endpoint (existing middleware).

## Open items

None remaining after clarifications. Phase 2 event ingestion is orthogonal and already planned at `docs/plan-spc-event-ingestion.md`.
