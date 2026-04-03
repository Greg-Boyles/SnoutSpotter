#!/bin/zsh
# Upload breed images to the SnoutSpotter training API
set -uo pipefail

API_URL="https://4kwmom6fj0.execute-api.eu-west-1.amazonaws.com/api/ml/labels/upload"
IMAGES_DIR="/Users/boylesg/Dev/Images"
TOKEN="$1"
BATCH_SIZE=5
PARALLEL=2
LOG_FILE="/tmp/breed-upload.log"

> "$LOG_FILE"

total_uploaded=0
total_failed=0
breed_count=0
total_breeds=$(ls -d "$IMAGES_DIR"/*/ 2>/dev/null | wc -l | tr -d ' ')

for breed_dir in "$IMAGES_DIR"/*/; do
  breed="${breed_dir%/}"
  breed="${breed##*/}"
  breed_count=$((breed_count + 1))

  all_files=("${(@f)$(find "$breed_dir" -type f \( -name "*.jpg" -o -name "*.jpeg" -o -name "*.png" -o -name "*.JPEG" \) | sort)}")

  if [[ ${#all_files[@]} -eq 0 || -z "${all_files[1]}" ]]; then
    echo "[$breed_count/$total_breeds] $breed: no images, skipping"
    continue
  fi

  encoded_breed=$(python3 -c "import urllib.parse; print(urllib.parse.quote(\"$breed\"))")
  url="${API_URL}?label=other_dog&breed=${encoded_breed}"

  batch_count=0
  batch_success=0
  batch_fail=0
  pids=()

  i=1
  while [[ $i -le ${#all_files[@]} ]]; do
    end=$((i + BATCH_SIZE - 1))
    if [[ $end -gt ${#all_files[@]} ]]; then
      end=${#all_files[@]}
    fi
    batch=("${all_files[$i,$end]}")
    batch_count=$((batch_count + 1))

    (
      curl_args=(-s -o /dev/null -w "%{http_code}" --max-time 120 -X POST -H "Authorization: Bearer $TOKEN")
      for f in "${batch[@]}"; do
        curl_args+=(-F "files=@$f")
      done
      http_code=$(curl "${curl_args[@]}" "$url")
      if [[ "$http_code" != "200" ]]; then
        echo "FAIL [$http_code] breed=$breed batch=$batch_count" >> "$LOG_FILE"
        exit 1
      fi
    ) &
    pids+=($!)

    if [[ ${#pids[@]} -ge $PARALLEL ]]; then
      for pid in "${pids[@]}"; do
        if wait "$pid" 2>/dev/null; then
          batch_success=$((batch_success + 1))
        else
          batch_fail=$((batch_fail + 1))
        fi
      done
      pids=()
    fi

    i=$((end + 1))
  done

  for pid in "${pids[@]}"; do
    if wait "$pid" 2>/dev/null; then
      batch_success=$((batch_success + 1))
    else
      batch_fail=$((batch_fail + 1))
    fi
  done

  success_images=$((batch_success * BATCH_SIZE))
  if [[ $success_images -gt ${#all_files[@]} ]]; then
    success_images=${#all_files[@]}
  fi

  echo "[$breed_count/$total_breeds] $breed: ${#all_files[@]} images, $batch_success/$batch_count batches ok"
  total_uploaded=$((total_uploaded + success_images))
  total_failed=$((total_failed + (batch_fail * BATCH_SIZE)))
done

echo ""
echo "Done! Approx uploaded: $total_uploaded, Failed: $total_failed"
[[ -s "$LOG_FILE" ]] && echo "Failures logged to: $LOG_FILE" || echo "No failures!"
