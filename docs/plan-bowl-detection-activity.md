# Bowl Detection & Activity Recognition Plan

## Goal

Extend SnoutSpotter to detect when the dog is eating or drinking from its bowl. The system should:
- Detect food bowls and water bowls in keyframes
- Determine if the dog is interacting with a bowl (eating/drinking)
- Record the activity alongside existing detection data
- Display activity in the dashboard

## Approach: Bowl Detection + Color Classification + Proximity

Use the stock YOLOv8n (COCO) model to detect bowls (class 45), classify bowl type by color analysis (HSV), then check spatial proximity between detected dogs and bowls to infer activity.

This avoids training a new model from scratch and leverages the existing AutoLabel pipeline for automatic training data generation.

---

## Phase 1: Auto-Label Bowls in Existing Keyframes

**Goal:** Automatically detect and label bowls in all existing keyframes to build training data.

### 1.1 Extend AutoLabel Lambda

**File:** `src/lambdas/SnoutSpotter.Lambda.AutoLabel/Function.cs`

Currently detects COCO class 16 (dog) only. Extend to also detect class 45 (bowl).

Changes:
- Add COCO class 45 (`bowl`) to the detection filter alongside class 16 (`dog`)
- For each detected bowl bounding box, crop the region from the original image
- Run HSV color analysis on the cropped region to classify as `food_bowl` or `water_bowl`
- Write bowl labels to the `snout-spotter-labels` table with `auto_label = "food_bowl"` or `"water_bowl"`

### 1.2 Bowl Color Classification Logic

```
Input: cropped bowl bounding box image (RGB)
Output: "food_bowl" | "water_bowl" | "unknown_bowl"

1. Convert crop to HSV color space
2. Create mask excluding very dark (V < 30) and very light (V > 230) pixels (shadows/highlights)
3. Calculate histogram of H (hue) channel on masked pixels
4. Find dominant hue peak

Color mapping (configurable via Lambda environment variables):
  - WATER_BOWL_HUE_MIN=100, WATER_BOWL_HUE_MAX=130  → blue → water_bowl
  - FOOD_BOWL_HUE_MIN=0,   FOOD_BOWL_HUE_MAX=10     → red low range → food_bowl
  - FOOD_BOWL_HUE_MIN2=170, FOOD_BOWL_HUE_MAX2=180   → red high range → food_bowl
    (red wraps around 0/180 in HSV)
  - Anything else → unknown_bowl
```

**Note:** HSV hue in OpenCV uses 0-180 range (not 0-360). Blue is ~100-130, red is ~0-10 and ~170-180.

### 1.3 Environment Variables

Add to AutoLabel Lambda:
```
WATER_BOWL_HUE_MIN=100
WATER_BOWL_HUE_MAX=130
FOOD_BOWL_HUE_MIN=0
FOOD_BOWL_HUE_MAX=10
FOOD_BOWL_HUE_MIN2=170
FOOD_BOWL_HUE_MAX2=180
BOWL_DETECTION_ENABLED=true
BOWL_CONFIDENCE_THRESHOLD=0.3
```

### 1.4 Labels Table Schema Update

Bowl labels use the same `snout-spotter-labels` table:

| Attribute | Value |
|-----------|-------|
| `keyframe_key` (PK) | S3 key of the keyframe |
| `auto_label` | `food_bowl` or `water_bowl` |
| `confirmed_label` | `food_bowl`, `water_bowl`, or `not_bowl` (human review) |
| `confidence` | COCO bowl detection confidence |
| `bounding_boxes` | JSON array of bowl bounding boxes |
| `bowl_color_hue` | Dominant hue value (for debugging) |

---

## Phase 2: Review UI for Bowls

**Goal:** Let users review auto-labeled bowls and correct mistakes.

### 2.1 Labels Page Updates

**File:** `src/web/src/pages/Labels.tsx`

- Add `food_bowl` and `water_bowl` to the filter dropdown options
- Add bowl-specific confirmation buttons (confirm food_bowl, confirm water_bowl, mark as not_bowl)
- Show the detected bowl color hue value for debugging
- Bowl labels don't need breed selection

### 2.2 Label Stats Updates

**File:** `src/api/Services/LabelService.cs`

- Include `food_bowl` and `water_bowl` counts in label stats endpoint
- Add bowl-specific stats: total bowls detected, bowls confirmed, color accuracy

---

## Phase 3: Retrain Custom Detector with Bowl Classes

**Goal:** Add bowl detection to the fine-tuned YOLOv8 model.

### 3.1 Updated Class Mapping

```
Class 0: my_dog
Class 1: other_dog
Class 2: food_bowl
Class 3: water_bowl
```

Output tensor shape changes: `[1, 6, 8400]` → `[1, 8, 8400]`

### 3.2 Export Dataset Updates

**File:** `src/lambdas/SnoutSpotter.Lambda.ExportDataset/Function.cs`

- Include confirmed bowl labels in the exported dataset
- Generate YOLO-format bounding box labels for bowls (class 2, class 3)
- Update `dataset.yaml` to list 4 classes

### 3.3 Training Script Updates

**File:** `src/ml/train_detector.py`

- Update class list: `names: ['my_dog', 'other_dog', 'food_bowl', 'water_bowl']`
- No architectural changes needed — YOLOv8 handles any number of classes
- Recommended: start with existing my_dog/other_dog weights and fine-tune with bowl data added

### 3.4 Verify Script Updates

**File:** `src/ml/verify_onnx.py`

- Update expected class count from 2 to 4
- Add bowl detection verification samples

---

## Phase 4: Activity Inference (Eating/Drinking Detection)

**Goal:** When both a dog and a bowl are detected, determine if the dog is eating/drinking.

### 4.1 Proximity Logic in RunInference Lambda

**File:** `src/lambdas/SnoutSpotter.Lambda.RunInference/Function.cs`

After running YOLO detection on a keyframe:

```
For each detected dog:
  For each detected bowl:
    1. Calculate overlap ratio (IoU) between dog bbox and bowl bbox
    2. Calculate distance from dog bbox bottom-center to bowl bbox center
       (dogs eat with head down — bottom of dog bbox is near the bowl)
    3. If IoU > 0.05 OR distance < (bowl_width * 1.5):
       → Dog is interacting with this bowl
       → Activity = "eating" if food_bowl, "drinking" if water_bowl
    4. Otherwise: activity = "idle"
```

**Proximity thresholds (configurable via env vars):**
```
ACTIVITY_IOU_THRESHOLD=0.05
ACTIVITY_DISTANCE_FACTOR=1.5
```

### 4.2 Data Model Changes

#### Keyframe Detections (clips table)

Add `activity` field to each detection in `keyframe_detections`:

```json
{
  "keyframeKey": "keyframes/2026/04/07/clip_0001.jpg",
  "label": "my_dog",
  "activity": "eating",
  "nearBowl": "food_bowl",
  "detections": [
    {
      "label": "my_dog",
      "confidence": 0.92,
      "activity": "eating",
      "boundingBox": { "x": 100, "y": 50, "width": 200, "height": 300 }
    },
    {
      "label": "food_bowl",
      "confidence": 0.78,
      "boundingBox": { "x": 150, "y": 280, "width": 80, "height": 40 }
    }
  ]
}
```

#### Clip-Level Activity

Add `activity_detected` attribute to clip record:
- `none` — no dog-bowl interaction
- `eating` — dog interacting with food bowl
- `drinking` — dog interacting with water bowl
- `eating_and_drinking` — both detected across keyframes

### 4.3 Detection Type Priority Update

**File:** `src/lambdas/SnoutSpotter.Lambda.RunInference/Function.cs`

Update `UpgradeDetectionType` to consider activity:
```
Priority: none < no_dog < other_dog < my_dog
Activity is orthogonal — stored separately, not as a detection type
```

---

## Phase 5: Dashboard Activity Display

**Goal:** Show eating/drinking activity in the UI.

### 5.1 Clip Detail Page

**File:** `src/web/src/pages/ClipDetail.tsx`

- Show activity badge alongside detection badge: "My Dog - Eating", "My Dog - Drinking"
- Render bowl bounding boxes in a different color (blue for water, red for food)
- Show activity timeline across keyframes

### 5.2 Clips Browser

**File:** `src/web/src/pages/ClipsBrowser.tsx`

- Add activity filter: "All", "Eating", "Drinking", "Idle"
- Show activity icon on clip thumbnails

### 5.3 Dashboard Stats

**File:** `src/web/src/pages/Dashboard.tsx`

- Add "Meals Today" stat card showing eating/drinking event counts
- Could show feeding schedule pattern over time

### 5.4 Detections Page

**File:** `src/web/src/pages/Detections.tsx`

- Add activity column to detection results table
- Filter by activity type

---

## Implementation Order

| Step | Phase | Description | Effort | Dependencies |
|------|-------|-------------|--------|--------------|
| 1 | 1.1 | Extend AutoLabel to detect COCO class 45 (bowl) | 2h | None |
| 2 | 1.2 | Add HSV color classification for bowl type | 2h | Step 1 |
| 3 | 1.3 | Add bowl env vars to AutoLabel Lambda CDK stack | 30m | Step 2 |
| 4 | 1.4 | Run AutoLabel on existing keyframes to generate bowl labels | 1h | Step 3 (deploy) |
| 5 | 2.1 | Add bowl filters and review UI to Labels page | 2h | Step 4 |
| 6 | 2.2 | Update label stats for bowl counts | 1h | Step 5 |
| 7 | — | **Review auto-labeled bowls, confirm/correct ~200 labels** | 2h | Step 5 |
| 8 | 3.2 | Update ExportDataset to include bowl labels | 2h | Step 7 |
| 9 | 3.1 | Retrain YOLOv8 with 4 classes (my_dog, other_dog, food_bowl, water_bowl) | 3h | Step 8 |
| 10 | 3.3 | Update train + verify scripts for 4 classes | 1h | Step 9 |
| 11 | 4.1 | Add proximity/activity logic to RunInference | 3h | Step 9 (deploy model) |
| 12 | 4.2 | Update DynamoDB schema for activity fields | 1h | Step 11 |
| 13 | 5.1-5.4 | Dashboard activity display (clip detail, browser, stats) | 4h | Step 12 |

**Total estimated effort: ~24 hours across 13 steps**

---

## Camera Placement Notes

Bowl detection works best when:
- Camera has a clear overhead or angled view of the bowl area
- Bowls are consistently placed in the same location
- Bowls have distinct, solid colors (blue, red) — patterned bowls are harder
- Lighting is consistent (avoid direct sunlight on bowls which washes out color)

Consider dedicating a Pi camera specifically for the feeding area if the main camera doesn't have a good angle on the bowls.

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Bowl color detection unreliable under variable lighting | Use HSV (separates hue from brightness). Add `unknown_bowl` fallback. Allow manual override in review UI. |
| Dog near bowl but not eating (just walking past) | Require significant bbox overlap (IoU) or head-down position. Tune thresholds after initial deployment. |
| Multiple dogs at bowl simultaneously | Track which dog bbox overlaps which bowl. Attribute activity to closest dog. |
| Bowl partially occluded by dog | COCO bowl detection handles partial occlusion reasonably. Confidence threshold filters weak detections. |
| Food and water bowls same color | Fall back to `unknown_bowl`. User confirms type in review UI. Consider adding a "bowl_position" config (left=food, right=water). |
| Retraining degrades existing my_dog/other_dog accuracy | Validate with test set before deploying. Keep previous model version as rollback via Models page. |

---

## Future Extensions

- **Feeding schedule tracking** — log eating/drinking timestamps, show daily feeding pattern chart
- **Portion monitoring** — detect bowl fullness (empty vs full) using fill-level estimation
- **Alert on missed meals** — notify if dog hasn't eaten by a configurable time
- **Multi-dog feeding** — track which dog ate from which bowl
- **Water level monitoring** — alert when water bowl appears empty
