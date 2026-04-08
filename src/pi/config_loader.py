"""Shared config loading: deep-merges defaults.yaml with device-specific config.yaml overrides."""

import logging
from pathlib import Path

import yaml

logger = logging.getLogger(__name__)

INSTALL_DIR = Path(__file__).parent

# Required config sections and their required keys with expected types.
# Only validates structure — value ranges are checked by config_schema.py.
REQUIRED_SCHEMA: dict[str, dict[str, type]] = {
    "motion": {"threshold": int, "blur_kernel": int, "min_area": int},
    "camera": {"preview_resolution": str, "record_resolution": str, "detection_fps": int, "record_fps": int},
    "recording": {"output_dir": str, "max_clip_length": int, "post_motion_buffer": int},
    "upload": {"bucket_name": str, "region": str, "prefix": str, "max_retries": int},
    "health": {"namespace": str, "metric_name": str, "interval_seconds": int},
    "iot": {"endpoint": str, "thing_name": str, "cert_path": str, "key_path": str, "root_ca_path": str},
}


def deep_merge(base: dict, overrides: dict) -> dict:
    """Recursively merge overrides into base dict. Modifies base in-place."""
    for key, value in overrides.items():
        if key in base and isinstance(base[key], dict) and isinstance(value, dict):
            deep_merge(base[key], value)
        else:
            base[key] = value
    return base


def validate_config(config: dict) -> list[str]:
    """Validate that required sections and keys exist with correct types.

    Returns a list of error messages (empty if valid).
    """
    errors = []
    for section, keys in REQUIRED_SCHEMA.items():
        if section not in config:
            errors.append(f"Missing required config section: {section}")
            continue
        if not isinstance(config[section], dict):
            errors.append(f"Config section '{section}' must be a dict, got {type(config[section]).__name__}")
            continue
        for key, expected_type in keys.items():
            if key not in config[section]:
                errors.append(f"Missing required key: {section}.{key}")
            elif not isinstance(config[section][key], expected_type):
                actual = type(config[section][key]).__name__
                errors.append(f"Wrong type for {section}.{key}: expected {expected_type.__name__}, got {actual}")
    return errors


def load_config() -> dict:
    """Load defaults.yaml then overlay config.yaml overrides on top."""
    defaults_path = INSTALL_DIR / "defaults.yaml"
    config_path = INSTALL_DIR / "config.yaml"

    config = {}
    if defaults_path.exists():
        with open(defaults_path) as f:
            config = yaml.safe_load(f) or {}

    if config_path.exists():
        with open(config_path) as f:
            overrides = yaml.safe_load(f) or {}
        deep_merge(config, overrides)

    # Auto-convert legacy list resolutions to strings (e.g. [1920, 1080] → "1920x1080")
    for section, key in [("camera", "preview_resolution"), ("camera", "record_resolution"), ("streaming", "resolution")]:
        val = config.get(section, {}).get(key)
        if isinstance(val, list) and len(val) == 2:
            config[section][key] = f"{val[0]}x{val[1]}"
            logger.info(f"Auto-converted {section}.{key} from list to string: {config[section][key]}")

    errors = validate_config(config)
    if errors:
        for err in errors:
            logger.error(f"Config validation: {err}")
        raise ValueError(f"Invalid configuration ({len(errors)} errors): {'; '.join(errors)}")

    return config
