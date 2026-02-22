#!/usr/bin/env python3
"""SnoutSpotter Pi health heartbeat - sends metrics to CloudWatch."""

import time
import logging
from datetime import datetime, timezone

import boto3
import yaml

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-health")


def load_config(path: str = "config.yaml") -> dict:
    with open(path) as f:
        return yaml.safe_load(f)


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
    logger.info("Heartbeat sent")


def main():
    config = load_config()
    health_cfg = config["health"]

    cloudwatch = boto3.client("cloudwatch", region_name=config["upload"]["region"])
    interval = health_cfg["interval_seconds"]

    logger.info(f"Health monitor started. Sending heartbeat every {interval}s")

    while True:
        try:
            send_heartbeat(cloudwatch, health_cfg["namespace"], health_cfg["metric_name"])
        except Exception as e:
            logger.error(f"Failed to send heartbeat: {e}")

        time.sleep(interval)


if __name__ == "__main__":
    main()
