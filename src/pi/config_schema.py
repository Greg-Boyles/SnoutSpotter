"""
Allow-list of remotely configurable Pi settings and helpers for validation,
applying changes to config dicts, and reading current values.

Keys use dot-notation: "section.field" maps to config["section"]["field"].
Camera resolution keys are intentionally excluded — changing them requires
a Picamera2 pipeline restart which is outside the scope of hot-reload.
"""

from typing import Any

CONFIGURABLE_KEYS: dict[str, dict] = {
    "motion.threshold": {
        "type": int, "min": 500, "max": 50000,
        "affects": "motion",
    },
    "motion.blur_kernel": {
        "type": int, "min": 3, "max": 51, "odd": True,
        "affects": "motion",
    },
    "camera.detection_fps": {
        "type": int, "min": 1, "max": 15,
        "affects": "motion",
    },
    "recording.max_clip_length": {
        "type": int, "min": 10, "max": 300,
        "affects": "motion",
    },
    "recording.pre_buffer": {
        "type": int, "min": 1, "max": 10,
        "affects": "motion",
    },
    "recording.post_motion_buffer": {
        "type": int, "min": 3, "max": 60,
        "affects": "motion",
    },
    "upload.max_retries": {
        "type": int, "min": 1, "max": 20,
        "affects": "uploader",
    },
    "upload.delete_after_upload": {
        "type": bool,
        "affects": "uploader",
    },
    "health.interval_seconds": {
        "type": int, "min": 60, "max": 3600,
        "affects": "agent",
    },
    "log_shipping.enabled": {
        "type": bool,
        "affects": "agent",
    },
    "log_shipping.batch_interval_seconds": {
        "type": int, "min": 30, "max": 600,
        "affects": "agent",
    },
    "log_shipping.max_lines_per_batch": {
        "type": int, "min": 10, "max": 200,
        "affects": "agent",
    },
    "log_shipping.min_level": {
        "type": str, "choices": ["DEBUG", "INFO", "WARNING", "ERROR"],
        "affects": "agent",
    },
    "credentials_provider.endpoint": {
        "type": str,
        "affects": "agent",
    },
}


def validate_config_value(key: str, value: Any) -> tuple[bool, str]:
    """Validate a single config key/value. Returns (ok, error_message)."""
    if key not in CONFIGURABLE_KEYS:
        return False, f"Unknown config key: {key}"

    spec = CONFIGURABLE_KEYS[key]
    expected_type = spec["type"]

    if expected_type is bool:
        if not isinstance(value, bool):
            return False, f"{key} must be a boolean"
    elif expected_type is str:
        if not isinstance(value, str):
            return False, f"{key} must be a string"
        choices = spec.get("choices")
        if choices and value not in choices:
            return False, f"{key} must be one of: {', '.join(choices)}"
    elif expected_type is int:
        if not isinstance(value, int) or isinstance(value, bool):
            return False, f"{key} must be an integer"
        min_val = spec.get("min")
        max_val = spec.get("max")
        if min_val is not None and value < min_val:
            return False, f"{key} must be >= {min_val}"
        if max_val is not None and value > max_val:
            return False, f"{key} must be <= {max_val}"
        if spec.get("odd") and value % 2 == 0:
            return False, f"{key} must be an odd number"

    return True, ""


def apply_to_dict(config_dict: dict, key: str, value: Any) -> None:
    """Write a dot-notation key/value into a nested config dict in-place."""
    section, field = key.split(".", 1)
    if section not in config_dict:
        config_dict[section] = {}
    config_dict[section][field] = value


def get_configurable_values(config_dict: dict) -> dict:
    """Extract current values for all configurable keys from a config dict."""
    result = {}
    for key in CONFIGURABLE_KEYS:
        section, field = key.split(".", 1)
        section_data = config_dict.get(section, {})
        if field in section_data:
            result[key] = section_data[field]
    return result
