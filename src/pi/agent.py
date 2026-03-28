#!/usr/bin/env python3
"""SnoutSpotter Pi agent — health heartbeat, shadow reporting, and OTA updates via a single MQTT connection."""

import json
import os
import shutil
import socket
import subprocess
import tarfile
import time
import logging
from datetime import datetime, timezone
from pathlib import Path

import boto3
import yaml

from awsiot import mqtt_connection_builder
from awscrt import mqtt

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-agent")

INSTALL_DIR = Path(__file__).parent
BACKUP_DIR = Path.home() / ".snoutspotter" / "backups"
SERVICES = ["snoutspotter-motion", "snoutspotter-uploader", "snoutspotter-agent"]


# ── Config & version ──────────────────────────────────────────────────

def load_config(path: str = "config.yaml") -> dict:
    with open(path) as f:
        return yaml.safe_load(f)


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


# ── IoT Shadow ────────────────────────────────────────────────────────

def build_shadow_state(version: str) -> dict:
    services = {svc.replace("snoutspotter-", ""): get_service_status(svc) for svc in SERVICES}
    return {
        "state": {
            "reported": {
                "version": version,
                "hostname": socket.gethostname(),
                "services": services,
                "lastHeartbeat": datetime.now(timezone.utc).isoformat(),
                "updateStatus": "idle",
            }
        }
    }


def update_shadow(connection, thing_name: str, reported: dict):
    payload = json.dumps({"state": {"reported": reported}})
    topic = f"$aws/things/{thing_name}/shadow/update"
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Shadow updated: {reported}")


def report_full_shadow(connection, thing_name: str, version: str):
    shadow_state = build_shadow_state(version)
    topic = f"$aws/things/{thing_name}/shadow/update"
    payload = json.dumps(shadow_state)
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Full shadow reported (version={version})")


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
            tar.extractall(path=INSTALL_DIR)

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


# ── Shadow delta handler ──────────────────────────────────────────────

def on_shadow_delta(topic, payload, **kwargs):
    """Called when a shadow delta is received."""
    try:
        delta = json.loads(payload)
        state = delta.get("state", {})
        desired_version = state.get("version")
        if desired_version:
            current_version = load_version()
            if desired_version != current_version:
                logger.info(f"Shadow delta: update requested {current_version} -> {desired_version}")
                on_shadow_delta.pending_version = desired_version
    except Exception as e:
        logger.error(f"Error processing shadow delta: {e}")


on_shadow_delta.pending_version = None


# ── Main loop ─────────────────────────────────────────────────────────

def main():
    config = load_config()
    iot_cfg = config.get("iot", {})
    health_cfg = config["health"]
    upload_cfg = config["upload"]

    endpoint = iot_cfg.get("endpoint", "")
    if not endpoint:
        logger.error("IoT endpoint not configured. Set iot.endpoint in config.yaml.")
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
    heartbeat_interval = health_cfg["interval_seconds"]

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

    # Report initial state
    version = load_version()
    report_full_shadow(connection, thing_name, version)

    logger.info(f"Agent started (version={version}). Heartbeat every {heartbeat_interval}s, watching for OTA updates.")

    last_heartbeat = 0

    try:
        while True:
            now = time.time()

            # Heartbeat + shadow report on interval
            if now - last_heartbeat >= heartbeat_interval:
                try:
                    send_heartbeat(cloudwatch, health_cfg["namespace"], health_cfg["metric_name"])
                except Exception as e:
                    logger.error(f"Failed to send CloudWatch heartbeat: {e}")

                try:
                    version = load_version()
                    report_full_shadow(connection, thing_name, version)
                except Exception as e:
                    logger.error(f"Failed to update shadow: {e}")

                last_heartbeat = now

            # Check for pending OTA updates
            if on_shadow_delta.pending_version:
                pending = on_shadow_delta.pending_version
                on_shadow_delta.pending_version = None
                apply_update(
                    version=pending,
                    bucket=upload_cfg["bucket_name"],
                    region=upload_cfg["region"],
                    connection=connection,
                    thing_name=thing_name,
                )

            time.sleep(5)

    except KeyboardInterrupt:
        logger.info("Shutting down agent...")
        connection.disconnect().result(timeout=5)


if __name__ == "__main__":
    main()
