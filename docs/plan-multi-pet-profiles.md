# Plan: Named Pet Profiles — Replace Hardcoded my_dog/other_dog

## Context

SnoutSpotter currently has a hardcoded binary classification: `my_dog` (class 0) and `other_dog` (class 1). This is baked into the inference Lambda, training pipeline, labels system, API validation, frontend UI, and DynamoDB schema across 47+ locations.

The goal is to replace this with **named pet profiles** — users create pets (e.g. "Biscuit", "Luna") through the dashboard, and labels/detections use pet IDs instead of "my_dog". Each pet becomes a YOLO class. The system stays single-household.

## Key Design Decisions

- **Pet ID format**: `pet-{slug}-{rand}` (e.g. `pet-biscuit-a3f2`). The `pet-` prefix lets all code distinguish pets from fixed values ("other_dog", "no_dog") with a simple `StartsWith("pet-")` check.
- **"other_dog" stays**: Unknown dogs remain "other_dog". "no_dog" stays for no detection.
- **auto_label stays binary**: COCO AutoLabel still produces "dog"/"no_dog". Only the custom-trained model identifies individual pets.
- **class_map.json**: Stored in S3 alongside the model (`models/dog-classifier/class_map.json`). Written during dataset export, read by RunInference at model load. Format: `{"classes": ["pet-biscuit-a3f2", "pet-luna-b7e1", "other_dog"]}`.
- **Migration**: Existing "my_dog" data migrated to the user's first pet profile.

---

## Phases

### Phase 1: Pet Profiles Backend (CDK + API)

New DynamoDB table `snout-spotter-pets` (PK: `pet_id`, PAY_PER_REQUEST) in CoreStack.

**New files:**
- `src/api/Models/PetProfile.cs` — record: PetId, Name, Breed?, PhotoUrl?, CreatedAt
- `src/api/Services/Interfaces/IPetService.cs` — List, Get, Create, Update, Delete
- `src/api/Services/PetService.cs` — DynamoDB CRUD, slug-based ID generation
- `src/api/Controllers/PetsController.cs` — `GET/POST /api/pets`, `PUT/DELETE /api/pets/{petId}`

**Modified files:**
- `src/infra/Stacks/CoreStack.cs` — add PetsTable
- `src/infra/Stacks/ApiStack.cs` — pass PetsTable, add env var + permissions
- `src/infra/Program.cs` — wire PetsTable
- `src/api/AppConfig.cs` — add PetsTable
- `src/api/Program.cs` — bind env var, register PetService

### Phase 2: Pets Frontend Page

New page for CRUD pet management with profile cards.

**New files:**
- `src/web/src/pages/Pets.tsx` — pet cards with create/edit/delete

**Modified files:**
- `src/web/src/types.ts` — add `Pet` interface
- `src/web/src/api.ts` — add pet CRUD methods
- `src/web/src/App.tsx` — add route + nav item (PawPrint icon)

### Phase 3: Generalize Backend Validation

Replace all hardcoded "my_dog" validation with dynamic pet-aware checks.

**Modified files:**
- `src/api/Controllers/LabelsController.cs` — inject IPetService, replace fixed validation with dynamic: accept any pet_id + "other_dog" + "no_dog"
- `src/api/Services/LabelService.cs`:
  - `GetStatsAsync` — return per-pet counts instead of hardcoded myDog/otherDog
  - `CountConfirmedLabelsAsync` — dynamic counting, any `pet-*` value accumulated
  - `UpdateLabelAsync` — `auto_label` mapping: pet_id or "other_dog" → "dog"
  - `BackfillBoundingBoxesAsync` — dynamic pet list instead of `["my_dog", "other_dog"]`
- `src/api/Services/ClipService.cs` — `GetDetectionsAsync`: query all pet_ids + "other_dog" via by-detection GSI
- `src/api/Controllers/StatsController.cs` — replace `MyDogDetections` with `KnownPetDetections` + per-pet breakdown
- `src/api/Models/DashboardStats.cs` — replace `int MyDogDetections` with `int KnownPetDetections` + `Dictionary<string, int>? PetDetectionCounts`

### Phase 4: Frontend Adaptation

Update all pages to use dynamic pet names. Create a `usePets()` hook to fetch and cache the pet list once.

**Modified files:**
- `src/web/src/types.ts` — update StatsOverview (knownPetDetections, petDetectionCounts)
- `src/web/src/pages/Dashboard.tsx` — "Known Pets" stat card with per-pet counts
- `src/web/src/pages/ClipsBrowser.tsx` — dynamic DETECTION_OPTIONS from pet list
- `src/web/src/pages/Detections.tsx` — dynamic pet filters
- `src/web/src/pages/Labels.tsx` — dynamic filter tabs, review buttons per pet, bulk confirm per pet, upload picker per pet
- `src/web/src/pages/LabelDetail.tsx` — review buttons per pet instead of "My Dog"/"Other Dog"
- `src/web/src/components/LabelBadge.tsx` — dynamic colors: `pet-*` = green, "other_dog" = orange
- `src/web/src/pages/TrainingExports.tsx` — per-pet counts
- `src/web/src/pages/ClipDetail.tsx` — pet name in detection badges

### Phase 5: Inference + Training Pipeline

Make RunInference and ExportDataset read class names dynamically.

**Modified files:**
- `src/lambdas/SnoutSpotter.Lambda.RunInference/Function.cs`:
  - Remove hardcoded `ClassNames` array (line 20)
  - Load `class_map.json` from S3 alongside model in `EnsureModelLoaded`
  - Dynamic `UpgradeDetectionType`: pet-* = priority 3, other_dog = 2, no_dog = 1
- `src/lambdas/SnoutSpotter.Lambda.ExportDataset/Function.cs`:
  - Read pets table to build class mapping
  - Generate dynamic `dataset.yaml` with pet names as classes
  - Write `class_map.json` to S3 at `models/dog-classifier/class_map.json`
  - Per-pet counts in export manifest and DynamoDB export record
  - Replace hardcoded `var classId = label.ConfirmedLabel == "my_dog" ? 0 : 1` with dynamic lookup
- `src/infra/Stacks/ExportDatasetStack.cs` — add PetsTable access + env var
- `src/ml/verify_onnx.py` — load class_map.json instead of hardcoded `CLASS_NAMES`/`EXPECTED_NUM_CLASSES`
- `src/ml/train_detector.py` — update summary display for per-pet counts

### Phase 6: Data Migration

One-time migration of existing "my_dog" data to the first pet profile.

**Modified files:**
- `src/api/Controllers/PetsController.cs` — add `POST /api/pets/migrate`
- `src/api/Services/PetService.cs` — add `MigrateLegacyLabelsAsync(petId)`:
  - Query labels via `by-confirmed-label` GSI where confirmed_label = "my_dog" → update to petId
  - Query clips via `by-detection` GSI where detection_type = "my_dog" → update to petId
  - Update `keyframe_detections` list entries where label = "my_dog" → petId
  - Return count of migrated records
- `src/web/src/pages/Pets.tsx` — migration wizard when no pets exist + legacy "my_dog" data detected

### Phase 7: Documentation

- `AGENTS.md` — update schema, endpoints, gotchas, architecture diagram
- Remove/replace all remaining "my_dog" string literals across codebase

---

## Implementation Order

```
Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5 → Phase 6 → Phase 7
```

Each phase is independently committable and deployable. Backward compatibility maintained throughout — existing "my_dog" data works until migrated in Phase 6.

## Verification

- `dotnet build SnoutSpotter.sln` — 0 errors after each phase
- `npm run build` from `src/web/` — clean build after each phase
- Phase 1: `POST /api/pets` creates pet, `GET /api/pets` lists it
- Phase 3: `PUT /api/ml/labels/{key}` accepts pet_id as confirmed_label
- Phase 5: ExportDataset writes class_map.json with pet names, RunInference loads it
- Phase 6: Migration converts all "my_dog" → pet_id in labels + clips tables
- Phase 7: `grep -r "my_dog" src/` returns only migration/compatibility code

## Impact Summary

**47+ locations** across the codebase reference the hardcoded binary classification:
- `"my_dog"` — 47+ occurrences (C#, TypeScript, Python)
- `"other_dog"` — 32+ occurrences
- `"no_dog"` — 28+ occurrences
- Hardcoded class arrays in RunInference, verify_onnx.py
- Fixed validation in LabelsController (3 endpoints)
- Hardcoded stats in LabelService, StatsController, DashboardStats
- Fixed filter options in ClipsBrowser, Detections, Labels pages
- Fixed badge logic in LabelBadge, Dashboard, ClipDetail

## Risks

- **Retraining required when pets change** — adding/removing a pet changes YOLO class count. UI should warn clearly and guide user through export → retrain → activate flow.
- **Class ordering stability** — class_map.json must match the model's class indices. Guard against pet deletion invalidating the active model (refuse delete if pet is in active class_map).
- **Migration volume** — thousands of labels may take minutes. Migration endpoint should batch updates and return progress.
- **GSI compatibility** — `by-detection` and `by-confirmed-label` GSIs use string PKs. New pet_id values automatically index without schema changes. No DynamoDB migration needed.
