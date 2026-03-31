"""IoT Device Shadow building and reporting."""

import json
import logging
import socket
from datetime import datetime, timezone

from awscrt import mqtt

import config_schema
from health import (
    SERVICES, get_camera_status, get_clips_pending, get_last_motion_time,
    get_last_upload_time, get_service_status, get_system_health, get_upload_stats,
)

logger = logging.getLogger("snout-spotter-agent")


def build_shadow_state(version: str, config: dict, streaming: bool = False) -> dict:
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
                "streaming": streaming,
            }
        }
    }


def update_shadow(connection, thing_name: str, reported: dict):
    payload = json.dumps({"state": {"reported": reported}})
    topic = f"$aws/things/{thing_name}/shadow/update"
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Shadow updated: {reported}")


def report_full_shadow(connection, thing_name: str, version: str, config: dict, streaming: bool = False):
    shadow_state = build_shadow_state(version, config, streaming=streaming)
    topic = f"$aws/things/{thing_name}/shadow/update"
    payload = json.dumps(shadow_state)
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Full shadow reported (version={version})")
