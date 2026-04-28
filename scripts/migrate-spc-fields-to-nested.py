#!/usr/bin/env python3
"""
Migrate SPC link fields on pets and devices from flat top-level attributes
into a nested `spc_integration` map.

The household record already stores SPC linkage in a nested spc_integration
map. Pets and devices used flat attributes (spc_pet_id, spc_pet_name on pets;
spc_product_id, spc_name, serial_number, last_refreshed_at on devices). This
script moves them into the same nested shape so every row that holds SPC
linkage data follows one pattern. Unlink becomes a single
REMOVE spc_integration across all three tables.

Run BEFORE deploying the refactored Lambdas — the new code only reads the
nested shape. Any row with flat fields still present after deploy will
appear "unlinked" until re-migrated or re-linked by the user.

Idempotent: running twice is safe. Rows with no flat fields left are skipped.
`spc_device_id` on device rows is intentionally left flat (it's the row
identifier, also in the SK).

Usage:
    AWS_PROFILE=greg python3 scripts/migrate-spc-fields-to-nested.py --dry-run
    AWS_PROFILE=greg python3 scripts/migrate-spc-fields-to-nested.py
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone

import boto3
from boto3.dynamodb.conditions import Attr, Key

REGION = "eu-west-1"
PETS_TABLE = "snout-spotter-pets"
DEVICES_TABLE = "snout-spotter-devices"

# Flat attributes on pet rows that need to move into spc_integration
PET_FLAT_FIELDS = ["spc_pet_id", "spc_pet_name"]

# Flat attributes on device spc# rows that need to move into spc_integration.
# spc_device_id stays flat — it's the row identifier (also in the SK).
DEVICE_FLAT_FIELDS = ["spc_product_id", "spc_name", "serial_number", "last_refreshed_at"]


def iso_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def build_pet_map(item: dict, linked_at: str) -> dict:
    """Copy existing flat pet SPC fields into the spc_integration map shape."""
    m = {"spc_pet_id": item["spc_pet_id"], "linked_at": linked_at}
    if "spc_pet_name" in item and item["spc_pet_name"]:
        m["spc_pet_name"] = item["spc_pet_name"]
    return m


def build_device_map(item: dict, linked_at: str) -> dict:
    """Copy existing flat device SPC fields into the spc_integration map shape."""
    m = {"linked_at": linked_at}
    if "spc_product_id" in item and item["spc_product_id"] is not None:
        m["spc_product_id"] = item["spc_product_id"]
    if "spc_name" in item and item["spc_name"]:
        m["spc_name"] = item["spc_name"]
    if "serial_number" in item and item["serial_number"]:
        m["serial_number"] = item["serial_number"]
    if "last_refreshed_at" in item and item["last_refreshed_at"]:
        m["last_refreshed_at"] = item["last_refreshed_at"]
    return m


def migrate_pets(table, dry_run: bool) -> tuple[int, int]:
    """Scan snout-spotter-pets. Return (migrated, skipped)."""
    migrated = 0
    skipped = 0
    scan_kwargs = {"FilterExpression": Attr("spc_pet_id").exists()}

    while True:
        resp = table.scan(**scan_kwargs)
        for item in resp.get("Items", []):
            key = {"household_id": item["household_id"], "pet_id": item["pet_id"]}

            # Skip rows that already have the nested map AND no flat fields —
            # a previous run finished them. If a row has BOTH the map and flat
            # fields (partial migration), we prefer the flat values as the
            # source of truth and overwrite the map.
            if "spc_integration" in item and "spc_pet_id" not in item:
                skipped += 1
                continue

            linked_at = iso_now()
            # Preserve the existing linked_at if the map is already partially
            # populated from an earlier run.
            existing = item.get("spc_integration") or {}
            if isinstance(existing, dict) and existing.get("linked_at"):
                linked_at = existing["linked_at"]

            new_map = build_pet_map(item, linked_at)
            pet_label = f"{item['household_id']}/{item['pet_id']}"

            if dry_run:
                print(f"  [DRY] {pet_label}: would set spc_integration={new_map}, remove {PET_FLAT_FIELDS}")
            else:
                table.update_item(
                    Key=key,
                    UpdateExpression="SET spc_integration = :m REMOVE " + ", ".join(PET_FLAT_FIELDS),
                    ExpressionAttributeValues={":m": new_map},
                )
                print(f"  {pet_label}: migrated")
            migrated += 1

        if "LastEvaluatedKey" not in resp:
            break
        scan_kwargs["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

    return migrated, skipped


def migrate_devices(table, dry_run: bool) -> tuple[int, int]:
    """Scan snout-spotter-devices for spc# rows with any flat SPC field."""
    migrated = 0
    skipped = 0

    any_flat_field_exists = (
        Attr("spc_product_id").exists()
        | Attr("spc_name").exists()
        | Attr("serial_number").exists()
        | Attr("last_refreshed_at").exists()
    )
    scan_kwargs = {
        "FilterExpression": Attr("sk").begins_with("spc#") & any_flat_field_exists,
    }

    while True:
        resp = table.scan(**scan_kwargs)
        for item in resp.get("Items", []):
            key = {"household_id": item["household_id"], "sk": item["sk"]}
            device_label = f"{item['household_id']}/{item['sk']}"

            linked_at = iso_now()
            existing = item.get("spc_integration") or {}
            if isinstance(existing, dict) and existing.get("linked_at"):
                linked_at = existing["linked_at"]

            new_map = build_device_map(item, linked_at)

            # Only REMOVE the fields that actually exist on this row —
            # DynamoDB rejects REMOVE on attributes that aren't there.
            to_remove = [f for f in DEVICE_FLAT_FIELDS if f in item and item.get(f) is not None]
            if not to_remove:
                skipped += 1
                continue

            remove_clause = ", ".join(to_remove)

            if dry_run:
                print(f"  [DRY] {device_label}: would set spc_integration={new_map}, remove {to_remove}")
            else:
                table.update_item(
                    Key=key,
                    UpdateExpression=f"SET spc_integration = :m REMOVE {remove_clause}",
                    ExpressionAttributeValues={":m": new_map},
                )
                print(f"  {device_label}: migrated")
            migrated += 1

        if "LastEvaluatedKey" not in resp:
            break
        scan_kwargs["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

    return migrated, skipped


def main():
    parser = argparse.ArgumentParser(description="Nest SPC link fields under spc_integration")
    parser.add_argument("--dry-run", action="store_true", help="Report changes without writing")
    parser.add_argument("--profile", default=None, help="AWS profile (or use AWS_PROFILE env)")
    args = parser.parse_args()

    session = boto3.Session(profile_name=args.profile, region_name=REGION)
    ddb = session.resource("dynamodb")

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Migrating SPC fields into spc_integration map")

    print("\nPets table:")
    pet_done, pet_skipped = migrate_pets(ddb.Table(PETS_TABLE), args.dry_run)
    print(f"  Pets migrated: {pet_done}, skipped (already nested): {pet_skipped}")

    print("\nDevices table:")
    dev_done, dev_skipped = migrate_devices(ddb.Table(DEVICES_TABLE), args.dry_run)
    print(f"  Devices migrated: {dev_done}, skipped (no flat fields): {dev_skipped}")

    print(f"\n{prefix}Done.")


if __name__ == "__main__":
    main()
