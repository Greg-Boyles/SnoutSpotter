#!/usr/bin/env python3
"""
Phase 1.5: Backfill household_id on all existing DynamoDB records.

Run after Phase 1 deploy (users + households tables exist) and before
Phase 2 deploy (queries start filtering by household_id).

Idempotent — safe to re-run. Uses ConditionExpression to skip records
that already have household_id set.

Usage:
    python scripts/migrate-household-backfill.py [--dry-run]
    python scripts/migrate-household-backfill.py --household-id hh-custom-1234
"""

import argparse
import sys
import boto3
from botocore.exceptions import ClientError

REGION = "eu-west-1"
DEFAULT_HOUSEHOLD_ID = "hh-default"
DEFAULT_HOUSEHOLD_NAME = "Default Household"

TABLES_TO_BACKFILL = [
    {"name": "snout-spotter-clips", "pk": "clip_id"},
    {"name": "snout-spotter-labels", "pk": "keyframe_key"},
    {"name": "snout-spotter-exports", "pk": "export_id"},
    {"name": "snout-spotter-training-jobs", "pk": "job_id"},
    {"name": "snout-spotter-models", "pk": "model_id"},
]

PETS_TABLE = "snout-spotter-pets"
USERS_TABLE = "snout-spotter-users"
HOUSEHOLDS_TABLE = "snout-spotter-households"


def create_default_household(dynamodb, household_id, dry_run):
    """Create the default household record if it doesn't exist."""
    table = dynamodb.Table(HOUSEHOLDS_TABLE)
    try:
        table.put_item(
            Item={
                "household_id": household_id,
                "name": DEFAULT_HOUSEHOLD_NAME,
                "created_at": "2026-04-17T00:00:00Z",
            },
            ConditionExpression="attribute_not_exists(household_id)",
        )
        print(f"  Created household '{household_id}'")
    except ClientError as e:
        if e.response["Error"]["Code"] == "ConditionalCheckFailedException":
            print(f"  Household '{household_id}' already exists — skipping")
        else:
            raise


def backfill_table(dynamodb, table_config, household_id, dry_run):
    """Scan a table and stamp household_id on records missing it."""
    table_name = table_config["name"]
    pk = table_config["pk"]
    table = dynamodb.Table(table_name)

    print(f"\n  Scanning {table_name}...")
    updated = 0
    skipped = 0
    errors = 0

    scan_kwargs = {
        "FilterExpression": "attribute_not_exists(household_id)",
        "ProjectionExpression": pk,
    }

    while True:
        response = table.scan(**scan_kwargs)
        items = response.get("Items", [])

        for item in items:
            pk_value = item[pk]
            if dry_run:
                print(f"    [DRY RUN] Would update {pk}={pk_value}")
                updated += 1
                continue

            try:
                table.update_item(
                    Key={pk: pk_value},
                    UpdateExpression="SET household_id = :hid",
                    ConditionExpression="attribute_not_exists(household_id)",
                    ExpressionAttributeValues={":hid": household_id},
                )
                updated += 1
            except ClientError as e:
                if e.response["Error"]["Code"] == "ConditionalCheckFailedException":
                    skipped += 1
                else:
                    print(f"    ERROR updating {pk}={pk_value}: {e}")
                    errors += 1

        if "LastEvaluatedKey" not in response:
            break
        scan_kwargs["ExclusiveStartKey"] = response["LastEvaluatedKey"]

    print(f"  {table_name}: {updated} updated, {skipped} already done, {errors} errors")
    return errors


def migrate_pets(dynamodb, household_id, dry_run):
    """Re-key pets from household_id='default' to the new household_id."""
    table = dynamodb.Table(PETS_TABLE)

    print(f"\n  Scanning {PETS_TABLE} for household_id='default'...")
    response = table.query(
        KeyConditionExpression="household_id = :hid",
        ExpressionAttributeValues={":hid": "default"},
    )
    items = response.get("Items", [])

    if not items:
        print("  No pets with household_id='default' found — skipping")
        return 0

    migrated = 0
    errors = 0

    for item in items:
        pet_id = item["pet_id"]
        if dry_run:
            print(f"    [DRY RUN] Would re-key pet {pet_id}: 'default' → '{household_id}'")
            migrated += 1
            continue

        new_item = {**item, "household_id": household_id}
        try:
            table.put_item(
                Item=new_item,
                ConditionExpression="attribute_not_exists(pet_id)",
            )
        except ClientError as e:
            if e.response["Error"]["Code"] == "ConditionalCheckFailedException":
                print(f"    Pet {pet_id} already exists under '{household_id}' — deleting old only")
            else:
                print(f"    ERROR creating pet {pet_id} under '{household_id}': {e}")
                errors += 1
                continue

        try:
            table.delete_item(Key={"household_id": "default", "pet_id": pet_id})
            migrated += 1
        except Exception as e:
            print(f"    ERROR deleting old pet {pet_id}: {e}")
            errors += 1

    print(f"  {PETS_TABLE}: {migrated} re-keyed, {errors} errors")
    return errors


def assign_users_to_household(dynamodb, household_id, dry_run):
    """Add the default household to any user that has an empty households list."""
    table = dynamodb.Table(USERS_TABLE)

    print(f"\n  Scanning {USERS_TABLE} for users without households...")
    response = table.scan()
    items = response.get("Items", [])

    updated = 0
    skipped = 0

    for item in items:
        user_id = item["user_id"]
        households = item.get("households", [])

        if any(h.get("householdId") == household_id for h in households):
            skipped += 1
            continue

        if dry_run:
            print(f"    [DRY RUN] Would assign {user_id} to '{household_id}'")
            updated += 1
            continue

        table.update_item(
            Key={"user_id": user_id},
            UpdateExpression="SET households = list_append(if_not_exists(households, :empty), :entry)",
            ExpressionAttributeValues={
                ":empty": [],
                ":entry": [
                    {
                        "householdId": household_id,
                        "role": "owner",
                        "joinedAt": "2026-04-17T00:00:00Z",
                    }
                ],
            },
        )
        updated += 1

    print(f"  {USERS_TABLE}: {updated} assigned, {skipped} already members")


def verify(dynamodb):
    """Quick verification that no records are missing household_id."""
    print("\nVerification:")
    all_good = True
    for t in TABLES_TO_BACKFILL:
        table = dynamodb.Table(t["name"])
        response = table.scan(
            FilterExpression="attribute_not_exists(household_id)",
            Select="COUNT",
        )
        count = response["Count"]
        status = "OK" if count == 0 else f"FAIL — {count} records missing"
        print(f"  {t['name']}: {status}")
        if count > 0:
            all_good = False

    pets_table = dynamodb.Table(PETS_TABLE)
    response = pets_table.query(
        KeyConditionExpression="household_id = :hid",
        ExpressionAttributeValues={":hid": "default"},
        Select="COUNT",
    )
    count = response["Count"]
    status = "OK" if count == 0 else f"FAIL — {count} pets still under 'default'"
    print(f"  {PETS_TABLE} (old 'default' key): {status}")
    if count > 0:
        all_good = False

    return all_good


def main():
    parser = argparse.ArgumentParser(description="Backfill household_id on existing DynamoDB records")
    parser.add_argument("--dry-run", action="store_true", help="Print what would be done without writing")
    parser.add_argument("--household-id", default=DEFAULT_HOUSEHOLD_ID, help=f"Household ID to assign (default: {DEFAULT_HOUSEHOLD_ID})")
    parser.add_argument("--skip-verify", action="store_true", help="Skip post-migration verification")
    args = parser.parse_args()

    dynamodb = boto3.resource("dynamodb", region_name=REGION)
    household_id = args.household_id

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Migrating existing data to household '{household_id}'")
    print("=" * 60)

    print("\n1. Creating default household")
    if not args.dry_run:
        create_default_household(dynamodb, household_id, args.dry_run)
    else:
        print(f"  [DRY RUN] Would create household '{household_id}'")

    print("\n2. Backfilling household_id on tables")
    total_errors = 0
    for table_config in TABLES_TO_BACKFILL:
        total_errors += backfill_table(dynamodb, table_config, household_id, args.dry_run)

    print("\n3. Re-keying pets table")
    total_errors += migrate_pets(dynamodb, household_id, args.dry_run)

    print("\n4. Assigning users to default household")
    assign_users_to_household(dynamodb, household_id, args.dry_run)

    if not args.dry_run and not args.skip_verify:
        all_good = verify(dynamodb)
        print("\n" + ("All checks passed." if all_good else "Some checks FAILED — re-run the script."))
        sys.exit(0 if all_good else 1)
    elif args.dry_run:
        print("\n[DRY RUN] No changes made. Run without --dry-run to apply.")


if __name__ == "__main__":
    main()
