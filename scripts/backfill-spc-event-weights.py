#!/usr/bin/env python3
"""
Backfill the nested `weight` attribute on existing snout-spotter-spc-events
rows by re-fetching the SPC timeline API.

Existing events were ingested before the poller captured weights[]. This
script pulls the timeline from SPC (using the stored access token), matches
each SPC event id to our DynamoDB row, and writes the full weight structure:

  weight = {
    duration: N,
    context: N,
    frames: [ { index: N, change: N, current_weight: N }, ... ]
  }

Multi-bowl devices produce multiple frames (one per bowl). Also removes any
flat weight_change/weight_duration/weight_current attrs left by the
previous version of this script.

Idempotent: rows that already have the nested `weight` map are skipped.

Usage:
    AWS_PROFILE=greg python3 scripts/backfill-spc-event-weights.py --dry-run
    AWS_PROFILE=greg python3 scripts/backfill-spc-event-weights.py
"""

import argparse
import json
import urllib.request
from decimal import Decimal

import boto3

REGION = "eu-west-1"
EVENTS_TABLE = "snout-spotter-spc-events"
HOUSEHOLDS_TABLE = "snout-spotter-households"
SPC_BASE = "https://app-api.beta.surehub.io"

# Flat attrs from the previous version that should be cleaned up.
LEGACY_FLAT_ATTRS = ["weight_change", "weight_duration", "weight_current"]


def get_spc_credentials(session, household_id: str):
    sm = session.client("secretsmanager")
    try:
        resp = sm.get_secret_value(SecretId=f"snoutspotter/spc/{household_id}")
        secret = json.loads(resp["SecretString"])
        return secret["access_token"]
    except Exception as e:
        print(f"  Could not load SPC token for {household_id}: {e}")
        return None


def get_spc_household_id(session, household_id: str):
    ddb = session.resource("dynamodb")
    table = ddb.Table(HOUSEHOLDS_TABLE)
    resp = table.get_item(Key={"household_id": household_id})
    item = resp.get("Item")
    if not item:
        return None
    integration = item.get("spc_integration")
    if not integration:
        return None
    return integration.get("spc_household_id")


def fetch_spc_timeline(token: str, spc_hh_id: str, page_size: int = 50):
    url = f"{SPC_BASE}/api/timeline/household/{spc_hh_id}?PageSize={page_size}"
    req = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
    })
    resp = urllib.request.urlopen(req)
    body = json.load(resp)
    return body.get("data", [])


def build_weight_map(spc_event: dict):
    """Build the nested weight structure from SPC's weights[0]."""
    weights = spc_event.get("weights") or []
    if not weights:
        return None
    first = weights[0]
    frames_raw = first.get("frames") or []
    if not frames_raw:
        return None

    frames = []
    for f in frames_raw:
        frames.append({
            "index": f.get("index", 0),
            "change": f.get("change", 0),
            "current_weight": f.get("current_weight", 0),
        })

    return {
        "duration": first.get("duration", 0),
        "context": first.get("context", 0),
        "frames": frames,
    }


def main():
    parser = argparse.ArgumentParser(
        description="Backfill nested weight data on SPC event rows from the live SPC API"
    )
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--profile", default=None)
    parser.add_argument("--page-size", type=int, default=50)
    args = parser.parse_args()

    session = boto3.Session(profile_name=args.profile, region_name=REGION)
    ddb = session.resource("dynamodb")
    events_table = ddb.Table(EVENTS_TABLE)

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Backfilling nested weight data from SPC API\n")

    # Find distinct households.
    household_ids = set()
    scan_kwargs: dict = {"ProjectionExpression": "household_id"}
    while True:
        resp = events_table.scan(**scan_kwargs)
        for item in resp.get("Items", []):
            household_ids.add(item["household_id"])
        if "LastEvaluatedKey" not in resp:
            break
        scan_kwargs["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

    print(f"Found {len(household_ids)} household(s): {sorted(household_ids)}\n")

    total_updated = 0
    total_skipped = 0
    total_not_found = 0

    for hh_id in sorted(household_ids):
        print(f"--- Household: {hh_id} ---")

        token = get_spc_credentials(session, hh_id)
        if not token:
            print(f"  Skipping — no SPC token\n")
            continue
        spc_hh_id = get_spc_household_id(session, hh_id)
        if not spc_hh_id:
            print(f"  Skipping — no spc_household_id\n")
            continue

        print(f"  Fetching SPC timeline for spc_hh={spc_hh_id}...")
        try:
            spc_events = fetch_spc_timeline(token, spc_hh_id, args.page_size)
        except Exception as e:
            print(f"  SPC API error: {e}\n")
            continue
        print(f"  Got {len(spc_events)} events from SPC")

        spc_by_id = {str(e["id"]): e for e in spc_events}

        event_scan: dict = {
            "FilterExpression": boto3.dynamodb.conditions.Key("household_id").eq(hh_id),
        }
        while True:
            resp = events_table.scan(**event_scan)
            for item in resp.get("Items", []):
                spc_event_id = item.get("spc_event_id")
                if not spc_event_id:
                    continue

                # Skip if already has nested weight map.
                if isinstance(item.get("weight"), dict) and item["weight"].get("frames"):
                    total_skipped += 1
                    continue

                spc_evt = spc_by_id.get(spc_event_id)
                if not spc_evt:
                    total_not_found += 1
                    continue

                weight_map = build_weight_map(spc_evt)
                if not weight_map:
                    total_skipped += 1
                    continue

                key = {
                    "household_id": item["household_id"],
                    "created_at_event": item["created_at_event"],
                }
                label = f"{item['household_id']}/{item['created_at_event']}"

                # Convert ints to Decimal for boto3 (DynamoDB resource API).
                weight_for_ddb = json.loads(json.dumps(weight_map), parse_float=Decimal, parse_int=Decimal)

                # Also remove any flat legacy attrs from the previous script run.
                flat_to_remove = [a for a in LEGACY_FLAT_ATTRS if a in item]
                remove_clause = ", ".join(flat_to_remove) if flat_to_remove else None

                total_change = sum(f["change"] for f in weight_map["frames"])
                frame_count = len(weight_map["frames"])

                if args.dry_run:
                    print(f"  [DRY] {label}: {frame_count} frame(s), total_change={total_change}, duration={weight_map['duration']}"
                          + (f", removing {flat_to_remove}" if flat_to_remove else ""))
                else:
                    update_expr = "SET weight = :w"
                    if remove_clause:
                        update_expr += f" REMOVE {remove_clause}"
                    events_table.update_item(
                        Key=key,
                        UpdateExpression=update_expr,
                        ExpressionAttributeValues={":w": weight_for_ddb},
                    )
                    print(f"  {label}: {frame_count} frame(s), total_change={total_change}")
                total_updated += 1

            if "LastEvaluatedKey" not in resp:
                break
            event_scan["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

        print()

    print(f"{prefix}Done. Updated: {total_updated}, Skipped: {total_skipped}, "
          f"Not in SPC page: {total_not_found}")


if __name__ == "__main__":
    main()
