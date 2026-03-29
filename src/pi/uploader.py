#!/usr/bin/env python3
"""SnoutSpotter S3 upload service - watches for new clips and uploads them."""

import json
import os
import time
import sqlite3
import logging
from datetime import datetime, timezone
from pathlib import Path

import boto3
import yaml
from botocore.exceptions import ClientError

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
logger = logging.getLogger("snout-spotter-upload")

STATUS_DIR = Path.home() / ".snoutspotter"
STATUS_FILE = STATUS_DIR / "uploader-status.json"
SHADOW_DIRTY_FLAG = STATUS_DIR / "shadow-dirty"


def load_config(path: str = "config.yaml") -> dict:
    with open(path) as f:
        return yaml.safe_load(f)


class UploadLedger:
    """Local SQLite ledger to track upload status."""

    def __init__(self, db_path: str | None = None):
        if db_path is None:
            db_dir = Path.home() / ".snoutspotter"
            db_dir.mkdir(parents=True, exist_ok=True)
            db_path = str(db_dir / "uploads.db")
        self.conn = sqlite3.connect(db_path)
        self.conn.execute("""
            CREATE TABLE IF NOT EXISTS uploads (
                filename TEXT PRIMARY KEY,
                s3_key TEXT,
                status TEXT DEFAULT 'pending',
                attempts INTEGER DEFAULT 0,
                last_attempt TEXT,
                uploaded_at TEXT
            )
        """)
        self.conn.commit()

    def record_attempt(self, filename: str, s3_key: str, success: bool):
        now = datetime.now(timezone.utc).isoformat()
        if success:
            self.conn.execute(
                "INSERT OR REPLACE INTO uploads (filename, s3_key, status, uploaded_at) VALUES (?, ?, 'uploaded', ?)",
                (filename, s3_key, now),
            )
        else:
            self.conn.execute("""
                INSERT INTO uploads (filename, s3_key, status, attempts, last_attempt)
                VALUES (?, ?, 'failed', 1, ?)
                ON CONFLICT(filename) DO UPDATE SET
                    attempts = attempts + 1,
                    last_attempt = ?,
                    status = 'failed'
            """, (filename, s3_key, now, now))
        self.conn.commit()

    def get_failed(self, max_attempts: int = 5) -> list:
        cursor = self.conn.execute(
            "SELECT filename, s3_key FROM uploads WHERE status = 'failed' AND attempts < ?",
            (max_attempts,),
        )
        return cursor.fetchall()


class Uploader:
    def __init__(self, config: dict):
        self.config = config["upload"]
        self.clips_dir = Path(config["recording"]["output_dir"])
        self.s3 = boto3.client("s3", region_name=self.config["region"])
        self.bucket = self.config["bucket_name"]
        self.prefix = self.config["prefix"]
        self.ledger = UploadLedger()

        # Status tracking
        self._last_upload_at = None
        self._uploads_today = 0
        self._failed_today = 0
        self._uploads_today_date = None

    def _touch_shadow_dirty(self):
        try:
            SHADOW_DIRTY_FLAG.touch(exist_ok=True)
        except Exception:
            pass

    def _check_today_reset(self):
        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        if self._uploads_today_date != today:
            self._uploads_today = 0
            self._failed_today = 0
            self._uploads_today_date = today

    def _write_status(self):
        try:
            STATUS_DIR.mkdir(parents=True, exist_ok=True)
            status = {
                "lastUploadAt": self._last_upload_at,
                "uploadsToday": self._uploads_today,
                "failedToday": self._failed_today,
                "pid": os.getpid(),
                "updatedAt": datetime.now(timezone.utc).isoformat(),
            }
            tmp = STATUS_FILE.with_suffix(".tmp")
            tmp.write_text(json.dumps(status, indent=2))
            tmp.rename(STATUS_FILE)
        except Exception as e:
            logger.warning(f"Failed to write status file: {e}")

    def get_s3_key(self, filename: str) -> str:
        """Generate S3 key from filename: raw-clips/YYYY/MM/DD/filename."""
        now = datetime.now(timezone.utc)
        return f"{self.prefix}/{now.strftime('%Y/%m/%d')}/{filename}"

    def upload_file(self, filepath: Path) -> bool:
        """Upload a single file to S3 with multipart upload."""
        s3_key = self.get_s3_key(filepath.name)
        self._check_today_reset()
        try:
            logger.info(f"Uploading {filepath.name} -> s3://{self.bucket}/{s3_key}")
            self.s3.upload_file(
                str(filepath),
                self.bucket,
                s3_key,
                Config=boto3.s3.transfer.TransferConfig(
                    multipart_threshold=8 * 1024 * 1024,  # 8 MB
                    multipart_chunksize=8 * 1024 * 1024,
                ),
            )
            self.ledger.record_attempt(filepath.name, s3_key, success=True)
            logger.info(f"Upload complete: {filepath.name}")

            self._last_upload_at = datetime.now(timezone.utc).isoformat()
            self._uploads_today += 1
            self._write_status()
            self._touch_shadow_dirty()

            if self.config["delete_after_upload"]:
                filepath.unlink()
                logger.info(f"Deleted local file: {filepath.name}")

            return True

        except ClientError as e:
            logger.error(f"Upload failed for {filepath.name}: {e}")
            self.ledger.record_attempt(filepath.name, s3_key, success=False)
            self._failed_today += 1
            self._write_status()
            self._touch_shadow_dirty()
            return False

    def retry_failed(self):
        """Retry previously failed uploads."""
        failed = self.ledger.get_failed(self.config["max_retries"])
        for filename, s3_key in failed:
            filepath = self.clips_dir / filename
            if filepath.exists():
                logger.info(f"Retrying failed upload: {filename}")
                self.upload_file(filepath)

    def watch_and_upload(self):
        """Main loop: watch for new clips and upload them."""
        logger.info(f"Watching {self.clips_dir} for new clips...")

        while True:
            try:
                # Find completed clips (files that haven't been modified in 5 seconds)
                for filepath in sorted(self.clips_dir.glob("*.mp4")):
                    age = time.time() - filepath.stat().st_mtime
                    if age > 5:  # File is at least 5 seconds old (recording likely complete)
                        self.upload_file(filepath)

                # Retry any previously failed uploads
                self.retry_failed()

            except Exception as e:
                logger.error(f"Error in upload loop: {e}")

            time.sleep(10)  # Check every 10 seconds


def main():
    config = load_config()
    uploader = Uploader(config)
    uploader.watch_and_upload()


if __name__ == "__main__":
    main()
