#!/usr/bin/env python3
"""SnoutSpotter Pi agent — thin orchestrator for MQTT connection and shadow delta dispatch."""

import json
import os
import queue
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


# ── Event types for the main-loop queue ──────────────────────────────

EVENT_OTA = "ota"
EVENT_CONFIG = "config"
EVENT_STREAMING = "streaming"
EVENT_COMMAND = "command"


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


# ── MQTT connection manager ──────────────────────────────────────────

class MqttManager:
    """Wraps the CRT MQTT connection with reconnect tracking and re-subscription."""

    def __init__(self, endpoint, cert_path, key_path, root_ca_path, thing_name):
        self.thing_name = thing_name
        self.connected = False
        self._subscriptions = []  # list of (topic, qos, callback)

        self.connection = mqtt_connection_builder.mtls_from_path(
            endpoint=endpoint,
            cert_filepath=cert_path,
            pri_key_filepath=key_path,
            ca_filepath=root_ca_path,
            client_id=thing_name,
            clean_session=False,
            keep_alive_secs=30,
            on_connection_interrupted=self._on_interrupted,
            on_connection_resumed=self._on_resumed,
        )

    def connect(self, timeout=10):
        self.connection.connect().result(timeout=timeout)
        self.connected = True
        logger.info("Connected to IoT Core")

    def _on_interrupted(self, connection, error, **kwargs):
        self.connected = False
        logger.warning(f"MQTT connection interrupted: {error}")

    def _on_resumed(self, connection, return_code, session_present, **kwargs):
        self.connected = True
        logger.info(f"MQTT connection resumed (session_present={session_present})")
        if not session_present:
            self._resubscribe()

    def subscribe(self, topic, qos, callback):
        self._subscriptions.append((topic, qos, callback))
        future, _ = self.connection.subscribe(topic=topic, qos=qos, callback=callback)
        future.result(timeout=10)
        logger.info(f"Subscribed to {topic}")

    def _resubscribe(self):
        logger.info(f"Re-subscribing to {len(self._subscriptions)} topics after reconnect")
        for topic, qos, callback in self._subscriptions:
            try:
                future, _ = self.connection.subscribe(topic=topic, qos=qos, callback=callback)
                future.result(timeout=10)
                logger.info(f"Re-subscribed to {topic}")
            except Exception as e:
                logger.error(f"Failed to re-subscribe to {topic}: {e}")

    def publish(self, topic, payload, qos=mqtt.QoS.AT_LEAST_ONCE):
        self.connection.publish(topic=topic, payload=payload, qos=qos)

    def disconnect(self, timeout=5):
        self.connection.disconnect().result(timeout=timeout)
        self.connected = False


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

    # Thread-safe event queue — MQTT callbacks enqueue, main loop dequeues
    events = queue.Queue()
    updating = False  # only read/written from main loop

    # ── MQTT connection with reconnect tracking ──
    mqttm = MqttManager(endpoint, cert_path, key_path, root_ca_path, thing_name)
    mqttm.connect()

    # ── Shadow delta callback ──
    def on_shadow_delta(topic, payload, **kwargs):
        try:
            delta = json.loads(payload)
            state = delta.get("state", {})

            desired_version = state.get("version")
            if desired_version:
                current_version = load_version()
                if desired_version != current_version:
                    logger.info(f"Shadow delta: OTA requested {current_version} -> {desired_version}")
                    events.put((EVENT_OTA, desired_version))
                else:
                    logger.info(f"Shadow delta version {desired_version} matches current — ignoring")

            desired_config = state.get("config")
            if desired_config:
                logger.info(f"Shadow delta: config change requested for {list(desired_config.keys())}")
                events.put((EVENT_CONFIG, desired_config))

            if "streaming" in state:
                logger.info(f"Shadow delta: streaming={'start' if state['streaming'] else 'stop'}")
                events.put((EVENT_STREAMING, state["streaming"]))

        except Exception as e:
            logger.error(f"Error processing shadow delta: {e}")

    # ── Shadow get/accepted callback (startup catch-up) ──
    def on_shadow_get_accepted(topic, payload, **kwargs):
        try:
            shadow = json.loads(payload)
            state = shadow.get("state", {})
            delta = state.get("delta", {})

            desired_version = delta.get("version")
            if desired_version:
                current_version = load_version()
                if desired_version != current_version:
                    logger.info(f"Pending OTA delta on startup: {current_version} -> {desired_version}")
                    events.put((EVENT_OTA, desired_version))
                else:
                    logger.info("Shadow delta version matches current — no OTA needed")

            desired_config = delta.get("config")
            if desired_config:
                logger.info(f"Pending config delta on startup: {list(desired_config.keys())}")
                events.put((EVENT_CONFIG, desired_config))

            if "streaming" in delta:
                logger.info(f"Pending streaming delta on startup: {delta['streaming']}")
                events.put((EVENT_STREAMING, delta["streaming"]))

            if not desired_version and not desired_config and "streaming" not in delta:
                logger.info("No pending shadow delta")

        except Exception as e:
            logger.error(f"Error processing shadow get response: {e}")

    # ── Command callback ──
    def on_command(topic, payload, **kwargs):
        try:
            cmd = json.loads(payload)
            logger.info(f"Command received via MQTT: {cmd.get('action')}")
            events.put((EVENT_COMMAND, cmd))
        except Exception as e:
            logger.error(f"Error parsing command: {e}")

    # ── Subscribe to topics ──
    delta_topic = f"$aws/things/{thing_name}/shadow/update/delta"
    mqttm.subscribe(delta_topic, mqtt.QoS.AT_LEAST_ONCE, on_shadow_delta)

    get_accepted_topic = f"$aws/things/{thing_name}/shadow/get/accepted"
    mqttm.subscribe(get_accepted_topic, mqtt.QoS.AT_LEAST_ONCE, on_shadow_get_accepted)

    mqttm.publish(f"$aws/things/{thing_name}/shadow/get", "")
    logger.info("Requested current shadow to check for pending updates")

    cmd_topic = f"snoutspotter/{thing_name}/commands"
    mqttm.subscribe(cmd_topic, mqtt.QoS.AT_LEAST_ONCE, on_command)

    # Report initial state
    version = load_version()
    report_full_shadow(mqttm.connection, thing_name, version, config)

    # Log shipping state
    log_ship_cfg = config.get("log_shipping", {})
    log_ship_interval_ref = [log_ship_cfg.get("batch_interval_seconds", 60)]
    last_log_timestamp = [datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")]

    logger.info(f"Agent started (version={version}). Heartbeat every {heartbeat_ref[0]}s.")

    last_heartbeat = 0
    last_log_ship = 0
    stream_proc = None

    try:
        while True:
            now = time.time()

            # ── Heartbeat + shadow report ──
            if now - last_heartbeat >= heartbeat_ref[0]:
                if mqttm.connected:
                    try:
                        send_heartbeat(cloudwatch, health_cfg["namespace"], health_cfg["metric_name"])
                    except Exception as e:
                        logger.error(f"Failed to send CloudWatch heartbeat: {e}")
                    try:
                        version = load_version()
                        is_streaming = stream_proc is not None and stream_proc.poll() is None
                        report_full_shadow(mqttm.connection, thing_name, version, config, streaming=is_streaming)
                    except Exception as e:
                        logger.error(f"Failed to update shadow: {e}")
                else:
                    logger.warning("Skipping heartbeat/shadow — MQTT disconnected")
                last_heartbeat = now

            # ── Shadow dirty flag ──
            if SHADOW_DIRTY_FLAG.exists():
                try:
                    SHADOW_DIRTY_FLAG.unlink(missing_ok=True)
                    version = load_version()
                    is_streaming = stream_proc is not None and stream_proc.poll() is None
                    report_full_shadow(mqttm.connection, thing_name, version, config, streaming=is_streaming)
                    last_heartbeat = now
                except Exception as e:
                    logger.error(f"Failed to process shadow dirty flag: {e}")

            # ── Drain event queue ──
            while not events.empty():
                try:
                    event_type, data = events.get_nowait()
                except queue.Empty:
                    break

                # ── OTA updates ──
                if event_type == EVENT_OTA:
                    if data == load_version():
                        logger.info(f"Skipping update to {data} — already running this version")
                    else:
                        updating = True
                        try:
                            apply_update(
                                version=data,
                                bucket=upload_cfg["bucket_name"],
                                region=upload_cfg["region"],
                                connection=mqttm.connection,
                                thing_name=thing_name,
                                session=iot_session,
                            )
                        finally:
                            updating = False

                # ── Config changes ──
                elif event_type == EVENT_CONFIG:
                    if updating:
                        logger.info("Config change received during OTA — will re-apply on next startup via shadow/get")
                    else:
                        was_shipping = config.get("log_shipping", {}).get("enabled", True)
                        apply_remote_config(data, config, mqttm.connection, thing_name, heartbeat_ref)
                        new_log_cfg = config.get("log_shipping", {})
                        log_ship_interval_ref[0] = new_log_cfg.get("batch_interval_seconds", 60)
                        if not was_shipping and new_log_cfg.get("enabled", True):
                            last_log_timestamp[0] = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
                            logger.info("Log shipping enabled — shipping logs from now")

                # ── Streaming ──
                elif event_type == EVENT_STREAMING:
                    want_stream = data
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
                        update_shadow(mqttm.connection, thing_name, {"streaming": True})
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
                        update_shadow(mqttm.connection, thing_name, {"streaming": False})

                # ── Commands ──
                elif event_type == EVENT_COMMAND:
                    execute_command(data, config, mqttm.connection, thing_name, updating=updating)

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
                mqttm.publish(topic, payload)
                logger.info("Streaming stopped — cleared desired and reported shadow state")

            # ── Log shipping ──
            if now - last_log_ship >= log_ship_interval_ref[0]:
                if mqttm.connected:
                    try:
                        collect_and_ship_logs(mqttm.connection, thing_name, config, last_log_timestamp)
                    except Exception as e:
                        logger.error(f"Log shipping error: {e}")
                last_log_ship = now

            time.sleep(5)

    except KeyboardInterrupt:
        logger.info("Shutting down agent...")
        if stream_proc is not None and stream_proc.poll() is None:
            stream_proc.terminate()
            try:
                stream_proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                stream_proc.kill()
                stream_proc.wait()
            try:
                subprocess.run(["sudo", "systemctl", "start", "snoutspotter-motion"], timeout=10)
            except Exception:
                pass
        mqttm.disconnect()


if __name__ == "__main__":
    main()
