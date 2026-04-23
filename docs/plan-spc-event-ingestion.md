# SPC Connector Phase 2 — Motion-Triggered Event Ingestion

## Context

SPC's mobile app receives pushes for feeding / drinking / pet-door events. We want our server to learn about those same events so we can correlate with SnoutSpotter detections. We can't receive the pushes (see earlier discussion — APNs/FCM token is bound to the user's phone), but the underlying data is in SPC's REST timeline:

- `GET /api/timeline/household/{householdId}` — cursor-capable via `SinceId`. Returns `{ id:int64, type:TimelineEventType, data:JSON, created_at, pets[], devices[], movements[] }`. The feed we ingest.
- No webhooks, no SSE, no WebSocket. REST-only.

**Cost-conscious design: poll only when something is worth polling for.** A naïve 1-minute EventBridge cron wastes ~1440 Lambda invokes per day per household during the ~22 hours per day that nothing is happening. Instead we trigger polling when our own Pi cameras see motion — the exact moments an SPC event is likely to follow.

## Decisions (locked)

1. **Motion-triggered burst polling** — start or extend a 10-minute polling window when a linked Pi uploads a clip.
2. **Per-household burst** — any linked Pi's motion starts a household-wide burst. SPC's timeline is household-scoped so one poll covers all Pis.
3. **Any uploaded clip triggers** (for now). Later we'll tighten to "only when a pet is actually detected" by moving the trigger from `IngestClip` to `RunInference`. Same downstream — poller doesn't care which Lambda kicked it off.
4. **No backfill, no safety-net cron.** Events that land after the burst window closes wait for the next motion. If the dog is gone for two hours, we don't care what happened.
5. **10-minute window**, extended (not reset) on subsequent motion within the window. SPC hub upload latency is the dominant factor.
6. **Cursor seeded on first motion, not at link time** — keeps the link wizard fast. First burst-start handler treats `last_timeline_id == null` as "seed mode": fetch latest id, store it, return without persisting historical events.

## Architecture

### Flow

```
Pi uploads clip → S3 PutObject → IngestClip Lambda (existing)
                                     │
                                     ├── existing keyframe extract
                                     │
                                     └── NEW: if Pi linked to any SPC device,
                                         enqueue BurstStart message for household
                                             │
                                             ▼
                                  snout-spotter-spc-burst SQS queue
                                             │
                                             ▼
                                  SpcPoller Lambda
                                     │
                                     ├── Upsert burst record (extend poll_until)
                                     ├── If within window, GET SPC timeline
                                     │   since last_timeline_id
                                     ├── Write new events to DynamoDB
                                     └── If poll_until > now + 30s,
                                         re-enqueue a 30s-delayed SQS message

When the 30-second self-scheduling chain fires after poll_until, the handler
short-circuits without re-enqueuing. The chain dies naturally. Zero Lambda
invocations when the house is quiet.
```

### Self-scheduling via SQS delay

SQS messages support `DelaySeconds` up to 15 min. The poller:

1. Receives a message (either from IngestClip's burst-start or a previous self-schedule).
2. Loads the burst record for the household. If `poll_until <= now`, return — chain dies.
3. Polls SPC timeline with `SinceId=last_timeline_id&PageSize=50`.
4. Persists new events; advances cursor; updates `last_sync_at`.
5. If `poll_until - now > 30s`, send a new message with `DelaySeconds=30`. Otherwise let the chain terminate.

Deduplication: we don't want two chains running for the same household. Two approaches:

- **Option A — SQS FIFO with MessageGroupId = household_id.** FIFO guarantees in-order processing per group, so if two clips arrive in the same second only one chain starts polling. Simple, but FIFO queues have throughput limits (~300 msg/sec per group) — fine for our scale.
- **Option B — Idempotent burst-start.** Standard queue, but `SpcBurstStart` for an already-active household is a no-op extend-only update. Accept that two messages in the same second might both trigger a poll, but the cursor advance is idempotent so duplicate writes are harmless.

**Choose B** — FIFO is overkill and imposes SqsFifo-specific IAM / message-group-id overhead. The duplicate-poll-per-burst-start case is rare and idempotent.

### New Lambda: `snout-spotter-spc-poller`

- Project: `src/lambdas/SnoutSpotter.Lambda.SpcPoller/` — plain event-driven handler (mirror `SnoutSpotter.Lambda.AutoLabel`), SQS trigger, **not** EventBridge cron.
- Handler: reads `BurstMessage { householdId, kind: "motion"|"continue" }` from SQS.
- Uses the shared `SnoutSpotter.Spc.Client` project for HTTP + secrets.
- Duplicated thin wrappers (`HouseholdIntegrationService`, `PetLinkService`) — same decision as before, cost of keeping them in the Lambda is lower than extracting.
- Memory: 256 MB, Timeout: 60 s.

### IngestClip hook

`src/lambdas/SnoutSpotter.Lambda.IngestClip/Function.cs` already processes every uploaded clip and knows the `household_id` (parsed from the S3 key prefix). Add after the existing keyframe extraction:

```
// If this household's Pi has any SPC device links, start/extend a burst poll.
// We don't block on failures — if the SQS send fails we just skip this clip.
if (await _deviceRegistry.HasAnySpcLinksAsync(householdId))
    await _burstQueue.SendStartMessageAsync(householdId);
```

Two new services in the IngestClip Lambda:

- `IDeviceRegistryReader.HasAnySpcLinksAsync(householdId)` — Query `snout-spotter-devices` with `household_id = hh AND begins_with(sk, "link#spc#")`, `Limit=1`. Returns bool. Zero-match Query is cheap.
- `IBurstQueueProducer.SendStartMessageAsync(householdId)` — SQS `SendMessage` with body `{ householdId, kind: "motion" }`. No delay.

The IngestClip Lambda gets new IAM: `dynamodb:Query` on `snout-spotter-devices` and `sqs:SendMessage` on the new burst queue.

**Later swap to detection-gated trigger**: move these two lines from `IngestClip.Function` to `RunInference.Function`, only firing when a detection of a known pet lands. Zero other changes needed.

## Data model

### New DynamoDB table — `snout-spotter-spc-events` (unchanged from original plan)

- PK `household_id` (S), SK `created_at_event` (S) = `{created_at}#{spc_event_id}`.
- No GSI in phase 2. Query by household + optional `FilterExpression` for pet.
- PITR on, RemovalPolicy RETAIN.
- Attributes: `spc_event_id`, `spc_event_type`, `event_category`, `created_at`, `pet_id?`, `spc_pet_id?`, `device_id?`, `raw_data` (verbatim JSON).

### New DynamoDB table — `snout-spotter-spc-burst-state`

One row per household to track the active burst. Small enough that an attribute on `snout-spotter-households.spc_integration` would also work, but a dedicated table keeps it hot-path-safe and easier to expire.

- PK `household_id` (S).
- Attributes:
  - `poll_until` (S, ISO) — deadline after which the chain should die.
  - `last_timeline_id` (N) — cursor for next poll. Nullable; `null` means "seed mode".
  - `last_poll_at` (S, ISO).
- PITR off (ephemeral state), RemovalPolicy DESTROY.
- Optional TTL attribute `ttl_expiry` set to `poll_until + 1h` so old rows self-delete.

### New SQS queue — `snout-spotter-spc-burst`

- Standard queue.
- Visibility timeout: 90 s (poller runs in ≤ 60s).
- Max receive count: 3 → DLQ `snout-spotter-spc-burst-dlq`.
- IAM: IngestClip gets `SendMessage`; SpcPoller gets `SendMessage` (for self-schedule) + standard consumer perms.

### Category decoder (unchanged from original plan)

```csharp
private static string Categorize(int eventType) => eventType switch
{
    >= 20000 and <= 20999 => "feeding",
    >= 22000 and <= 22999 => "drinking",
    20 => "movement",
    >= 21000 and <= 21999 => "movement",
    >= 9000 and <= 19999 => "device_status",
    _ => "other"
};
```

Approximate; users see raw type in a tooltip if they care. Full decoder is a follow-up.

## Poller handler logic

```
handler(BurstMessage msg):
  hh = msg.householdId
  now = utcNow

  burst = GetBurstState(hh)

  // motion trigger — start or extend
  if msg.kind == "motion":
      newDeadline = max(burst?.poll_until ?? now, now) + 10 min
      UpdateBurstState(hh, poll_until = newDeadline)
      burst = burst ?? new { poll_until = newDeadline, last_timeline_id = null }

  // continue trigger — check we're still inside the window
  else if msg.kind == "continue":
      if burst == null or burst.poll_until <= now:
          return   // chain dies

  token = SecretsManager.Get("snoutspotter/spc/{hh}")
  if token == null:                       // household unlinked mid-burst
      return

  state = HouseholdIntegrationService.Get(hh)
  if state == null or state.status != "linked":
      return

  // seed-on-first-motion: first-ever poll just records the cursor
  if burst.last_timeline_id == null:
      latest = SpcApiClient.ListTimeline(token, state.spcHouseholdId, sinceId: null, pageSize: 1)
      if latest.Any():
          UpdateBurstState(hh, last_timeline_id = latest.Max(e => e.id))
      UpdateBurstState(hh, last_poll_at = now)
      MaybeScheduleContinue(hh, burst.poll_until)
      return

  // normal poll
  try:
      page = SpcApiClient.ListTimeline(token, state.spcHouseholdId,
                                        sinceId: burst.last_timeline_id,
                                        pageSize: 50)
      petMap = PetLinkService.GetSpcToInternalMap(hh)
      foreach evt in page:
          WriteEvent(hh, evt, petMap)         // PutItem, idempotent
      if page.Any():
          UpdateBurstState(hh, last_timeline_id = page.Max.id)
      UpdateBurstState(hh, last_poll_at = now)
      HouseholdIntegrationService.SetLastSyncAt(hh, now)
  catch SpcUnauthorizedException:
      HouseholdIntegrationService.MarkTokenExpired(hh)
      return                                   // chain dies; re-link needed
  catch SpcUpstreamException:
      // Transient — let SQS retry (visibility timeout will redeliver).
      throw

  MaybeScheduleContinue(hh, burst.poll_until)

MaybeScheduleContinue(hh, deadline):
  remaining = deadline - utcNow
  if remaining > 30s:
      sqs.SendMessage(body = { householdId: hh, kind: "continue" }, DelaySeconds = 30)
  // else: chain dies naturally
```

## Infra changes (CDK)

### `src/infra/Stacks/CoreStack.cs`

- New DynamoDB table `snout-spotter-spc-events` (as above).
- New DynamoDB table `snout-spotter-spc-burst-state` (as above).
- New SQS queue `snout-spotter-spc-burst` + DLQ.
- New ECR repo `snout-spotter-spc-poller`.
- New SSM params: `/snoutspotter/core/spc-events-table-name`, `/snoutspotter/core/spc-burst-state-table-name`, `/snoutspotter/core/spc-burst-queue-url`.

### `src/infra/Stacks/IngestStack.cs` (edit existing)

- Add `dynamodb:Query` on `snout-spotter-devices`.
- Add `sqs:SendMessage` on `snout-spotter-spc-burst`.
- Env: `DEVICES_TABLE`, `SPC_BURST_QUEUE_URL`.

### New stack `src/infra/Stacks/SpcPollerStack.cs`

Props: `SpcPollerEcrRepo`, `ImageTag`.

- Reads SSM params for households / pets / events / burst-state table names + burst queue URL.
- `DockerImageFunction snout-spotter-spc-poller`, 256 MB, 60 s timeout.
- SQS event source for `snout-spotter-spc-burst` with BatchSize=1 (simpler accounting; we don't benefit from batching because each message is per-household).
- IAM:
  - `secretsmanager:GetSecretValue` on `snoutspotter/spc/*`.
  - `dynamodb:GetItem + UpdateItem` on households, burst-state.
  - `dynamodb:Query` on pets (for pet_id lookup).
  - `dynamodb:PutItem + Query` on spc-events.
  - `sqs:SendMessage` on the burst queue (for self-schedule).

### `src/infra/Program.cs`

Register `SpcPollerStack` after `SpcConnectorStack`; pass `DevicesTable`, `SpcBurstQueue`, `SpcBurstStateTable`, `SpcEventsTable` into IngestStack/SpcConnectorStack props as needed. (Actually — per our existing rule we use SSM lookups between Lambda stacks; only CoreStack hands out `Table`/`Queue`/`Repository` refs directly.)

## Read endpoint + frontend (unchanged from original plan)

- Main API `GET /api/pets/{petId}/spc-events?limit=50&nextPageKey=...` — `SpcEventsService` queries by household + `FilterExpression = "pet_id = :pid"`, newest first.
- `src/api/Controllers/PetsController.cs` — add route.
- `src/api/Services/SpcEventsService.cs` + interface — new.
- `ApiStack`: env `SPC_EVENTS_TABLE` + `dynamodb:Query` on `snout-spotter-spc-events`.
- Frontend `src/web/src/pages/Pets.tsx` — add collapsible Activity section per pet card, lazy-loaded on expand. Icons by category: `Utensils` (feeding), `Droplets` (drinking), `DoorOpen` (movement), `Plug` (device_status), `Circle` (other).

## Cost sketch

Assumption: 20 motion uploads/day, each 10-min burst, some overlap → ~90 min/day active polling per linked household.

- **Polls (SQS + Lambda invokes + SPC GETs)**: 90 min × 2 polls/min = **180 / day / household**.
- **Naive 1-min cron baseline**: **1440 / day / household**.
- Savings: **8× fewer invokes**, proportional DynamoDB cursor-write savings, zero cost during quiet hours.
- SQS messages are free under 1M/month (we'd send ~200/day/household).

## Critical files

- `src/shared/SnoutSpotter.Spc.Client/` — existing, gains `ListTimelineAsync` on `ISpcApiClient` and `SpcTimelineResource` DTO.
- `src/lambdas/SnoutSpotter.Lambda.IngestClip/Function.cs` — two new service calls after keyframe extract.
- `src/lambdas/SnoutSpotter.Lambda.IngestClip/Services/DeviceRegistryReader.cs` **(new)** — minimal Query-projected bool check.
- `src/lambdas/SnoutSpotter.Lambda.IngestClip/Services/BurstQueueProducer.cs` **(new)** — single `SendMessage` wrapper.
- `src/lambdas/SnoutSpotter.Lambda.SpcPoller/` **(new project)** — entire Lambda.
- `src/infra/Stacks/CoreStack.cs` — events + burst-state tables, burst queue + DLQ, new ECR repo, new SSM params.
- `src/infra/Stacks/IngestStack.cs` — add IAM + env for devices table + burst queue.
- `src/infra/Stacks/SpcPollerStack.cs` **(new)**.
- `src/infra/Program.cs` — wire new stack.
- `src/api/Controllers/PetsController.cs` — add `GET /api/pets/{petId}/spc-events`.
- `src/api/Services/SpcEventsService.cs` **(new)**.
- `src/api/Services/Interfaces/ISpcEventsService.cs` **(new)**.
- `src/api/Models/SpcEventDto.cs` **(new)**.
- `src/infra/Stacks/ApiStack.cs` — env + IAM for events table.
- `src/web/src/pages/Pets.tsx` — Activity panel.
- `src/web/src/api.ts` + `src/web/src/types.ts` — event types + fetcher.
- `.github/workflows/` — new `build-spc-poller-image.yml`, `deploy-spc-poller.yml`; edit `deploy.yml`.

## Edge cases

- **Household unlinked mid-burst.** Secret missing → poller returns silently, chain dies.
- **Duplicates.** PutItem with natural PK+SK is idempotent.
- **Unmapped SPC pet.** Persist with `pet_id=null`; surfaces later if user maps. No data loss.
- **Late events after burst closes.** Accepted — next motion picks them up via `SinceId`.
- **Pi without SPC links.** `HasAnySpcLinksAsync` returns false; no queue message sent; zero downstream cost.
- **Hub upload delay > 10 min.** Rare in practice; events are lost until next motion.
- **Transient SPC 5xx / 429.** Polly (existing in shared client) handles short retries. Sustained failure → SQS redelivery up to 3× → DLQ. DLQ inspection is manual and rare.

## Incremental delivery (2 PRs)

1. **PR A — Poller infra + backend.** Shared-client `ListTimelineAsync`, new poller project, new stacks/queues/tables, IngestClip hook, CI. No UI.
2. **PR B — API read endpoint + UI.** Main API `GET /api/pets/{petId}/spc-events` + Pets-page Activity panel.

## Verification

- `dotnet build SnoutSpotter.sln` clean.
- `cdk synth SnoutSpotter-Core SnoutSpotter-Ingest SnoutSpotter-SpcPoller SnoutSpotter-Api` all pass.
- `cd src/web && npm run build`.
- **Beta runbook**:
  1. Link household + a Pi → a SPC device.
  2. Wait quiet — confirm zero poller invocations in CloudWatch.
  3. Trigger a clip upload (motion at the camera) → confirm burst-start SQS message, then a chain of continue messages at 30s intervals.
  4. Feed the dog (or trigger a door) → confirm a row lands in `snout-spotter-spc-events` within ~90s.
  5. Wait 10 min quiet — confirm chain dies, no more invocations.
  6. Token-expiry path: rotate the secret to garbage, next motion → poller catches 401, marks household `token_expired`, chain dies, subsequent motions don't re-poll.
  7. Unmapped SPC pet event → row persisted with `pet_id=null`.

## Future (not in scope here)

- **Detection-gated trigger** — move motion hook from IngestClip to RunInference, fire only on confirmed pet detections. One-file change.
- **Human-readable text** — enrich events by fetching `GET /api/notification` alongside timeline.
- **Feeding-amount reports** — use `GET /api/report/household/.../pet/.../aggregate`.
- **Live "is eating now" status** — separate feature using `GET /api/pet/{petId}/status/{deviceId}`.
