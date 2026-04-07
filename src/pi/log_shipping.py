"""Log shipping — collect journald logs and publish via MQTT."""

import json
import logging
import subprocess
from datetime import datetime, timezone

from awscrt import mqtt

logger = logging.getLogger("snout-spotter-agent")

_LEVEL_TO_PRIORITY = {"DEBUG": "7", "INFO": "6", "WARNING": "4", "ERROR": "3"}
_PRIORITY_TO_LEVEL = {"7": "DEBUG", "6": "INFO", "5": "NOTICE", "4": "WARNING", "3": "ERROR", "2": "CRITICAL"}


def _extract_log_timestamp(entry: dict) -> str:
    for field in ("__REALTIME_USEC", "_SOURCE_REALTIME_USEC"):
        usec_str = entry.get(field, "")
        if usec_str and usec_str != "0":
            try:
                ts = datetime.fromtimestamp(int(usec_str) / 1_000_000, tz=timezone.utc)
                if ts.year >= 2020:
                    return ts.isoformat()
            except (ValueError, OSError):
                continue
    logger.warning("No valid timestamp in journalctl entry, using current time")
    return datetime.now(timezone.utc).isoformat()


def collect_and_ship_logs(connection, thing_name: str, config: dict, last_log_timestamp: list):
    """Read recent journald logs and publish them via MQTT."""
    log_cfg = config.get("log_shipping", {})
    if not log_cfg.get("enabled", True):
        return

    min_level = log_cfg.get("min_level", "INFO")
    max_lines = log_cfg.get("max_lines_per_batch", 50)
    priority = _LEVEL_TO_PRIORITY.get(min_level, "6")

    since = last_log_timestamp[0]
    now_ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")

    try:
        cmd = [
            "journalctl", "--since", since, "--no-pager", "-o", "json",
            "-u", "snoutspotter-motion",
            "-u", "snoutspotter-uploader",
            "-u", "snoutspotter-agent",
            "-u", "snoutspotter-watchdog",
            "-p", priority,
        ]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=10)
        lines = result.stdout.strip().split("\n") if result.stdout.strip() else []

        log_entries = []
        for line in lines[-max_lines:]:
            try:
                entry = json.loads(line)
                prio = entry.get("PRIORITY", "6")
                level = _PRIORITY_TO_LEVEL.get(prio, "INFO")
                unit = entry.get("_SYSTEMD_UNIT", "")
                service = unit.replace("snoutspotter-", "").replace(".service", "")
                msg = entry.get("MESSAGE", "")
                ts = _extract_log_timestamp(entry)
                log_entries.append({"ts": ts, "level": level, "service": service, "msg": msg})
            except (json.JSONDecodeError, ValueError, KeyError):
                continue

        if log_entries:
            payload = json.dumps({
                "thingName": thing_name,
                "timestamp": datetime.now(timezone.utc).isoformat(),
                "logs": log_entries,
            })
            topic = f"snoutspotter/{thing_name}/logs"
            connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
            logger.info(f"Shipped {len(log_entries)} log entries to {topic}")

        last_log_timestamp[0] = now_ts

    except subprocess.TimeoutExpired:
        logger.warning("journalctl timed out during log collection")
    except Exception as e:
        logger.error(f"Log shipping failed: {e}")
