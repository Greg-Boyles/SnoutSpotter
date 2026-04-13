# Plan: Named Pet Profiles — Replace Hardcoded my_dog/other_dog

## Context

SnoutSpotter currently has a hardcoded binary classification: `my_dog` (class 0) and `other_dog` (class 1). This is baked into the inference Lambda, training pipeline, labels system, API validation, frontend UI, and DynamoDB schema across 47+ locations.

The goal is to replace this with **named pet profiles** — users create pets (e.g. "Biscuit", "Luna") through the dashboard, and labels/detections use pet IDs instead of "my_dog". Each pet becomes a YOLO class. The system stays single-household for now but is designed for future multi-household support.

## Key Design Decisions

- **Pet ID format**: `pet-{slug}-{rand}` (e.g. `pet-biscuit-a3f2`). The `pet-` prefix lets all code distinguish pets from fixed values ("other_dog", "no_dog") with a simple `StartsWith("pet-")` check.
- **"other_dog" stays**: Unknown dogs remain "other_dog". "no_dog" stays for no detection.
- **auto_label stays binary**: COCO AutoLabel still produces "dog"/"no_dog". Only the custom-trained model identifies individual pets. Auto-labeled "dog" entries stay unreviewed until a user assigns them to a specific pet or "other_dog". If only one pet exists, auto-confirm to that pet.
- **class_map.json per model version**: Stored alongside the ONNX model at `models/dog-classifier/versions/{version}/class_map.json`. Written by the training agent after training (extracted from dataset.yaml). Copied to `models/dog-classifier/class_map.json` on model activation, same as `best.onnx`. Format: `{"classes": ["pet-biscuit-a3f2", "pet-luna-b7e1", "other_dog"]}`.
- **Household-ready schema**: Pets table uses composite key `PK: household_id, SK: pet_id` from day one with a hardcoded `default` household. Avoids a DynamoDB migration when multi-household is added later.
- **Migration**: Existing "my_dog" data migrated to the user's first pet profile.

---

## Phases

### Phase 1: Pet Profiles Backend (CDK + API)

New DynamoDB table `snout-spotter-pets` (`PK: household_id`, `SK: pet_id`, PAY_PER_REQUEST) in CoreStack.

**New files:**
- `src/api/Models/PetProfile.cs` — record: HouseholdId, PetId, Name, Breed?, PhotoUrl?, CreatedAt
- `src/api/Services/Interfaces/IPetService.cs` — List, Get, Create, Update, Delete
- `src/api/Services/PetService.cs` — DynamoDB CRUD, slug-based ID generation. `household_id` hardcoded to `"default"` for now. Delete validation: refuse if pet is referenced in the active model's class_map (fetch `models/dog-classifier/class_map.json` from S3 and check).
- `src/api/Controllers/PetsController.cs` — `GET/POST /api/pets`, `PUT/DELETE /api/pets/{petId}`

**Modified files:**
- `src/infra/Stacks/CoreStack.cs` — add PetsTable with composite key
- `src/infra/Stacks/ApiStack.cs` — pass PetsTable, add env var + permissions
- `src/infra/Program.cs` — wire PetsTable
- `src/api/AppConfig.cs` — add PetsTable
- `src/api/Program.cs` — bind env var, register PetService

### Phase 2: Pets Frontend Page

New page for CRUD pet management with profile cards.

**New files:**
- `src/web/src/pages/Pets.tsx` — pet cards with create/edit/delete. Warn on delete if pet is in active model. Show labeled image count per pet.

**Modified files:**
- `src/web/src/types.ts` — add `Pet` interface
- `src/web/src/api.ts` — add pet CRUD methods
- `src/web/src/App.tsx` — add route + nav item (PawPrint icon, under Browse group)

### Phase 3: Generalize Backend Validation

Replace all hardcoded "my_dog" validation with dynamic pet-aware checks.

**Modified files:**
- `src/api/Controllers/LabelsController.cs` — inject IPetService, replace fixed validation:
  - `UpdateLabel`, `BulkConfirm`: accept any `pet-*` ID (validate exists in pets table) + "other_dog" + "no_dog"
  - `BackfillBoxes`: accept any `pet-*` + "other_dog" (not just "my_dog"/"other_dog")
  - `UploadTrainingImages`: change default label from `"my_dog"` to require explicit pet_id. Accept `pet-*` + "other_dog" + "no_dog"
- `src/api/Services/LabelService.cs`:
  - `GetStatsAsync` — return per-pet counts dynamically instead of hardcoded myDog/otherDog. Fetch pet list from IPetService, query `by-confirmed-label` GSI for each pet_id.
  - `CountConfirmedLabelsAsync` — init dictionaries dynamically from pet list, count `pet-*` entries
  - `UpdateLabelAsync` — auto_label mapping: any `pet-*` or "other_dog" → "dog", "no_dog" → "no_dog"
  - `BackfillBoundingBoxesAsync` — query pet list instead of hardcoded `["my_dog", "other_dog"]`
- `src/api/Services/ClipService.cs` — `GetDetectionsAsync`: query all pet_ids + "other_dog" via `by-detection` GSI in parallel. For N pets this is N+1 parallel queries — acceptable for household-scale (<10 pets). Merge and sort client-side as before.
- `src/api/Controllers/StatsController.cs` — replace `MyDogDetections` with `KnownPetDetections` + per-pet breakdown
- `src/api/Models/DashboardStats.cs` — replace `int MyDogDetections` with `int KnownPetDetections` + `Dictionary<string, int>? PetDetectionCounts`

### Phase 4: Frontend Adaptation

Update all pages to use dynamic pet names. Create a `usePets()` hook to fetch and cache the pet list once.

**New files:**
- `src/web/src/hooks/usePets.ts` — fetches pet list, caches in state, provides name lookup helper

**Modified files:**
- `src/web/src/types.ts` — update StatsOverview (knownPetDetections, petDetectionCounts)
- `src/web/src/pages/Dashboard.tsx` — "Known Pets" stat card with per-pet counts
- `src/web/src/pages/ClipsBrowser.tsx` — dynamic DETECTION_OPTIONS from pet list
- `src/web/src/pages/Detections.tsx` — dynamic pet filters
- `src/web/src/pages/Labels.tsx` — dynamic filter tabs, review buttons per pet, bulk confirm per pet. Upload picker: pet selector instead of hardcoded "my_dog" default.
- `src/web/src/pages/LabelDetail.tsx` — pet picker buttons instead of "My Dog"/"Other Dog". Render one button per pet + "Other Dog" + "No Dog".
- `src/web/src/components/LabelBadge.tsx` — dynamic colors: any `pet-*` = green (with pet name from lookup), "other_dog" = orange
- `src/web/src/pages/TrainingExports.tsx` — per-pet counts instead of my_dog/other_dog
- `src/web/src/pages/ClipDetail.tsx` — pet name in detection badges
- `src/web/src/pages/SubmitTraining.tsx` — per-pet counts in export manifest display

### Phase 5: Inference + Training Pipeline

Make RunInference, ExportDataset, and the training agent handle dynamic class names.

**Modified files:**
- `src/lambdas/SnoutSpotter.Lambda.RunInference/Function.cs`:
  - Remove hardcoded `ClassNames` array (line 20)
  - In `EnsureModelLoaded`: also load `class_map.json` from S3 (`models/dog-classifier/class_map.json`). Parse into `string[]` for class name lookup. Cache alongside `_session`.
  - `UpgradeDetectionType`: replace hardcoded priority dict. Use: `StartsWith("pet-")` → priority 3, "other_dog" → 2, "no_dog"/"none"/"pending" → 1/0.
  - Line 293: use loaded class names instead of `ClassNames[bestClassIdx]`
  - **Cache invalidation**: `_session` is cached per Lambda instance. On cold start, the latest `best.onnx` + `class_map.json` are loaded. Warm instances use stale data until they recycle — this is acceptable (same as current model caching behavior). No change needed.
- `src/lambdas/SnoutSpotter.Lambda.ExportDataset/Function.cs`:
  - Read pets table to build class mapping: class 0..N-1 = pet_ids sorted by created_at, class N = "other_dog"
  - Generate dynamic `dataset.yaml` with actual pet names
  - Write `class_map.json` into the export ZIP alongside `dataset.yaml` and `manifest.json`. The training agent extracts and uploads it with the model.
  - Per-pet counts in export manifest and DynamoDB export record (replace my_dog_count/not_my_dog_count)
  - Replace hardcoded `var classId = label.ConfirmedLabel == "my_dog" ? 0 : 1` with dynamic lookup from class mapping
- `src/infra/Stacks/ExportDatasetStack.cs` — add PetsTable access + env var
- `src/training-agent/SnoutSpotter.TrainingAgent/JobRunner.cs`:
  - After training completes, read `dataset.yaml` from the dataset dir to extract class names
  - Upload `class_map.json` to S3 alongside the ONNX model at `models/dog-classifier/versions/{version}/class_map.json`
  - Use extracted class names in `TrainingResult.Classes` instead of hardcoded `["my_dog", "other_dog"]`
- `src/api/Controllers/LabelsController.cs` — `ActivateModel`: in addition to copying `best.onnx`, also copy `versions/{version}/class_map.json` to `models/dog-classifier/class_map.json`
- `src/ml/verify_onnx.py` — load class_map.json instead of hardcoded `CLASS_NAMES`/`EXPECTED_NUM_CLASSES`
- `src/ml/train_detector.py` — update summary display for per-pet counts

### Phase 6: Data Migration

One-time migration of existing "my_dog" data to the first pet profile.

**Modified files:**
- `src/api/Controllers/PetsController.cs` — add `POST /api/pets/migrate`
- `src/api/Services/PetService.cs` — add `MigrateLegacyLabelsAsync(petId)`:
  - Query labels via `by-confirmed-label` GSI where confirmed_label = "my_dog" → update to petId (batch writes, 25 per batch)
  - Query clips via `by-detection` GSI where detection_type = "my_dog" → update to petId
  - For `keyframe_detections` list entries: read full clip item, replace "my_dog" labels in-memory, write back. Must handle concurrent updates (use conditional writes with version counter or just accept last-writer-wins for migration).
  - Return count of migrated records
- `src/web/src/pages/Pets.tsx` — migration wizard when no pets exist + legacy "my_dog" data detected. Prompt: "Create your first pet profile and migrate existing data."

### Phase 7: Documentation

- `AGENTS.md` — update schema, endpoints, gotchas, architecture diagram
- Remove/replace all remaining "my_dog" string literals across codebase

---

## Implementation Order

```
Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5 → Phase 6 → Phase 7
```

Each phase is independently committable and deployable. Backward compatibility maintained throughout — existing "my_dog" data works until migrated in Phase 6. Between Phase 4 (frontend uses pet_ids) and a retrained model, the existing model still outputs "my_dog"/"other_dog" — the migration (Phase 6) and first retrain resolve this naturally.

## Verification

- `dotnet build SnoutSpotter.sln` — 0 errors after each phase
- `npm run build` from `src/web/` — clean build after each phase
- Phase 1: `POST /api/pets` creates pet with `household_id=default`, `GET /api/pets` lists it. Delete refused if pet in active class_map.
- Phase 3: `PUT /api/ml/labels/{key}` accepts pet_id as confirmed_label
- Phase 5: ExportDataset writes class_map.json in ZIP, training agent uploads it alongside ONNX, model activation copies both best.onnx + class_map.json
- Phase 6: Migration converts all "my_dog" → pet_id in labels + clips tables
- Phase 7: `grep -r "my_dog" src/` returns only migration/compatibility code

## Impact Summary

**47+ locations** across the codebase reference the hardcoded binary classification:
- `"my_dog"` — 47+ occurrences (C#, TypeScript, Python)
- `"other_dog"` — 32+ occurrences
- `"no_dog"` — 28+ occurrences
- Hardcoded class arrays: `RunInference/Function.cs:20`, `JobRunner.cs:164`
- Fixed validation: `LabelsController.cs` (4 endpoints)
- Hardcoded stats: `LabelService.cs`, `StatsController.cs`, `DashboardStats.cs`
- Fixed filter options: `ClipsBrowser.tsx`, `Detections.tsx`, `Labels.tsx`
- Fixed badge logic: `LabelBadge.tsx`, `Dashboard.tsx`, `ClipDetail.tsx`
- Dataset export: `ExportDataset/Function.cs` (9 locations)
- Detection queries: `ClipService.cs:144-154` (hardcoded "my_dog"/"other_dog" GSI queries)

## Risks

- **Retraining required when pets change** — adding/removing a pet changes YOLO class count. UI should warn clearly and guide user through export → retrain → activate flow. Show a banner: "Pet list changed since last training — retrain to detect [new pet name]."
- **Class ordering stability** — class_map.json must match the model's class indices. Guard against pet deletion invalidating the active model: PetService.DeleteAsync checks the active class_map and refuses deletion if the pet is referenced. The model activation step copies class_map.json atomically with best.onnx.
- **Migration volume** — thousands of labels may take minutes. Migration endpoint should batch DynamoDB updates (25 per BatchWriteItem) and return progress. `keyframe_detections` list updates require read-modify-write per clip item — estimate ~2 seconds per 100 clips.
- **GSI compatibility** — `by-detection` and `by-confirmed-label` GSIs use string PKs. New pet_id values automatically index without schema changes. No DynamoDB migration needed.
- **Auto-label ambiguity** — with multiple pets, auto-labeled "dog" entries can't auto-assign to a specific pet. They stay as unreviewed "dog" until the user assigns a pet. Exception: if only one pet exists, auto-confirm to that pet. This changes the workflow from "auto-label + confirm" to "auto-label + assign pet" for multi-pet households.
- **RunInference cache staleness** — Lambda instances cache the model + class_map on cold start. After model activation, warm instances serve stale predictions until they recycle (typically within minutes for low-traffic functions). This is existing behavior and acceptable — no change needed.
- **N+1 detection queries** — `ClipService.GetDetectionsAsync` queries each pet_id separately via the `by-detection` GSI. With N pets, that's N+1 parallel DynamoDB queries. Acceptable for household-scale (<10 pets). If this becomes a bottleneck, add a GSI with a compound partition key (`detection_group#date`) but don't optimize prematurely.

## Future: Multi-Household

The pets table schema (`PK: household_id, SK: pet_id`) is designed for multi-household from day one. When households are added:
- Labels, clips, exports, and training jobs gain a `household_id` attribute
- Each household trains its own models from its own labeled data
- Training agent job messages include `household_id` for dataset/model scoping
- S3 paths gain a household prefix: `models/{household_id}/dog-classifier/...`
- Auth middleware extracts household_id from the user's token
- Existing `default` household data migrates to the first real household
