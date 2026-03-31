"""Device command execution from shadow delta."""

import json
import logging
import shutil
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

from awscrt import mqtt

logger = logging.getLogger("snout-spotter-agent")

BACKUP_DIR = Path.home() / ".snoutspotter" / "backups"
STALE_THRESHOLD_SECONDS = 600  # 10 minutes

ALLOWED_ACTIONS = {
    "restart-motion",
    "restart-uploader",
    "restart-agent",
    "reboot",
    "clear-clips",
    "clear-backups",
}


def execute_command(cmd: dict, config: dict, connection, thing_name: str, last_command_id: list) -> None:
    """Execute a device command and report the result via shadow."""
    cmd_id = cmd.get("id")
    action = cmd.get("action")
    requested_at = cmd.get("requestedAt", "")

    if not cmd_id or not action:
        logger.warning(f"Invalid command: missing id or action: {cmd}")
        return

    # Skip duplicate
    if cmd_id == last_command_id[0]:
        logger.info(f"Skipping duplicate command {cmd_id}")
        return

    # Skip stale
    if requested_at:
        try:
            req_time = datetime.fromisoformat(requested_at.replace("Z", "+00:00"))
            age = (datetime.now(timezone.utc) - req_time).total_seconds()
            if age > STALE_THRESHOLD_SECONDS:
                logger.info(f"Skipping stale command {cmd_id} ({action}, {int(age)}s old)")
                _report_result(connection, thing_name, cmd_id, "skipped", message="Command too old")
                last_command_id[0] = cmd_id
                return
        except (ValueError, TypeError):
            pass

    if action not in ALLOWED_ACTIONS:
        logger.warning(f"Unknown command action: {action}")
        _report_result(connection, thing_name, cmd_id, "failed", error=f"Unknown action: {action}")
        last_command_id[0] = cmd_id
        return

    logger.info(f"Executing command {cmd_id}: {action}")

    try:
        if action == "reboot":
            _report_result(connection, thing_name, cmd_id, "success", message="Rebooting")
            last_command_id[0] = cmd_id
            time.sleep(2)
            subprocess.run(["sudo", "reboot"], timeout=5)

        elif action == "restart-agent":
            # Report before restarting — agent process will die during restart
            _report_result(connection, thing_name, cmd_id, "success", message="snoutspotter-agent restarting")
            last_command_id[0] = cmd_id
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

        last_command_id[0] = cmd_id

    except Exception as e:
        logger.error(f"Command {action} failed: {e}")
        _report_result(connection, thing_name, cmd_id, "failed", error=str(e))
        last_command_id[0] = cmd_id


def _report_result(connection, thing_name: str, cmd_id: str, status: str,
                   message: str = "", error: str = ""):
    """Report command result AND clear desired.command in one shadow update to prevent delta loops."""
    result: dict = {
        "id": cmd_id,
        "status": status,
        "completedAt": datetime.now(timezone.utc).isoformat(),
    }
    if message:
        result["message"] = message
    if error:
        result["error"] = error

    # Set desired.command to null + reported.commandResult in one atomic update
    payload = json.dumps({
        "state": {
            "desired": {"command": None},
            "reported": {"commandResult": result}
        }
    })
    topic = f"$aws/things/{thing_name}/shadow/update"
    connection.publish(topic=topic, payload=payload, qos=mqtt.QoS.AT_LEAST_ONCE)
    logger.info(f"Command result reported and desired.command cleared: {status}")
