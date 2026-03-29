"""Shared config loading: deep-merges defaults.yaml with device-specific config.yaml overrides."""

from pathlib import Path
import yaml

INSTALL_DIR = Path(__file__).parent


def deep_merge(base: dict, overrides: dict) -> dict:
    """Recursively merge overrides into base dict. Modifies base in-place."""
    for key, value in overrides.items():
        if key in base and isinstance(base[key], dict) and isinstance(value, dict):
            deep_merge(base[key], value)
        else:
            base[key] = value
    return base


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

    return config
