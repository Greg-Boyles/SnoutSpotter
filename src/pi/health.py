#!/usr/bin/env python3
"""SnoutSpotter Pi health heartbeat - sends metrics to CloudWatch and reports to IoT Device Shadow."""

import json
import os
import socket
import subprocess
import time
import logging
from datetime import datetime, timezone
from pathlib import Path

import boto3
import yaml

try:
    from awsiot import iotshadow, mqtt_connection_builder
    from awscrt import mqtt
    HAS_IOT_SDK = True
except ImportError:
    HAS_IOT_SDK = False

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-health")

SERVICES = ["snoutspotter-motion", "snoutspotter-uploader", "snoutspotter-health"]


def load_config(path: str = "config.yaml") -> dict:
    with open(path) as f:
        return yaml.safe_load(f)


def load_version(path: str = "version.json") -> str:
    try:
        with open(path) as f:
            data = json.load(f)
            return data.get("version", "unknown")
    except Exception:
        return "unknown"


def get_service_status(service_name: str) -> str:
    try:
        result = subprocess.run(
            ["systemctl", "is-active", service_name],
            capture_output=True, text=True, timeout=5
        )
        return result.stdout.strip()
    except Exception:
        return "unknown"


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


def build_shadow_state(version: str) -> dict:
    services = {svc.replace("snoutspotter-", ""): get_service_status(svc) for svc in SERVICES}
    return {
        "state": {
            "reported": {
                "version": version,
                "hostname": socket.gethostname(),
                "services": services,
                "lastHeartbeat": datetime.now(timezone.utc).isoformat(),
            }
        }
    }


def connect_iot(iot_cfg: dict) -> mqtt.Connection | None:
    if not HAS_IOT_SDK:
        logger.warning("awsiotsdk not available, skipping Device Shadow reporting")
        return None

    endpoint = iot_cfg.get("endpoint", "")
    if not endpoint:
        logger.warning("IoT endpoint not configured, skipping Device Shadow reporting")
        return None

    cert_path = os.path.expanduser(iot_cfg["cert_path"])
    key_path = os.path.expanduser(iot_cfg["key_path"])
    root_ca_path = os.path.expanduser(iot_cfg["root_ca_path"])

    if not all(Path(p).exists() for p in [cert_path, key_path, root_ca_path]):
        logger.warning("IoT certificates not found, skipping Device Shadow reporting")
        return None

    try:
        connection = mqtt_connection_builder.mtls_from_path(
            endpoint=endpoint,
            cert_filepath=cert_path,
            pri_key_filepath=key_path,
            ca_filepath=root_ca_path,
            client_id=iot_cfg["thing_name"],
            clean_session=False,
            keep_alive_secs=30,
        )
        connect_future = connection.connect()
        connect_future.result(timeout=10)
        logger.info(f"Connected to IoT Core at {endpoint}")
        return connection
    except Exception as e:
        logger.error(f"Failed to connect to IoT Core: {e}")
        return None


def report_shadow(connection: mqtt.Connection, thing_name: str, version: str):
    shadow_state = build_shadow_state(version)
    topic = f"$aws/things/{thing_name}/shadow/update"
    payload = json.dumps(shadow_state)
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Device Shadow updated (version={version})")


def main():
    config = load_config()
    health_cfg = config["health"]
    iot_cfg = config.get("iot", {})
    version = load_version()

    cloudwatch = boto3.client("cloudwatch", region_name=config["upload"]["region"])
    interval = health_cfg["interval_seconds"]

    # Connect to IoT Core for Device Shadow
    mqtt_connection = connect_iot(iot_cfg) if iot_cfg else None

    logger.info(f"Health monitor started (version={version}). Sending heartbeat every {interval}s")

    while True:
        try:
            send_heartbeat(cloudwatch, health_cfg["namespace"], health_cfg["metric_name"])
        except Exception as e:
            logger.error(f"Failed to send CloudWatch heartbeat: {e}")

        if mqtt_connection:
            try:
                report_shadow(mqtt_connection, iot_cfg["thing_name"], version)
            except Exception as e:
                logger.error(f"Failed to update Device Shadow: {e}")

        time.sleep(interval)


if __name__ == "__main__":
    main()
