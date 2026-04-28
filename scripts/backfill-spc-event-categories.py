#!/usr/bin/env python3
"""
Re-categorise existing snout-spotter-spc-events rows using the corrected
EventCategorizer logic. The original categoriser used wrong five-digit ranges;
two-digit event types (22, 29, 33 etc.) all landed in "other".

Run AFTER deploying the fix. Idempotent — rows already correctly categorised
are skipped. Does NOT backfill weight_change/weight_duration/weight_current;
those are only available from new events ingested by the updated poller
(the data was silently dropped at ingest time and can't be recovered).

Usage:
    AWS_PROFILE=greg python3 scripts/backfill-spc-event-categories.py --dry-run
    AWS_PROFILE=greg python3 scripts/backfill-spc-event-categories.py
"""

import argparse
import boto3

REGION = "eu-west-1"
TABLE = "snout-spotter-spc-events"

# Must match EventCategorizer.cs exactly.
CATEGORIZE = {
    0: "movement",
    7: "movement",
    21: "feeding",
    22: "feeding",
    23: "feeding",
    24: "feeding",
    29: "drinking",
    30: "drinking",
    31: "drinking",
    32: "drinking",
    33: "drinking",
    34: "drinking",
    35: "feeding",
    50: "feeding",
    51: "feeding",
    52: "feeding",
    53: "feeding",
    54: "drinking",
    55: "feeding",
    1: "device_status",
    9: "device_status",
    18: "device_status",
    30000: "device_status",
    30001: "device_status",
    30002: "device_status",
}


def categorize(event_type: int) -> str:
    if event_type in CATEGORIZE:
        return CATEGORIZE[event_type]
    if 20000 <= event_type <= 28999:
        return "device_status"
    return "other"


def main():
    parser = argparse.ArgumentParser(description="Re-categorise SPC event rows")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--profile", default=None)
    args = parser.parse_args()

    session = boto3.Session(profile_name=args.profile, region_name=REGION)
    table = session.resource("dynamodb").Table(TABLE)

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Re-categorising SPC events in {TABLE}")

    updated = 0
    skipped = 0
    scan_kwargs: dict = {}

    while True:
        resp = table.scan(**scan_kwargs)
        for item in resp.get("Items", []):
            event_type = int(item.get("spc_event_type", 0))
            old_cat = item.get("event_category", "other")
            new_cat = categorize(event_type)

            if old_cat == new_cat:
                skipped += 1
                continue

            key = {
                "household_id": item["household_id"],
                "created_at_event": item["created_at_event"],
            }
            label = f"{item['household_id']}/{item['created_at_event']}"

            if args.dry_run:
                print(f"  [DRY] {label}: type={event_type} {old_cat} -> {new_cat}")
            else:
                table.update_item(
                    Key=key,
                    UpdateExpression="SET event_category = :c",
                    ExpressionAttributeValues={":c": new_cat},
                )
                print(f"  {label}: type={event_type} {old_cat} -> {new_cat}")
            updated += 1

        if "LastEvaluatedKey" not in resp:
            break
        scan_kwargs["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

    print(f"\n{prefix}Done. Updated: {updated}, Skipped (already correct): {skipped}")


if __name__ == "__main__":
    main()
