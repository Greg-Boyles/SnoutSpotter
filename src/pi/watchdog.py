#!/usr/bin/env python3
"""SnoutSpotter watchdog — monitors core services and reboots on persistent failure."""

import json
import logging
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-watchdog")

STATUS_DIR = Path.home() / ".snoutspotter"
STATUS_FILE = STATUS_DIR / "watchdog-status.json"
SHADOW_DIRTY_FLAG = STATUS_DIR / "shadow-dirty"

MONITORED_SERVICES = ["snoutspotter-motion", "snoutspotter-uploader", "snoutspotter-agent"]
CHECK_INTERVAL = 30  # seconds between checks
MAX_FAILURES = 5  # consecutive failures before reboot
RESTART_COOLDOWN = 60  # seconds between restart attempts for the same service


def is_service_active(service: str) -> bool:
    try:
        result = subprocess.run(
            ["systemctl", "is-active", service],
            capture_output=True, text=True, timeout=5,
        )
        return result.stdout.strip() == "active"
    except Exception:
        return False


def restart_service(service: str) -> bool:
    try:
        subprocess.run(
            ["sudo", "systemctl", "restart", service],
            timeout=30, check=True,
        )
        logger.info(f"Restarted {service}")
        return True
    except Exception as e:
        logger.error(f"Failed to restart {service}: {e}")
        return False


def reboot():
    logger.warning("Too many consecutive failures — rebooting device")
    try:
        subprocess.run(["sudo", "reboot"], timeout=5)
    except Exception as e:
        logger.error(f"Reboot failed: {e}")


class Watchdog:
    def __init__(self):
        self._failure_counts: dict[str, int] = {svc: 0 for svc in MONITORED_SERVICES}
        self._last_restart: dict[str, float] = {svc: 0.0 for svc in MONITORED_SERVICES}
        self._events: list[dict] = []
        self._total_restarts = 0
        self._total_reboots = 0

    def _touch_shadow_dirty(self):
        try:
            SHADOW_DIRTY_FLAG.touch(exist_ok=True)
        except Exception:
            pass

    def _write_status(self):
        try:
            STATUS_DIR.mkdir(parents=True, exist_ok=True)
            status = {
                "failureCounts": self._failure_counts,
                "totalRestarts": self._total_restarts,
                "totalReboots": self._total_reboots,
                "recentEvents": self._events[-10:],
                "pid": __import__("os").getpid(),
                "updatedAt": datetime.now(timezone.utc).isoformat(),
            }
            tmp = STATUS_FILE.with_suffix(".tmp")
            tmp.write_text(json.dumps(status, indent=2))
            tmp.rename(STATUS_FILE)
        except Exception as e:
            logger.warning(f"Failed to write status file: {e}")

    def _record_event(self, event_type: str, service: str, detail: str = ""):
        event = {
            "type": event_type,
            "service": service,
            "detail": detail,
            "at": datetime.now(timezone.utc).isoformat(),
        }
        self._events.append(event)
        # Keep only the last 50 events in memory
        if len(self._events) > 50:
            self._events = self._events[-50:]

    def check_and_recover(self):
        now = time.time()
        any_action = False

        for svc in MONITORED_SERVICES:
            if is_service_active(svc):
                if self._failure_counts[svc] > 0:
                    logger.info(f"{svc} recovered (was at {self._failure_counts[svc]} failures)")
                    self._failure_counts[svc] = 0
                    any_action = True
                continue

            self._failure_counts[svc] += 1
            logger.warning(f"{svc} is down (failure #{self._failure_counts[svc]})")

            # Check if this service has hit the max consecutive failures — reboot
            if self._failure_counts[svc] >= MAX_FAILURES:
                self._record_event("reboot", svc, f"Failed {MAX_FAILURES} consecutive times")
                self._total_reboots += 1
                self._write_status()
                self._touch_shadow_dirty()
                reboot()
                return

            # Try to restart the service (with cooldown)
            if now - self._last_restart[svc] >= RESTART_COOLDOWN:
                logger.info(f"Attempting to restart {svc}")
                success = restart_service(svc)
                self._last_restart[svc] = now
                self._total_restarts += 1
                self._record_event(
                    "restart_success" if success else "restart_failed",
                    svc,
                    f"failure_count={self._failure_counts[svc]}",
                )
                any_action = True

        if any_action:
            self._write_status()
            self._touch_shadow_dirty()

    def run(self):
        logger.info(f"Watchdog started. Monitoring: {', '.join(MONITORED_SERVICES)}")
        logger.info(f"Check interval: {CHECK_INTERVAL}s, max failures before reboot: {MAX_FAILURES}")

        # Initial status write
        self._write_status()

        check_count = 0
        while True:
            try:
                self.check_and_recover()
                check_count += 1
                # Log an all-healthy confirmation on first check and every 10 thereafter (~5 minutes)
                if check_count == 1 or check_count % 10 == 0:
                    statuses = {svc: ("active" if is_service_active(svc) else "down") for svc in MONITORED_SERVICES}
                    logger.debug(f"Services status: {statuses}")
            except Exception as e:
                logger.error(f"Watchdog check failed: {e}")
            time.sleep(CHECK_INTERVAL)


def main():
    watchdog = Watchdog()
    watchdog.run()


if __name__ == "__main__":
    main()
