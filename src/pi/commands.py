"""Device command execution — commands arrive via MQTT, results reported via shadow."""

import json
import logging
import shutil
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

from awscrt import mqtt
from shadow import update_shadow

logger = logging.getLogger("snout-spotter-agent")

BACKUP_DIR = Path.home() / ".snoutspotter" / "backups"

ALLOWED_ACTIONS = {
    "restart-motion",
    "restart-uploader",
    "restart-agent",
    "reboot",
    "clear-clips",
    "clear-backups",
}


def execute_command(cmd: dict, config: dict, connection, thing_name: str, updating: bool = False) -> None:
    """Execute a device command and report the result via shadow reported state."""
    cmd_id = cmd.get("id")
    action = cmd.get("action")

    if not cmd_id or not action:
        logger.warning(f"Invalid command: missing id or action: {cmd}")
        return

    if updating:
        logger.info(f"Command {action} rejected — OTA in progress")
        _report_result(connection, thing_name, cmd_id, "failed", error="OTA in progress")
        return

    if action not in ALLOWED_ACTIONS:
        logger.warning(f"Unknown command action: {action}")
        _report_result(connection, thing_name, cmd_id, "failed", error=f"Unknown action: {action}")
        return

    logger.info(f"Executing command {cmd_id}: {action}")

    try:
        if action == "reboot":
            _report_result(connection, thing_name, cmd_id, "success", message="Rebooting")
            time.sleep(2)
            subprocess.run(["sudo", "reboot"], timeout=5)

        elif action == "restart-agent":
            _report_result(connection, thing_name, cmd_id, "success", message="snoutspotter-agent restarting")
            time.sleep(2)
            subprocess.run(["sudo", "systemctl", "restart", "snoutspotter-agent"], timeout=30)

        elif action.startswith("restart-"):
            svc_name = action.replace("restart-", "")
            svc = f"snoutspotter-{svc_name}"
            subprocess.run(["sudo", "systemctl", "restart", svc], timeout=30, check=True)
            _report_result(connection, thing_name, cmd_id, "success", message=f"{svc} restarted")

        elif action == "clear-clips":
            clips_dir = Path(config.get("recording", {}).get("output_dir", "/home/admin/clips"))
            count = 0
            if clips_dir.exists():
                for f in clips_dir.glob("*.mp4"):
                    f.unlink()
                    count += 1
            _report_result(connection, thing_name, cmd_id, "success", message=f"Deleted {count} clips")

        elif action == "clear-backups":
            freed = 0
            if BACKUP_DIR.exists():
                for d in BACKUP_DIR.iterdir():
                    if d.is_dir():
                        size = sum(f.stat().st_size for f in d.rglob("*") if f.is_file())
                        freed += size
                        shutil.rmtree(d)
                freed_mb = round(freed / (1024 * 1024), 1)
            else:
                freed_mb = 0
            _report_result(connection, thing_name, cmd_id, "success", message=f"Freed {freed_mb} MB")

    except Exception as e:
        logger.error(f"Command {action} failed: {e}")
        _report_result(connection, thing_name, cmd_id, "failed", error=str(e))


def _report_result(connection, thing_name: str, cmd_id: str, status: str,
                   message: str = "", error: str = ""):
    """Report command result via shadow reported state only — no desired state touched."""
    result: dict = {
        "id": cmd_id,
        "status": status,
        "completedAt": datetime.now(timezone.utc).isoformat(),
    }
    if message:
        result["message"] = message
    if error:
        result["error"] = error
    update_shadow(connection, thing_name, {"commandResult": result})
