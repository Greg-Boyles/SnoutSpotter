#!/usr/bin/env python3
"""
Set household_id attribute on existing IoT Things (Pi devices and trainers).

Run after Phase 4 deploy so the DeviceOwnershipService can check device
membership. Idempotent — safe to re-run.

Usage:
    python scripts/migrate-device-household.py --profile greg --dry-run
    python scripts/migrate-device-household.py --profile greg
"""

import argparse
import boto3

REGION = "eu-west-1"
DEFAULT_HOUSEHOLD_ID = "hh-default"
THING_GROUPS = ["snoutspotter-pis", "snoutspotter-trainers"]


def main():
    parser = argparse.ArgumentParser(description="Set household_id on IoT Things")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--profile", default=None)
    parser.add_argument("--household-id", default=DEFAULT_HOUSEHOLD_ID)
    args = parser.parse_args()

    session = boto3.Session(profile_name=args.profile, region_name=REGION)
    iot = session.client("iot")

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Setting household_id='{args.household_id}' on IoT Things")

    for group in THING_GROUPS:
        print(f"\nGroup: {group}")
        try:
            response = iot.list_things_in_thing_group(thingGroupName=group)
        except Exception as e:
            print(f"  Skipping — {e}")
            continue

        for thing_name in response.get("things", []):
            thing = iot.describe_thing(thingName=thing_name)
            current = thing.get("attributes", {}).get("household_id")

            if current == args.household_id:
                print(f"  {thing_name}: already set — skipping")
                continue

            if args.dry_run:
                print(f"  {thing_name}: would set household_id (current: {current})")
            else:
                iot.update_thing(
                    thingName=thing_name,
                    attributePayload={
                        "attributes": {"household_id": args.household_id},
                        "merge": True,
                    },
                )
                print(f"  {thing_name}: set household_id='{args.household_id}'")

    print(f"\n{prefix}Done.")


if __name__ == "__main__":
    main()
