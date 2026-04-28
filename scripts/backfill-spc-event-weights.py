#!/usr/bin/env python3
"""
Backfill weight_change, weight_duration, weight_current on existing
snout-spotter-spc-events rows by re-fetching the SPC timeline API.

Existing events were ingested before the poller captured weights[]. This
script pulls the timeline from SPC (using the stored access token), matches
each SPC event id to our DynamoDB row, and writes the weight fields.

Idempotent: rows that already have weight_change are skipped.

Usage:
    AWS_PROFILE=greg python3 scripts/backfill-spc-event-weights.py --dry-run
    AWS_PROFILE=greg python3 scripts/backfill-spc-event-weights.py
"""

import argparse
import json
import urllib.request

import boto3

REGION = "eu-west-1"
EVENTS_TABLE = "snout-spotter-spc-events"
HOUSEHOLDS_TABLE = "snout-spotter-households"
SPC_BASE = "https://app-api.beta.surehub.io"


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
    """Fetch timeline events from SPC. Returns list of event dicts."""
    url = f"{SPC_BASE}/api/timeline/household/{spc_hh_id}?PageSize={page_size}"
    req = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
    })
    resp = urllib.request.urlopen(req)
    body = json.load(resp)
    return body.get("data", [])


def extract_weight(event: dict):
    """Extract weight_change, weight_duration, weight_current from weights[0].frames[0]."""
    weights = event.get("weights") or []
    if not weights:
        return None, None, None
    first_weight = weights[0]
    duration = first_weight.get("duration", 0)
    frames = first_weight.get("frames") or []
    if not frames:
        return None, duration if duration > 0 else None, None
    first_frame = frames[0]
    change = first_frame.get("change")
    current = first_frame.get("current_weight")
    return change, duration if duration > 0 else None, current


def main():
    parser = argparse.ArgumentParser(
        description="Backfill weight data on SPC event rows from the live SPC API"
    )
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--profile", default=None)
    parser.add_argument("--page-size", type=int, default=50,
                        help="SPC timeline page size (default 50)")
    args = parser.parse_args()

    session = boto3.Session(profile_name=args.profile, region_name=REGION)
    ddb = session.resource("dynamodb")
    events_table = ddb.Table(EVENTS_TABLE)

    prefix = "[DRY RUN] " if args.dry_run else ""
    print(f"{prefix}Backfilling weight data from SPC API\n")

    # 1. Find all distinct household_ids in the events table.
    household_ids = set()
    scan_kwargs: dict = {"ProjectionExpression": "household_id"}
    while True:
        resp = events_table.scan(**scan_kwargs)
        for item in resp.get("Items", []):
            household_ids.add(item["household_id"])
        if "LastEvaluatedKey" not in resp:
            break
        scan_kwargs["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

    print(f"Found {len(household_ids)} household(s) with events: {sorted(household_ids)}\n")

    total_updated = 0
    total_skipped = 0
    total_not_found = 0

    for hh_id in sorted(household_ids):
        print(f"--- Household: {hh_id} ---")

        # 2. Get SPC credentials + household id.
        token = get_spc_credentials(session, hh_id)
        if not token:
            print(f"  Skipping — no SPC token\n")
            continue
        spc_hh_id = get_spc_household_id(session, hh_id)
        if not spc_hh_id:
            print(f"  Skipping — no spc_household_id on record\n")
            continue

        # 3. Fetch timeline from SPC.
        print(f"  Fetching SPC timeline for spc_hh={spc_hh_id}...")
        try:
            spc_events = fetch_spc_timeline(token, spc_hh_id, args.page_size)
        except Exception as e:
            print(f"  SPC API error: {e}\n")
            continue
        print(f"  Got {len(spc_events)} events from SPC")

        # Index by SPC event id for quick lookup.
        spc_by_id = {str(e["id"]): e for e in spc_events}

        # 4. Scan our events for this household and backfill.
        event_scan: dict = {
            "FilterExpression": boto3.dynamodb.conditions.Key("household_id").eq(hh_id),
        }
        while True:
            resp = events_table.scan(**event_scan)
            for item in resp.get("Items", []):
                spc_event_id = item.get("spc_event_id")
                if not spc_event_id:
                    continue

                # Skip if already has weight data.
                if item.get("weight_change") is not None:
                    total_skipped += 1
                    continue

                spc_evt = spc_by_id.get(spc_event_id)
                if not spc_evt:
                    total_not_found += 1
                    continue

                change, duration, current = extract_weight(spc_evt)
                if change is None and duration is None and current is None:
                    total_skipped += 1
                    continue

                key = {
                    "household_id": item["household_id"],
                    "created_at_event": item["created_at_event"],
                }
                label = f"{item['household_id']}/{item['created_at_event']}"

                update_parts = []
                values = {}
                if change is not None:
                    update_parts.append("weight_change = :wc")
                    values[":wc"] = change
                if duration is not None:
                    update_parts.append("weight_duration = :wd")
                    values[":wd"] = duration
                if current is not None:
                    update_parts.append("weight_current = :wcur")
                    values[":wcur"] = current

                if not update_parts:
                    total_skipped += 1
                    continue

                update_expr = "SET " + ", ".join(update_parts)

                if args.dry_run:
                    print(f"  [DRY] {label}: change={change} duration={duration} current={current}")
                else:
                    events_table.update_item(
                        Key=key,
                        UpdateExpression=update_expr,
                        ExpressionAttributeValues=values,
                    )
                    print(f"  {label}: change={change} duration={duration} current={current}")
                total_updated += 1

            if "LastEvaluatedKey" not in resp:
                break
            event_scan["ExclusiveStartKey"] = resp["LastEvaluatedKey"]

        print()

    print(f"{prefix}Done. Updated: {total_updated}, Skipped: {total_skipped}, "
          f"Not found in SPC (event too old / outside page): {total_not_found}")


if __name__ == "__main__":
    main()
