#!/usr/bin/env python3
"""SnoutSpotter Pi agent — thin orchestrator for MQTT connection and shadow delta dispatch."""

import json
import os
import subprocess
import sys
import time
import logging
from datetime import datetime, timezone
from pathlib import Path

from awsiot import mqtt_connection_builder
from awscrt import mqtt

import config_loader
import iot_credential_provider
from ota import load_version, apply_update
from shadow import update_shadow, report_full_shadow
from remote_config import apply_remote_config
from log_shipping import collect_and_ship_logs
from commands import execute_command

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-agent")

INSTALL_DIR = Path(__file__).parent
STATUS_DIR = Path.home() / ".snoutspotter"
SHADOW_DIRTY_FLAG = STATUS_DIR / "shadow-dirty"


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

        if "streaming" in state:
            on_shadow_delta.pending_streaming = state["streaming"]
            logger.info(f"Shadow delta: streaming={'start' if state['streaming'] else 'stop'}")

        desired_command = state.get("command")
        if desired_command:
            logger.info(f"Shadow delta: command received: {desired_command.get('action')}")
            on_shadow_delta.pending_command = desired_command

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

        if "streaming" in delta:
            on_shadow_delta.pending_streaming = delta["streaming"]
            logger.info(f"Pending streaming delta on startup: {delta['streaming']}")

        desired_command = delta.get("command")
        if desired_command:
            logger.info(f"Pending command delta on startup: {desired_command.get('action')}")
            on_shadow_delta.pending_command = desired_command

        if not desired_version and not desired_config and "streaming" not in delta and not desired_command:
            logger.info("No pending shadow delta")

    except Exception as e:
        logger.error(f"Error processing shadow get response: {e}")


on_shadow_delta.pending_version = None
on_shadow_delta.pending_config = None
on_shadow_delta.pending_streaming = None
on_shadow_delta.pending_command = None
on_shadow_delta.updating = False


# ── Main loop ─────────────────────────────────────────────────────────

def main():
    config = config_loader.load_config()
    iot_cfg = config.get("iot", {})
    health_cfg = config["health"]
    upload_cfg = config["upload"]

    endpoint = iot_cfg.get("endpoint", "")
    if not endpoint:
        logger.error("IoT endpoint not configured. Set iot.endpoint in config.yaml or defaults.yaml.")
        return

    thing_name = iot_cfg["thing_name"]
    os.environ["IOT_THING_NAME"] = thing_name
    cert_path = os.path.expanduser(iot_cfg["cert_path"])
    key_path = os.path.expanduser(iot_cfg["key_path"])
    root_ca_path = os.path.expanduser(iot_cfg["root_ca_path"])

    if not all(Path(p).exists() for p in [cert_path, key_path, root_ca_path]):
        logger.error("IoT certificates not found. Run setup-pi.sh first.")
        return

    # boto3 session backed by IoT Credentials Provider (auto-refreshing)
    iot_session = iot_credential_provider.create_session(config)

    # CloudWatch client for heartbeats
    cloudwatch = iot_session.client("cloudwatch", region_name=upload_cfg["region"])
    heartbeat_ref = [health_cfg["interval_seconds"]]

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

    # Subscribe to shadow delta
    delta_topic = f"$aws/things/{thing_name}/shadow/update/delta"
    subscribe_future, _ = connection.subscribe(
        topic=delta_topic, qos=mqtt.QoS.AT_LEAST_ONCE, callback=on_shadow_delta,
    )
    subscribe_future.result(timeout=10)
    logger.info(f"Subscribed to {delta_topic}")

    # Request current shadow to catch any pending delta from while offline
    get_accepted_topic = f"$aws/things/{thing_name}/shadow/get/accepted"
    sub_future, _ = connection.subscribe(
        topic=get_accepted_topic, qos=mqtt.QoS.AT_LEAST_ONCE, callback=on_shadow_get_accepted,
    )
    sub_future.result(timeout=10)
    connection.publish(topic=f"$aws/things/{thing_name}/shadow/get", payload="", qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info("Requested current shadow to check for pending updates")

    # Report initial state
    version = load_version()
    report_full_shadow(connection, thing_name, version, config)

    # Log shipping state
    log_ship_cfg = config.get("log_shipping", {})
    log_ship_interval_ref = [log_ship_cfg.get("batch_interval_seconds", 60)]
    last_log_timestamp = [datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")]

    logger.info(f"Agent started (version={version}). Heartbeat every {heartbeat_ref[0]}s.")

    last_heartbeat = 0
    last_log_ship = 0
    stream_proc = None
    last_command_id = [None]  # list so commands.py can update it

    try:
        while True:
            now = time.time()

            # ── Heartbeat + shadow report ──
            if now - last_heartbeat >= heartbeat_ref[0]:
                try:
                    send_heartbeat(cloudwatch, health_cfg["namespace"], health_cfg["metric_name"])
                except Exception as e:
                    logger.error(f"Failed to send CloudWatch heartbeat: {e}")
                try:
                    version = load_version()
                    is_streaming = stream_proc is not None and stream_proc.poll() is None
                    report_full_shadow(connection, thing_name, version, config, streaming=is_streaming)
                except Exception as e:
                    logger.error(f"Failed to update shadow: {e}")
                last_heartbeat = now

            # ── Shadow dirty flag ──
            if SHADOW_DIRTY_FLAG.exists():
                try:
                    SHADOW_DIRTY_FLAG.unlink(missing_ok=True)
                    version = load_version()
                    is_streaming = stream_proc is not None and stream_proc.poll() is None
                    report_full_shadow(connection, thing_name, version, config, streaming=is_streaming)
                    last_heartbeat = now
                except Exception as e:
                    logger.error(f"Failed to process shadow dirty flag: {e}")

            # ── OTA updates ──
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
                            session=iot_session,
                        )
                    finally:
                        on_shadow_delta.updating = False

            # ── Config changes ──
            if on_shadow_delta.pending_config:
                pending = on_shadow_delta.pending_config
                on_shadow_delta.pending_config = None
                if on_shadow_delta.updating:
                    logger.info("Config change received during OTA — will re-apply on next startup via shadow/get")
                else:
                    was_shipping = config.get("log_shipping", {}).get("enabled", True)
                    apply_remote_config(pending, config, connection, thing_name, heartbeat_ref)
                    new_log_cfg = config.get("log_shipping", {})
                    log_ship_interval_ref[0] = new_log_cfg.get("batch_interval_seconds", 60)
                    if not was_shipping and new_log_cfg.get("enabled", True):
                        last_log_timestamp[0] = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
                        logger.info("Log shipping enabled — shipping logs from now")

            # ── Streaming ──
            if on_shadow_delta.pending_streaming is not None:
                want_stream = on_shadow_delta.pending_streaming
                on_shadow_delta.pending_streaming = None
                if want_stream and stream_proc is None:
                    logger.info("Starting live stream — stopping motion detection")
                    try:
                        subprocess.run(["sudo", "systemctl", "stop", "snoutspotter-motion"], timeout=10)
                    except Exception as e:
                        logger.warning(f"Failed to stop motion service: {e}")
                    stream_proc = subprocess.Popen(
                        [sys.executable, str(INSTALL_DIR / "stream_manager.py")],
                        cwd=str(INSTALL_DIR),
                    )
                    logger.info(f"stream_manager.py started (pid={stream_proc.pid})")
                    update_shadow(connection, thing_name, {"streaming": True})
                elif not want_stream and stream_proc is not None:
                    logger.info("Stopping live stream")
                    stream_proc.terminate()
                    try:
                        stream_proc.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        stream_proc.kill()
                        stream_proc.wait()
                    stream_proc = None
                    logger.info("Restarting motion detection")
                    try:
                        subprocess.run(["sudo", "systemctl", "start", "snoutspotter-motion"], timeout=10)
                    except Exception as e:
                        logger.warning(f"Failed to start motion service: {e}")
                    update_shadow(connection, thing_name, {"streaming": False})

            # ── Stream exit detection ──
            if stream_proc is not None and stream_proc.poll() is not None:
                logger.info(f"stream_manager.py exited (code={stream_proc.returncode})")
                stream_proc = None
                logger.info("Restarting motion detection")
                try:
                    subprocess.run(["sudo", "systemctl", "start", "snoutspotter-motion"], timeout=10)
                except Exception as e:
                    logger.warning(f"Failed to start motion service: {e}")
                payload = json.dumps({"state": {
                    "desired": {"streaming": False},
                    "reported": {"streaming": False}
                }})
                topic = f"$aws/things/{thing_name}/shadow/update"
                connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
                logger.info("Streaming stopped — cleared desired and reported shadow state")

            # ── Commands ──
            if on_shadow_delta.pending_command:
                cmd = on_shadow_delta.pending_command
                on_shadow_delta.pending_command = None
                if on_shadow_delta.updating:
                    logger.info("Command received during OTA — rejecting")
                    update_shadow(connection, thing_name, {
                        "commandResult": {"id": cmd.get("id"), "status": "failed", "error": "OTA in progress"}
                    })
                else:
                    execute_command(cmd, config, connection, thing_name, last_command_id)

            # ── Log shipping ──
            if now - last_log_ship >= log_ship_interval_ref[0]:
                try:
                    collect_and_ship_logs(connection, thing_name, config, last_log_timestamp)
                except Exception as e:
                    logger.error(f"Log shipping error: {e}")
                last_log_ship = now

            time.sleep(5)

    except KeyboardInterrupt:
        logger.info("Shutting down agent...")
        if stream_proc is not None and stream_proc.poll() is None:
            stream_proc.terminate()
            stream_proc.wait(timeout=10)
            try:
                subprocess.run(["sudo", "systemctl", "start", "snoutspotter-motion"], timeout=10)
            except Exception:
                pass
        connection.disconnect().result(timeout=5)


if __name__ == "__main__":
    main()
