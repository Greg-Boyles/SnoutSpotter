"""Remote config application from shadow delta."""

import logging
from pathlib import Path

import yaml

import config_schema
from shadow import update_shadow

logger = logging.getLogger("snout-spotter-agent")

INSTALL_DIR = Path(__file__).parent
STATUS_DIR = Path.home() / ".snoutspotter"
CONFIG_RELOAD_MOTION = STATUS_DIR / "config-reload-motion"
CONFIG_RELOAD_UPLOADER = STATUS_DIR / "config-reload-uploader"
SHADOW_DIRTY_FLAG = STATUS_DIR / "shadow-dirty"


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
                config_schema.apply_to_dict(config, key, value)

            tmp = config_path.with_suffix(".yaml.tmp")
            with open(tmp, "w") as f:
                yaml.dump(file_config, f, default_flow_style=False, allow_unicode=True)
            tmp.rename(config_path)
            logger.info(f"config.yaml updated: {list(valid_changes.keys())}")

            if "health.interval_seconds" in valid_changes:
                heartbeat_ref[0] = valid_changes["health.interval_seconds"]
                logger.info(f"Heartbeat interval updated to {heartbeat_ref[0]}s")

            CONFIG_RELOAD_MOTION.touch(exist_ok=True)
            CONFIG_RELOAD_UPLOADER.touch(exist_ok=True)

        except Exception as e:
            logger.error(f"Failed to write config.yaml: {e}")
            errors["_write"] = str(e)

    reported: dict = {"config": config_schema.get_configurable_values(config)}
    if errors:
        reported["configErrors"] = errors
    update_shadow(connection, thing_name, reported)
    SHADOW_DIRTY_FLAG.touch(exist_ok=True)
