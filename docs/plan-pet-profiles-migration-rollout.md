# Plan: Multi-Pet Profiles Migration Rollout

## Context

The `feat/multi-pet-profiles` branch replaces the hardcoded `my_dog`/`other_dog` classification with named pet profiles. The code ships with `my_dog` backward-compat shims everywhere, so deploying is safe on its own — but the existing `my_dog`-labelled data in DynamoDB and the existing trained model still emit the legacy labels until an explicit migration is run.

This doc covers the one-time rollout against production data.

## Pre-flight state

- All hot paths have `my_dog` fallbacks (see AGENTS.md gotcha #43): RunInference, `ClipService.GetDetectionsAsync`, `LabelService.GetStatsAsync`, `StatsRefreshService`, `ExportDataset.BuildClassMapAsync` (maps `my_dog` → pet index 0), frontend renderers.
- `models/dog-classifier/class_map.json` does not yet exist in S3. RunInference's `LoadClassMapAsync` returns the fallback `["my_dog", "other_dog"]`.
- `snout-spotter-pets` table is empty.
- All `confirmed_label = "my_dog"` labels and `detection_type = "my_dog"` clips are still tagged as such.

## Rollout steps

### 1. Merge and deploy

Merge `feat/multi-pet-profiles` → `main`. `deploy.yml` picks up:
- CDK (`snout-spotter-pets` table, env vars on API + ExportDataset + StatsRefresh lambdas)
- API image (pets controller, migration endpoint, generalised validation)
- RunInference image (dynamic class_map loading with fallback)
- ExportDataset image (class_map.json in ZIP, pet_counts in manifest)
- Web (pets page, usePets hook, dynamic filters)

**Verification:** existing clips still render correctly, existing inference keeps producing `my_dog`, `/pets` loads empty.

### 2. Create the first pet profile

On the dashboard:
- Navigate to `/pets`
- Click **Add Pet** → name = "Biscuit" (or whatever), breed = "Labrador Retriever"
- Confirm the pet card shows `pet-biscuit-xxxx`

At this point the pets table has one row; nothing else has changed yet.

### 3. Run the migration (first pass)

On the pet card, click **Migrate my_dog data**. This calls `POST /api/pets/migrate` with the pet ID and, via `PetService.MigrateLegacyLabelsAsync`:
- Queries labels via `by-confirmed-label` GSI where `confirmed_label = "my_dog"`, UpdateItem each → `confirmed_label = petId`
- Queries clips via `by-detection` GSI where `detection_type = "my_dog"`, for each clip read-modify-write the full item — rewrites top-level `detection_type` + every `keyframe_detections[*].label` and `keyframe_detections[*].detections[*].label` occurrence

UI shows `Migration complete: N labels and M clips updated`. Idempotent — re-running finds nothing and is a no-op.

**Verification:**
- `/labels` filter for "Biscuit" shows the previously `my_dog` labels
- `/clips` detection filter for "Biscuit" shows the previously `my_dog` clips
- Dashboard "Known Pets" stat starts populating

### 4. Export a fresh dataset

On `/training-exports`, click **New Export** → detection type, default config. The export Lambda now:
- Reads the pets table, builds `class_map = ["pet-biscuit-xxxx", "other_dog"]`
- Writes `dataset.yaml` (`names: {0: pet-biscuit-xxxx, 1: other_dog}`) and `class_map.json` into the ZIP
- Records `pet_counts: {"pet-biscuit-xxxx": N, "other_dog": M}` on the export row

### 5. Train + activate a new model

Submit a training job from the new export on `/training`. When it completes, `JobRunner` uploads both `best.onnx` and `class_map.json` to `models/dog-classifier/versions/{version}/`.

On `/models`, click **Activate** on the new version. `ModelService.ActivateModel` copies `best.onnx` AND `class_map.json` to `models/dog-classifier/best.onnx` + `models/dog-classifier/class_map.json` atomically.

**Verification:** RunInference cold-starts load the new `class_map.json`. Any new clip triggers inference that now outputs `pet-biscuit-xxxx` instead of `my_dog`.

### 6. Run the migration (second pass)

Between steps 3 and 5 the old model was still active, so any clips ingested during that window got tagged `my_dog` again. Click **Migrate my_dog data** once more to clean them up.

**Verification:** `grep` production data (or query `by-detection` / `by-confirmed-label` GSI for `my_dog`) returns zero rows.

### 7. (Optional) Clear the stats cache

The stats cache may still hold `my_dog_detections`. Either wait 5 minutes for the stale-while-revalidate refresh to overwrite it, or force a refresh by reading `/api/stats` (triggers async refresh if stale).

## Alternative: skip retraining (single-pet only)

If you only ever plan to have one pet for now and don't want to retrain just to relabel the model output, you can shortcut steps 4–5 by hand-writing a `class_map.json` for the *existing* model:

```json
["pet-biscuit-xxxx", "other_dog"]
```

Upload to `s3://snout-spotter-{account}/models/dog-classifier/class_map.json`. RunInference loads this at next cold start and starts emitting the pet ID without a retrain. The old 2-class model still predicts 2 classes — we're just relabelling the output indices.

**Constraints:**
- Only works while the pet count matches the model's class count (1 pet + other_dog = 2 classes = the old `my_dog`/`other_dog` model's output shape).
- Adding a second pet breaks this shortcut — you must retrain.
- Do it AFTER the first migration (step 3) so the class-0 label in the model matches the pet ID you migrated to.

## Rollback

If migration goes wrong mid-pass:
- Labels: rewrite `confirmed_label` back to `my_dog` via DynamoDB console or a one-off script. There's no transactional boundary around migration — partial state is expected on failure.
- Clips: same, but you lose the nested `keyframe_detections` rewrite unless you have a backup. Recommend taking a point-in-time recovery snapshot of both tables before step 3.
- Pet row: delete via `/pets` (will refuse if pet is in active `class_map.json` — manually delete the class_map first).
- Active model: `/models` → re-activate previous version.

## Gotchas

- **Stats cache staleness.** `snout-spotter-stats` has `my_dog_detections` baked in; the `StatsRefreshService` fallback treats it as `known_pet_detections` until the next refresh overwrites it. Expect a transient period where counts look slightly off.
- **Warm RunInference instances** hold the old model + class_map in memory until they recycle. New predictions may emit `my_dog` for a few minutes after activation. Idempotent second migration (step 6) handles this.
- **Exports table legacy fields** (`my_dog_count`, `not_my_dog_count`) stay on pre-migration export records forever. `TrainingExports.tsx` handles both shapes — don't retroactively rewrite them.
- **Pet deletion is blocked** while the pet appears in the active `class_map.json`. To remove a pet: retrain without it, activate, then delete.
- **Migration volume.** With thousands of labels/clips the endpoint may take minutes. The migration uses individual UpdateItem calls (labels have many attributes to preserve); budget ~2 seconds per 100 clips. API Gateway's 30-second timeout is the ceiling — if migration exceeds that, split the work by running multiple times (idempotent).

## Done when

- `by-confirmed-label` GSI query for `my_dog` returns 0 items
- `by-detection` GSI query for `my_dog` returns 0 items
- `models/dog-classifier/class_map.json` exists in S3 and contains the pet IDs
- Dashboard "Known Pets" count matches expectation
- New clips auto-tag with pet IDs (verify by letting a clip through the pipeline post-activation)
