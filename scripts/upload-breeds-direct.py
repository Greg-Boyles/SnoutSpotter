#!/usr/bin/env python3
"""
Upload breed images directly to S3 and write label records to DynamoDB.
Bypasses the API for much faster bulk uploads.

Usage:
    python3 scripts/upload-breeds-direct.py /Users/boylesg/Dev/Images

Requires: AWS CLI credentials configured (aws sts get-caller-identity)
"""

import os
import sys
import uuid
import json
import time
import concurrent.futures
from datetime import datetime, timezone

import boto3
from botocore.config import Config

BUCKET = "snout-spotter-490204853569"
LABELS_TABLE = "snout-spotter-labels"
REGION = "eu-west-1"
MAX_S3_WORKERS = 20
MAX_DDB_WORKERS = 10
BATCH_WRITE_SIZE = 25  # DynamoDB BatchWriteItem limit


def get_clients():
    config = Config(region_name=REGION, max_pool_connections=MAX_S3_WORKERS + 5)
    s3 = boto3.client("s3", config=config)
    dynamodb = boto3.client("dynamodb", config=config)
    return s3, dynamodb


def upload_image(s3, local_path, s3_key):
    content_type = "image/png" if local_path.lower().endswith(".png") else "image/jpeg"
    s3.upload_file(local_path, BUCKET, s3_key, ExtraArgs={"ContentType": content_type})
    return s3_key


def build_label_item(s3_key, breed):
    now = datetime.now(timezone.utc).isoformat()
    item = {
        "keyframe_key": {"S": s3_key},
        "clip_id": {"S": "uploaded"},
        "auto_label": {"S": "dog"},
        "confirmed_label": {"S": "other_dog"},
        "confidence": {"N": "1"},
        "bounding_boxes": {"S": "[]"},
        "reviewed": {"S": "true"},
        "labelled_at": {"S": now},
        "reviewed_at": {"S": now},
        "breed": {"S": breed},
    }
    return item


def batch_write_items(dynamodb, items):
    """Write items to DynamoDB in batches of 25 with retry for unprocessed items."""
    for i in range(0, len(items), BATCH_WRITE_SIZE):
        batch = items[i : i + BATCH_WRITE_SIZE]
        request_items = {
            LABELS_TABLE: [{"PutRequest": {"Item": item}} for item in batch]
        }

        retries = 0
        while request_items:
            response = dynamodb.batch_write_item(RequestItems=request_items)
            unprocessed = response.get("UnprocessedItems", {})
            if unprocessed:
                retries += 1
                if retries > 5:
                    print(f"  WARNING: {len(unprocessed.get(LABELS_TABLE, []))} items failed after 5 retries")
                    break
                time.sleep(2 ** retries * 0.1)  # exponential backoff
                request_items = unprocessed
            else:
                break


def process_breed(s3, dynamodb, breed_dir, breed_name):
    """Upload all images for one breed and write DynamoDB records."""
    # Collect image files
    image_files = []
    for f in sorted(os.listdir(breed_dir)):
        if f.lower().endswith((".jpg", ".jpeg", ".png")):
            image_files.append(os.path.join(breed_dir, f))

    if not image_files:
        return 0, 0

    # Generate S3 keys
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%S")
    upload_plan = []
    for local_path in image_files:
        ext = os.path.splitext(local_path)[1].lower()
        if ext not in (".jpg", ".jpeg", ".png"):
            ext = ".jpg"
        uid = uuid.uuid4().hex[:8]
        s3_key = f"training-uploads/{timestamp}_{uid}{ext}"
        upload_plan.append((local_path, s3_key))

    # Upload images to S3 in parallel
    uploaded_keys = []
    failed = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=MAX_S3_WORKERS) as executor:
        futures = {
            executor.submit(upload_image, s3, local_path, s3_key): s3_key
            for local_path, s3_key in upload_plan
        }
        for future in concurrent.futures.as_completed(futures):
            try:
                key = future.result()
                uploaded_keys.append(key)
            except Exception as e:
                failed += 1
                if failed <= 3:
                    print(f"  S3 upload error: {e}")

    # Write DynamoDB records in batch
    items = [build_label_item(key, breed_name) for key in uploaded_keys]
    batch_write_items(dynamodb, items)

    return len(uploaded_keys), failed


def main():
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <images-dir>")
        sys.exit(1)

    images_dir = sys.argv[1]
    if not os.path.isdir(images_dir):
        print(f"Error: {images_dir} is not a directory")
        sys.exit(1)

    # Verify AWS credentials
    sts = boto3.client("sts", region_name=REGION)
    try:
        identity = sts.get_caller_identity()
        print(f"AWS Account: {identity['Account']}")
    except Exception as e:
        print(f"Error: AWS credentials not configured: {e}")
        sys.exit(1)

    s3, dynamodb = get_clients()

    # Get list of breed directories
    breeds = sorted(
        d for d in os.listdir(images_dir)
        if os.path.isdir(os.path.join(images_dir, d))
    )
    print(f"Found {len(breeds)} breed directories\n")

    total_uploaded = 0
    total_failed = 0
    start_time = time.time()

    for i, breed in enumerate(breeds, 1):
        breed_dir = os.path.join(images_dir, breed)
        breed_start = time.time()

        uploaded, failed = process_breed(s3, dynamodb, breed_dir, breed)

        elapsed = time.time() - breed_start
        total_uploaded += uploaded
        total_failed += failed
        print(f"[{i}/{len(breeds)}] {breed}: {uploaded} uploaded, {failed} failed ({elapsed:.1f}s)")

    total_time = time.time() - start_time
    print(f"\nDone in {total_time:.0f}s! Uploaded: {total_uploaded}, Failed: {total_failed}")


if __name__ == "__main__":
    main()
