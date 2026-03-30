#!/usr/bin/env python3
"""SnoutSpotter Pi agent — health heartbeat, shadow reporting, and OTA updates via a single MQTT connection."""

import json
import os
import platform
import re
import shutil
import socket
import sqlite3
import subprocess
import sys
import tarfile
import time
import logging
from datetime import datetime, timezone
from pathlib import Path

import boto3
import yaml

from awsiot import mqtt_connection_builder
from awscrt import mqtt

import config_schema
import config_loader

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-agent")

INSTALL_DIR = Path(__file__).parent
BACKUP_DIR = Path.home() / ".snoutspotter" / "backups"
STATUS_DIR = Path.home() / ".snoutspotter"
SERVICES = ["snoutspotter-motion", "snoutspotter-uploader", "snoutspotter-agent"]

SHADOW_DIRTY_FLAG = STATUS_DIR / "shadow-dirty"
CONFIG_RELOAD_MOTION = STATUS_DIR / "config-reload-motion"
CONFIG_RELOAD_UPLOADER = STATUS_DIR / "config-reload-uploader"

# Cached values that don't change at runtime
_cached_pi_model = None
_cached_python_version = None
_cached_sensor_model = None


# ── Config & version ──────────────────────────────────────────────────

def load_config() -> dict:
    return config_loader.load_config()


def load_version() -> str:
    try:
        with open(INSTALL_DIR / "version.json") as f:
            return json.load(f).get("version", "unknown")
    except Exception:
        return "unknown"


def save_version(version: str):
    with open(INSTALL_DIR / "version.json", "w") as f:
        json.dump({"version": version, "updated_at": datetime.now(timezone.utc).isoformat()}, f, indent=4)


# ── Service status ────────────────────────────────────────────────────

def get_service_status(service_name: str) -> str:
    try:
        result = subprocess.run(
            ["systemctl", "is-active", service_name],
            capture_output=True, text=True, timeout=5
        )
        return result.stdout.strip()
    except Exception:
        return "unknown"


def check_services_healthy() -> bool:
    for svc in SERVICES:
        try:
            result = subprocess.run(
                ["systemctl", "is-active", svc],
                capture_output=True, text=True, timeout=5
            )
            if result.stdout.strip() != "active":
                logger.warning(f"Service {svc} is not active: {result.stdout.strip()}")
                return False
        except Exception as e:
            logger.warning(f"Failed to check {svc}: {e}")
            return False
    return True


# ── CloudWatch heartbeat ──────────────────────────────────────────────

def send_heartbeat(cloudwatch, namespace: str, metric_name: str):
    cloudwatch.put_metric_data(
        Namespace=namespace,
        MetricData=[
            {
                "MetricName": metric_name,
                "Timestamp": datetime.now(timezone.utc),
                "Value": 1.0,
                "Unit": "Count",
            }
        ],
    )
    logger.info("CloudWatch heartbeat sent")


# ── Health data gathering ─────────────────────────────────────────────

def _read_status_file(filename: str) -> dict | None:
    try:
        path = STATUS_DIR / filename
        if path.exists():
            return json.loads(path.read_text())
    except Exception:
        pass
    return None


def get_camera_status(config: dict) -> dict:
    try:
        connected = os.path.exists("/dev/video0")
        healthy = connected

        motion_status = _read_status_file("motion-status.json")
        if motion_status is not None:
            healthy = motion_status.get("cameraOk", connected)

        result = {"connected": connected, "healthy": healthy}

        # Sensor model (cached)
        global _cached_sensor_model
        if _cached_sensor_model is None:
            try:
                out = subprocess.run(
                    ["journalctl", "-u", "snoutspotter-motion", "--no-pager", "-n", "50"],
                    capture_output=True, text=True, timeout=5
                )
                match = re.search(r"Sensor: .*/(\w+)@", out.stdout)
                if match:
                    _cached_sensor_model = match.group(1)
            except Exception:
                pass
        if _cached_sensor_model:
            result["sensor"] = _cached_sensor_model

        # Native sensor resolution from v4l2
        if connected:
            try:
                out = subprocess.run(
                    ["v4l2-ctl", "-d", "/dev/video0", "--get-fmt-video"],
                    capture_output=True, text=True, timeout=5
                )
                match = re.search(r"Width/Height\s*:\s*(\d+/\d+)", out.stdout)
                if match:
                    result["resolution"] = match.group(1).replace("/", "x")
            except Exception:
                pass

        # Record resolution from config
        rec_res = config.get("camera", {}).get("record_resolution")
        if rec_res and len(rec_res) == 2:
            result["recordResolution"] = f"{rec_res[0]}x{rec_res[1]}"

        return result
    except Exception:
        return {"connected": False, "healthy": False}


def get_last_motion_time() -> str | None:
    try:
        status = _read_status_file("motion-status.json")
        if status:
            return status.get("lastMotionAt")
    except Exception:
        pass
    return None


def get_last_upload_time() -> str | None:
    try:
        db_path = STATUS_DIR / "uploads.db"
        if not db_path.exists():
            return None
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        cursor = conn.execute("SELECT MAX(uploaded_at) FROM uploads WHERE status = 'uploaded'")
        row = cursor.fetchone()
        conn.close()
        return row[0] if row and row[0] else None
    except Exception:
        return None


def get_upload_stats() -> dict:
    try:
        db_path = STATUS_DIR / "uploads.db"
        if not db_path.exists():
            return {"uploadsToday": 0, "failedToday": 0, "totalUploaded": 0}
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")

        uploaded_today = conn.execute(
            "SELECT COUNT(*) FROM uploads WHERE status = 'uploaded' AND uploaded_at >= ?",
            (today,)
        ).fetchone()[0]

        failed_today = conn.execute(
            "SELECT COUNT(*) FROM uploads WHERE status = 'failed' AND last_attempt >= ?",
            (today,)
        ).fetchone()[0]

        total = conn.execute(
            "SELECT COUNT(*) FROM uploads WHERE status = 'uploaded'"
        ).fetchone()[0]

        conn.close()
        return {"uploadsToday": uploaded_today, "failedToday": failed_today, "totalUploaded": total}
    except Exception:
        return {"uploadsToday": 0, "failedToday": 0, "totalUploaded": 0}


def get_clips_pending(config: dict) -> int:
    try:
        clips_dir = Path(config.get("recording", {}).get("output_dir", "/home/admin/clips"))
        if clips_dir.exists():
            return len(list(clips_dir.glob("*.mp4")))
    except Exception:
        pass
    return 0


def get_system_health() -> dict:
    result = {}

    # CPU temperature
    try:
        temp = Path("/sys/class/thermal/thermal_zone0/temp").read_text().strip()
        result["cpuTempC"] = round(int(temp) / 1000, 1)
    except Exception:
        pass

    # Memory
    try:
        meminfo = Path("/proc/meminfo").read_text()
        total = int(re.search(r"MemTotal:\s+(\d+)", meminfo).group(1))
        available = int(re.search(r"MemAvailable:\s+(\d+)", meminfo).group(1))
        result["memUsedPercent"] = round((1 - available / total) * 100, 1)
    except Exception:
        pass

    # Disk
    try:
        usage = shutil.disk_usage("/")
        result["diskUsedPercent"] = round(usage.used / usage.total * 100, 1)
        result["diskFreeGb"] = round(usage.free / (1024 ** 3), 1)
    except Exception:
        pass

    # Uptime
    try:
        uptime_str = Path("/proc/uptime").read_text().strip().split()[0]
        result["uptimeSeconds"] = int(float(uptime_str))
    except Exception:
        pass

    # Load average
    try:
        result["loadAvg"] = [round(x, 2) for x in os.getloadavg()]
    except Exception:
        pass

    # Pi model (cached)
    global _cached_pi_model
    if _cached_pi_model is None:
        try:
            _cached_pi_model = Path("/proc/device-tree/model").read_text().strip().rstrip("\x00")
        except Exception:
            _cached_pi_model = ""
    if _cached_pi_model:
        result["piModel"] = _cached_pi_model

    # Python version (cached)
    global _cached_python_version
    if _cached_python_version is None:
        _cached_python_version = platform.python_version()
    result["pythonVersion"] = _cached_python_version

    # IP address
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        result["ipAddress"] = s.getsockname()[0]
        s.close()
    except Exception:
        pass

    # WiFi signal strength and SSID
    try:
        wireless = Path("/proc/net/wireless").read_text()
        lines = wireless.strip().split("\n")
        if len(lines) >= 3:
            parts = lines[2].split()
            result["wifiSignalDbm"] = int(float(parts[3]))
    except Exception:
        pass

    try:
        out = subprocess.run(["iwgetid", "-r"], capture_output=True, text=True, timeout=5)
        ssid = out.stdout.strip()
        if ssid:
            result["wifiSsid"] = ssid
    except Exception:
        pass

    return result


# ── IoT Shadow ────────────────────────────────────────────────────────

def build_shadow_state(version: str, config: dict) -> dict:
    services = {svc.replace("snoutspotter-", ""): get_service_status(svc) for svc in SERVICES}
    log_cfg = config.get("log_shipping", {})
    return {
        "state": {
            "reported": {
                "version": version,
                "hostname": socket.gethostname(),
                "services": services,
                "lastHeartbeat": datetime.now(timezone.utc).isoformat(),
                "updateStatus": "idle",
                "camera": get_camera_status(config),
                "lastMotionAt": get_last_motion_time(),
                "lastUploadAt": get_last_upload_time(),
                "uploadStats": get_upload_stats(),
                "clipsPending": get_clips_pending(config),
                "system": get_system_health(),
                "config": config_schema.get_configurable_values(config),
                "logShipping": log_cfg.get("enabled", True),
            }
        }
    }


def update_shadow(connection, thing_name: str, reported: dict):
    payload = json.dumps({"state": {"reported": reported}})
    topic = f"$aws/things/{thing_name}/shadow/update"
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Shadow updated: {reported}")


def report_full_shadow(connection, thing_name: str, version: str, config: dict):
    shadow_state = build_shadow_state(version, config)
    topic = f"$aws/things/{thing_name}/shadow/update"
    payload = json.dumps(shadow_state)
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Full shadow reported (version={version})")


# ── Remote config ────────────────────────────────────────────────────

def apply_remote_config(changes: dict, config: dict, connection, thing_name: str, heartbeat_ref: list):
    """Validate and apply remote config changes from the shadow delta."""
    valid_changes = {}
    errors = {}

    for key, value in changes.items():
        ok, msg = config_schema.validate_config_value(key, value)
        if ok:
            valid_changes[key] = value
        else:
            errors[key] = msg
            logger.warning(f"Config validation error — {msg}")

    if valid_changes:
        config_path = INSTALL_DIR / "config.yaml"
        try:
            with open(config_path) as f:
                file_config = yaml.safe_load(f)

            for key, value in valid_changes.items():
                config_schema.apply_to_dict(file_config, key, value)
                config_schema.apply_to_dict(config, key, value)  # update in-memory config too

            tmp = config_path.with_suffix(".yaml.tmp")
            with open(tmp, "w") as f:
                yaml.dump(file_config, f, default_flow_style=False, allow_unicode=True)
            tmp.rename(config_path)
            logger.info(f"config.yaml updated: {list(valid_changes.keys())}")

            # Update agent's own heartbeat interval in-process (no restart needed)
            if "health.interval_seconds" in valid_changes:
                heartbeat_ref[0] = valid_changes["health.interval_seconds"]
                logger.info(f"Heartbeat interval updated to {heartbeat_ref[0]}s")

            # Signal motion detector and uploader to hot-reload their config dicts
            CONFIG_RELOAD_MOTION.touch(exist_ok=True)
            CONFIG_RELOAD_UPLOADER.touch(exist_ok=True)

        except Exception as e:
            logger.error(f"Failed to write config.yaml: {e}")
            errors["_write"] = str(e)

    # Report applied config (and any errors) back to shadow
    reported: dict = {"config": config_schema.get_configurable_values(config)}
    if errors:
        reported["configErrors"] = errors
    update_shadow(connection, thing_name, reported)
    SHADOW_DIRTY_FLAG.touch(exist_ok=True)


# ── OTA update logic ──────────────────────────────────────────────────

def backup_current(old_version: str):
    backup_path = BACKUP_DIR / old_version
    backup_path.mkdir(parents=True, exist_ok=True)
    for item in INSTALL_DIR.iterdir():
        if item.name.startswith(".") or item.name == "__pycache__":
            continue
        dest = backup_path / item.name
        if item.is_dir():
            shutil.copytree(item, dest, dirs_exist_ok=True)
        else:
            shutil.copy2(item, dest)
    logger.info(f"Backed up current version ({old_version}) to {backup_path}")


def rollback(old_version: str):
    backup_path = BACKUP_DIR / old_version
    if not backup_path.exists():
        logger.error(f"Backup not found for rollback: {backup_path}")
        return False
    for item in backup_path.iterdir():
        dest = INSTALL_DIR / item.name
        if item.is_dir():
            shutil.copytree(item, dest, dirs_exist_ok=True)
        else:
            shutil.copy2(item, dest)
    logger.info(f"Rolled back to version {old_version}")
    return True


def parse_system_deps(path: Path) -> set[str]:
    """Parse system-deps.txt into a set of package names, ignoring comments and blanks."""
    if not path.exists():
        return set()
    packages = set()
    for line in path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            packages.add(line)
    return packages


def install_system_deps(old_version: str | None):
    """Install system apt packages if system-deps.txt has changed since the previous version."""
    new_deps_path = INSTALL_DIR / "system-deps.txt"
    new_deps = parse_system_deps(new_deps_path)
    if not new_deps:
        return

    old_deps = set()
    if old_version:
        old_deps_path = BACKUP_DIR / old_version / "system-deps.txt"
        old_deps = parse_system_deps(old_deps_path)

    if new_deps == old_deps:
        logger.info("System deps unchanged — skipping apt install")
        return

    added = new_deps - old_deps
    if added:
        logger.info(f"New system packages to install: {added}")
    else:
        logger.info("System deps file changed — ensuring all packages are installed")

    packages = sorted(new_deps)
    logger.info(f"Running apt install for: {packages}")
    subprocess.run(["sudo", "apt-get", "update", "-qq"], timeout=120, check=True)
    subprocess.run(
        ["sudo", "apt-get", "install", "-y", "-qq", "--no-install-recommends"] + packages,
        timeout=600, check=True,
    )
    logger.info("System packages installed successfully")


def restart_services():
    for svc in SERVICES:
        try:
            subprocess.run(["sudo", "systemctl", "restart", svc], timeout=30, check=True)
            logger.info(f"Restarted {svc}")
        except Exception as e:
            logger.error(f"Failed to restart {svc}: {e}")


def apply_update(version: str, bucket: str, region: str, connection, thing_name: str):
    old_version = load_version()
    s3_key = f"releases/pi/v{version}.tar.gz"
    local_path = Path(f"/tmp/pi-v{version}.tar.gz")

    logger.info(f"Starting update: {old_version} -> {version}")
    update_shadow(connection, thing_name, {"updateStatus": "updating"})

    try:
        logger.info(f"Downloading s3://{bucket}/{s3_key}")
        s3 = boto3.client("s3", region_name=region)
        s3.download_file(bucket, s3_key, str(local_path))

        backup_current(old_version)

        logger.info(f"Extracting to {INSTALL_DIR}")
        with tarfile.open(local_path, "r:gz") as tar:
            # Exclude config.yaml to preserve device-specific settings
            members = [m for m in tar.getmembers() if m.name not in ("./config.yaml", "config.yaml")]
            tar.extractall(path=INSTALL_DIR, members=members)

        install_system_deps(old_version)

        logger.info("Installing Python dependencies")
        subprocess.run(
            ["pip3", "install", "-r", str(INSTALL_DIR / "requirements.txt"),
             "--break-system-packages", "--quiet"],
            timeout=120, check=True
        )

        save_version(version)
        restart_services()

        logger.info("Waiting 30 seconds for services to stabilize...")
        time.sleep(30)

        if check_services_healthy():
            logger.info(f"Update successful: now running v{version}")
            update_shadow(connection, thing_name, {
                "version": version,
                "updateStatus": "success"
            })
            time.sleep(60)
            update_shadow(connection, thing_name, {"updateStatus": "idle"})
        else:
            raise RuntimeError("Services unhealthy after update")

    except Exception as e:
        logger.error(f"Update failed: {e}")
        logger.info("Attempting rollback...")
        if rollback(old_version):
            save_version(old_version)
            restart_services()
        update_shadow(connection, thing_name, {
            "version": old_version,
            "updateStatus": "failed"
        })
        time.sleep(60)
        update_shadow(connection, thing_name, {"updateStatus": "idle"})
    finally:
        if local_path.exists():
            local_path.unlink()


# ── Log shipping ─────────────────────────────────────────────────────

# Map Python log level names to journalctl priority values
_LEVEL_TO_PRIORITY = {"DEBUG": "7", "INFO": "6", "WARNING": "4", "ERROR": "3"}

# Map journalctl numeric priority back to level name
_PRIORITY_TO_LEVEL = {"7": "DEBUG", "6": "INFO", "5": "NOTICE", "4": "WARNING", "3": "ERROR", "2": "CRITICAL"}


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
                # Strip .service suffix for cleaner display
                service = unit.replace("snoutspotter-", "").replace(".service", "")
                msg = entry.get("MESSAGE", "")
                # Convert microseconds to ISO timestamp
                usec = entry.get("__REALTIME_USEC", "0")
                ts = datetime.fromtimestamp(int(usec) / 1_000_000, tz=timezone.utc).isoformat()
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


# ── Shadow delta handler ──────────────────────────────────────────────

def on_shadow_delta(topic, payload, **kwargs):
    """Called when a shadow delta is received (push notification)."""
    try:
        if on_shadow_delta.updating:
            logger.info("Shadow delta received but OTA in progress — ignoring")
            return
        delta = json.loads(payload)
        state = delta.get("state", {})

        desired_version = state.get("version")
        if desired_version:
            current_version = load_version()
            if desired_version != current_version:
                logger.info(f"Shadow delta: OTA requested {current_version} -> {desired_version}")
                on_shadow_delta.pending_version = desired_version
            else:
                logger.info(f"Shadow delta version {desired_version} matches current — ignoring")

        desired_config = state.get("config")
        if desired_config:
            logger.info(f"Shadow delta: config change requested for {list(desired_config.keys())}")
            on_shadow_delta.pending_config = desired_config

    except Exception as e:
        logger.error(f"Error processing shadow delta: {e}")


def on_shadow_get_accepted(topic, payload, **kwargs):
    """Called when shadow/get/accepted is received — check for pending delta on startup."""
    try:
        shadow = json.loads(payload)
        state = shadow.get("state", {})
        delta = state.get("delta", {})

        desired_version = delta.get("version")
        if desired_version:
            current_version = load_version()
            if desired_version != current_version:
                logger.info(f"Pending OTA delta on startup: {current_version} -> {desired_version}")
                on_shadow_delta.pending_version = desired_version
            else:
                logger.info("Shadow delta version matches current — no OTA needed")

        desired_config = delta.get("config")
        if desired_config:
            logger.info(f"Pending config delta on startup: {list(desired_config.keys())}")
            on_shadow_delta.pending_config = desired_config

        if not desired_version and not desired_config:
            logger.info("No pending shadow delta")

    except Exception as e:
        logger.error(f"Error processing shadow get response: {e}")


on_shadow_delta.pending_version = None
on_shadow_delta.pending_config = None
on_shadow_delta.updating = False


# ── Main loop ─────────────────────────────────────────────────────────

def main():
    config = load_config()
    iot_cfg = config.get("iot", {})
    health_cfg = config["health"]
    upload_cfg = config["upload"]

    endpoint = iot_cfg.get("endpoint", "")
    if not endpoint:
        logger.error("IoT endpoint not configured. Set iot.endpoint in config.yaml or defaults.yaml.")
        return

    thing_name = iot_cfg["thing_name"]
    cert_path = os.path.expanduser(iot_cfg["cert_path"])
    key_path = os.path.expanduser(iot_cfg["key_path"])
    root_ca_path = os.path.expanduser(iot_cfg["root_ca_path"])

    if not all(Path(p).exists() for p in [cert_path, key_path, root_ca_path]):
        logger.error("IoT certificates not found. Run setup-pi.sh first.")
        return

    # CloudWatch client for heartbeats
    cloudwatch = boto3.client("cloudwatch", region_name=upload_cfg["region"])
    heartbeat_ref = [health_cfg["interval_seconds"]]  # list so apply_remote_config can update it

    # Single MQTT connection for shadow + OTA
    connection = mqtt_connection_builder.mtls_from_path(
        endpoint=endpoint,
        cert_filepath=cert_path,
        pri_key_filepath=key_path,
        ca_filepath=root_ca_path,
        client_id=thing_name,
        clean_session=False,
        keep_alive_secs=30,
    )

    connect_future = connection.connect()
    connect_future.result(timeout=10)
    logger.info(f"Connected to IoT Core at {endpoint}")

    # Subscribe to shadow delta for OTA
    delta_topic = f"$aws/things/{thing_name}/shadow/update/delta"
    subscribe_future, _ = connection.subscribe(
        topic=delta_topic,
        qos=mqtt.QoS.AT_LEAST_ONCE,
        callback=on_shadow_delta,
    )
    subscribe_future.result(timeout=10)
    logger.info(f"Subscribed to {delta_topic}")

    # Subscribe to shadow get response, then request current shadow to catch any pending delta
    get_accepted_topic = f"$aws/things/{thing_name}/shadow/get/accepted"
    sub_future, _ = connection.subscribe(
        topic=get_accepted_topic,
        qos=mqtt.QoS.AT_LEAST_ONCE,
        callback=on_shadow_get_accepted,
    )
    sub_future.result(timeout=10)

    get_topic = f"$aws/things/{thing_name}/shadow/get"
    connection.publish(topic=get_topic, payload="", qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info("Requested current shadow to check for pending updates")

    # Report initial state
    version = load_version()
    report_full_shadow(connection, thing_name, version, config)

    # Log shipping state
    log_ship_cfg = config.get("log_shipping", {})
    log_ship_interval_ref = [log_ship_cfg.get("batch_interval_seconds", 60)]
    last_log_timestamp = [datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")]

    logger.info(f"Agent started (version={version}). Heartbeat every {heartbeat_ref[0]}s, watching for OTA/config updates.")

    last_heartbeat = 0
    last_log_ship = 0

    try:
        while True:
            now = time.time()

            # Heartbeat + shadow report on interval
            if now - last_heartbeat >= heartbeat_ref[0]:
                try:
                    send_heartbeat(cloudwatch, health_cfg["namespace"], health_cfg["metric_name"])
                except Exception as e:
                    logger.error(f"Failed to send CloudWatch heartbeat: {e}")

                try:
                    version = load_version()
                    report_full_shadow(connection, thing_name, version, config)
                except Exception as e:
                    logger.error(f"Failed to update shadow: {e}")

                last_heartbeat = now

            # Check for dirty flag — publish shadow immediately on meaningful events from other services
            if SHADOW_DIRTY_FLAG.exists():
                try:
                    SHADOW_DIRTY_FLAG.unlink(missing_ok=True)
                    version = load_version()
                    report_full_shadow(connection, thing_name, version, config)
                    last_heartbeat = now  # reset so heartbeat doesn't fire redundantly right after
                except Exception as e:
                    logger.error(f"Failed to process shadow dirty flag: {e}")

            # Check for pending OTA updates
            if on_shadow_delta.pending_version:
                pending = on_shadow_delta.pending_version
                on_shadow_delta.pending_version = None
                if pending == load_version():
                    logger.info(f"Skipping update to {pending} — already running this version")
                else:
                    on_shadow_delta.updating = True
                    try:
                        apply_update(
                            version=pending,
                            bucket=upload_cfg["bucket_name"],
                            region=upload_cfg["region"],
                            connection=connection,
                            thing_name=thing_name,
                        )
                    finally:
                        on_shadow_delta.updating = False

            # Check for pending config changes
            if on_shadow_delta.pending_config:
                pending = on_shadow_delta.pending_config
                on_shadow_delta.pending_config = None
                if on_shadow_delta.updating:
                    logger.info("Config change received during OTA — will re-apply on next startup via shadow/get")
                else:
                    apply_remote_config(pending, config, connection, thing_name, heartbeat_ref)
                    # Pick up log shipping config changes in-process
                    new_log_cfg = config.get("log_shipping", {})
                    log_ship_interval_ref[0] = new_log_cfg.get("batch_interval_seconds", 60)

            # Ship logs on interval
            if now - last_log_ship >= log_ship_interval_ref[0]:
                try:
                    collect_and_ship_logs(connection, thing_name, config, last_log_timestamp)
                except Exception as e:
                    logger.error(f"Log shipping error: {e}")
                last_log_ship = now

            time.sleep(5)

    except KeyboardInterrupt:
        logger.info("Shutting down agent...")
        connection.disconnect().result(timeout=5)


if __name__ == "__main__":
    main()
