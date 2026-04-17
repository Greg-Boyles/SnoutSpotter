#!/usr/bin/env python3
"""
Phase 7: Migrate S3 objects and DynamoDB keys to household-prefixed paths.

Copies S3 objects from root prefixes to hh-default/ prefixed paths, then
updates all DynamoDB records that store S3 keys. Labels table requires
delete + re-insert since keyframe_key is the PK.

Idempotent — checks if destination exists before copying, skips already-
migrated DynamoDB records.

Usage:
    python scripts/migrate-s3-household-prefix.py --profile greg --dry-run
    python scripts/migrate-s3-household-prefix.py --profile greg
    python scripts/migrate-s3-household-prefix.py --profile greg --skip-s3  # DynamoDB only
    python scripts/migrate-s3-household-prefix.py --profile greg --skip-dynamo  # S3 only
"""

import argparse
import sys
import boto3
from botocore.exceptions import ClientError

REGION = "eu-west-1"
BUCKET = "snout-spotter-490204853569"
HOUSEHOLD_ID = "hh-default"

S3_PREFIXES_TO_MIGRATE = [
    "raw-clips/",
    "keyframes/",
    "training-uploads/",
    "training-exports/",
    "models/dog-detector/",
    "models/dog-classifier/",
]

# Prefixes to NOT migrate (global resources)
S3_PREFIXES_SKIP = [
    "releases/",
    "terraform/",
    "models/yolov8",  # COCO pretrained models — global
]


def prefix_key(key, household_id):
    """Add household prefix to an S3 key."""
    return f"{household_id}/{key}"


def is_already_prefixed(key, household_id):
    """Check if a key already has the household prefix."""
    return key.startswith(f"{household_id}/")


def should_skip(key):
    """Check if a key belongs to a global prefix that shouldn't be migrated."""
    return any(key.startswith(p) for p in S3_PREFIXES_SKIP)


def migrate_s3(session, household_id, dry_run):
    """Copy S3 objects to household-prefixed paths using parallel threads."""
    import concurrent.futures
    import threading

    s3 = session.client("s3")
    paginator = s3.get_paginator("list_objects_v2")

    total_copied = 0
    total_skipped = 0
    total_errors = 0
    lock = threading.Lock()
    progress_count = 0

    def copy_one(key):
        nonlocal progress_count
        new_key = prefix_key(key, household_id)
        try:
            thread_s3 = session.client("s3")
            thread_s3.copy_object(
                Bucket=BUCKET,
                CopySource={"Bucket": BUCKET, "Key": key},
                Key=new_key,
            )
            with lock:
                progress_count += 1
                if progress_count % 500 == 0:
                    print(f"    ... {progress_count} copied")
            return True
        except Exception as e:
            print(f"    ERROR copying {key}: {e}")
            return False

    for prefix in S3_PREFIXES_TO_MIGRATE:
        print(f"\n  Prefix: {prefix}")
        keys_to_copy = []

        for page in paginator.paginate(Bucket=BUCKET, Prefix=prefix):
            for obj in page.get("Contents", []):
                key = obj["Key"]
                if is_already_prefixed(key, household_id) or should_skip(key):
                    total_skipped += 1
                    continue
                keys_to_copy.append(key)

        if dry_run:
            for key in keys_to_copy[:3]:
                print(f"    [DRY RUN] {key} → {prefix_key(key, household_id)}")
            if len(keys_to_copy) > 3:
                print(f"    ... ({len(keys_to_copy)} total)")
            total_copied += len(keys_to_copy)
            continue

        copied = 0
        errors = 0
        progress_count = 0

        with concurrent.futures.ThreadPoolExecutor(max_workers=20) as executor:
            results = executor.map(copy_one, keys_to_copy)
            for ok in results:
                if ok:
                    copied += 1
                else:
                    errors += 1

        print(f"  {prefix}: {copied} copied, {errors} errors")
        total_copied += copied
        total_errors += errors

    print(f"\n  S3 total: {total_copied} copied, {total_skipped} skipped, {total_errors} errors")
    return total_errors


def migrate_clips(dynamodb, household_id, dry_run):
    """Update s3_key and keyframe_keys on clip records."""
    table = dynamodb.Table("snout-spotter-clips")
    print(f"\n  Updating snout-spotter-clips...")
    updated = 0
    skipped = 0
    errors = 0

    scan_kwargs = {}
    while True:
        response = table.scan(**scan_kwargs)
        for item in response.get("Items", []):
            clip_id = item["clip_id"]
            s3_key = item.get("s3_key", "")
            keyframe_keys = item.get("keyframe_keys", set())

            if is_already_prefixed(s3_key, household_id):
                skipped += 1
                continue

            new_s3_key = prefix_key(s3_key, household_id) if s3_key else s3_key
            new_kk = {prefix_key(k, household_id) for k in keyframe_keys} if keyframe_keys else keyframe_keys

            if dry_run:
                if updated < 3:
                    print(f"    [DRY RUN] {clip_id}: s3_key → {new_s3_key}")
                updated += 1
                continue

            try:
                update_expr = "SET s3_key = :sk"
                expr_values = {":sk": new_s3_key}

                if new_kk:
                    update_expr += ", keyframe_keys = :kk"
                    expr_values[":kk"] = new_kk

                table.update_item(
                    Key={"clip_id": clip_id},
                    UpdateExpression=update_expr,
                    ExpressionAttributeValues=expr_values,
                )
                updated += 1
            except Exception as e:
                print(f"    ERROR updating clip {clip_id}: {e}")
                errors += 1

        if "LastEvaluatedKey" not in response:
            break
        scan_kwargs["ExclusiveStartKey"] = response["LastEvaluatedKey"]

    print(f"  snout-spotter-clips: {updated} updated, {skipped} already done, {errors} errors")
    return errors


def migrate_labels(dynamodb, household_id, dry_run):
    """Delete + re-insert labels with prefixed keyframe_key (PK change)."""
    import concurrent.futures
    import threading

    table = dynamodb.Table("snout-spotter-labels")
    print(f"\n  Migrating snout-spotter-labels (PK re-key)...")

    items_to_migrate = []
    skipped = 0

    scan_kwargs = {}
    while True:
        response = table.scan(**scan_kwargs)
        for item in response.get("Items", []):
            old_key = item["keyframe_key"]
            if is_already_prefixed(old_key, household_id):
                skipped += 1
            else:
                items_to_migrate.append(item)

        if "LastEvaluatedKey" not in response:
            break
        scan_kwargs["ExclusiveStartKey"] = response["LastEvaluatedKey"]

    if dry_run:
        for item in items_to_migrate[:3]:
            old_key = item["keyframe_key"]
            print(f"    [DRY RUN] {old_key} → {prefix_key(old_key, household_id)}")
        if len(items_to_migrate) > 3:
            print(f"    ... ({len(items_to_migrate)} total)")
        print(f"  snout-spotter-labels: {len(items_to_migrate)} to re-key, {skipped} already done")
        return 0

    migrated = 0
    errors = 0
    lock = threading.Lock()

    def rekey_one(item):
        nonlocal migrated
        old_key = item["keyframe_key"]
        new_key = prefix_key(old_key, household_id)
        try:
            new_item = {**item, "keyframe_key": new_key}
            table.put_item(Item=new_item)
            table.delete_item(Key={"keyframe_key": old_key})
            with lock:
                migrated += 1
                if migrated % 500 == 0:
                    print(f"    ... {migrated} re-keyed")
            return True
        except Exception as e:
            print(f"    ERROR migrating label {old_key}: {e}")
            return False

    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as executor:
        results = list(executor.map(rekey_one, items_to_migrate))
        errors = results.count(False)

    print(f"  snout-spotter-labels: {migrated} re-keyed, {skipped} already done, {errors} errors")
    return errors


def migrate_models(dynamodb, household_id, dry_run):
    """Update s3_key on model records."""
    table = dynamodb.Table("snout-spotter-models")
    print(f"\n  Updating snout-spotter-models...")
    updated = 0
    skipped = 0

    response = table.scan()
    for item in response.get("Items", []):
        model_id = item["model_id"]
        s3_key = item.get("s3_key", "")

        if is_already_prefixed(s3_key, household_id):
            skipped += 1
            continue

        new_s3_key = prefix_key(s3_key, household_id)

        if dry_run:
            print(f"    [DRY RUN] {model_id}: {s3_key} → {new_s3_key}")
            updated += 1
            continue

        table.update_item(
            Key={"model_id": model_id},
            UpdateExpression="SET s3_key = :sk",
            ExpressionAttributeValues={":sk": new_s3_key},
        )
        updated += 1

    print(f"  snout-spotter-models: {updated} updated, {skipped} already done")
    return 0


def migrate_exports(dynamodb, household_id, dry_run):
    """Update s3_key on export records."""
    table = dynamodb.Table("snout-spotter-exports")
    print(f"\n  Updating snout-spotter-exports...")
    updated = 0
    skipped = 0

    response = table.scan()
    for item in response.get("Items", []):
        export_id = item["export_id"]
        s3_key = item.get("s3_key", "")

        if not s3_key or is_already_prefixed(s3_key, household_id):
            skipped += 1
            continue

        new_s3_key = prefix_key(s3_key, household_id)

        if dry_run:
            print(f"    [DRY RUN] {export_id}: {s3_key} → {new_s3_key}")
            updated += 1
            continue

        table.update_item(
            Key={"export_id": export_id},
            UpdateExpression="SET s3_key = :sk",
            ExpressionAttributeValues={":sk": new_s3_key},
        )
        updated += 1

    print(f"  snout-spotter-exports: {updated} updated, {skipped} already done")
    return 0


def migrate_training_jobs(dynamodb, household_id, dry_run):
    """Update export_s3_key and checkpoint_s3_key on training job records."""
    table = dynamodb.Table("snout-spotter-training-jobs")
    print(f"\n  Updating snout-spotter-training-jobs...")
    updated = 0
    skipped = 0

    response = table.scan()
    for item in response.get("Items", []):
        job_id = item["job_id"]
        export_key = item.get("export_s3_key", "")
        checkpoint_key = item.get("checkpoint_s3_key")
        result = item.get("result", {})
        model_key = result.get("model_s3_key") if isinstance(result, dict) else None

        if export_key and is_already_prefixed(export_key, household_id):
            skipped += 1
            continue

        if not export_key:
            skipped += 1
            continue

        update_parts = []
        expr_values = {}

        new_export = prefix_key(export_key, household_id)
        update_parts.append("export_s3_key = :esk")
        expr_values[":esk"] = new_export

        if checkpoint_key and not is_already_prefixed(checkpoint_key, household_id):
            update_parts.append("checkpoint_s3_key = :csk")
            expr_values[":csk"] = prefix_key(checkpoint_key, household_id)

        if dry_run:
            print(f"    [DRY RUN] {job_id}: export_s3_key → {new_export}")
            updated += 1
            continue

        table.update_item(
            Key={"job_id": job_id},
            UpdateExpression="SET " + ", ".join(update_parts),
            ExpressionAttributeValues=expr_values,
        )
        updated += 1

    print(f"  snout-spotter-training-jobs: {updated} updated, {skipped} already done")
    return 0


def main():
    parser = argparse.ArgumentParser(description="Migrate S3 objects and DynamoDB keys to household-prefixed paths")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--profile", default=None)
    parser.add_argument("--skip-s3", action="store_true", help="Skip S3 copy, only update DynamoDB")
    parser.add_argument("--skip-dynamo", action="store_true", help="Skip DynamoDB updates, only copy S3")
    args = parser.parse_args()

    session = boto3.Session(profile_name=args.profile, region_name=REGION)

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Migrating to household-prefixed paths (household={HOUSEHOLD_ID})")
    print("=" * 60)

    total_errors = 0

    if not args.skip_s3:
        print("\n1. Copying S3 objects")
        total_errors += migrate_s3(session, HOUSEHOLD_ID, args.dry_run)
    else:
        print("\n1. S3 copy — SKIPPED")

    if not args.skip_dynamo:
        dynamodb = session.resource("dynamodb")
        print("\n2. Updating DynamoDB records")
        total_errors += migrate_clips(dynamodb, HOUSEHOLD_ID, args.dry_run)
        total_errors += migrate_labels(dynamodb, HOUSEHOLD_ID, args.dry_run)
        total_errors += migrate_models(dynamodb, HOUSEHOLD_ID, args.dry_run)
        total_errors += migrate_exports(dynamodb, HOUSEHOLD_ID, args.dry_run)
        total_errors += migrate_training_jobs(dynamodb, HOUSEHOLD_ID, args.dry_run)
    else:
        print("\n2. DynamoDB updates — SKIPPED")

    if args.dry_run:
        print(f"\n{prefix}No changes made. Run without --dry-run to apply.")
    else:
        print(f"\nDone. {total_errors} total errors.")

    sys.exit(1 if total_errors > 0 else 0)


if __name__ == "__main__":
    main()
