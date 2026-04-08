"""System health and status gathering for shadow reporting."""

import json
import os
import platform
import re
import shutil
import socket
import sqlite3
import subprocess
from datetime import datetime, timezone
from pathlib import Path

STATUS_DIR = Path.home() / ".snoutspotter"
SERVICES = ["snoutspotter-motion", "snoutspotter-uploader", "snoutspotter-agent", "snoutspotter-watchdog"]

# Cached values that don't change at runtime
_cached_pi_model = None
_cached_python_version = None
_cached_sensor_model = None


def get_service_status(service_name: str) -> str:
    try:
        result = subprocess.run(
            ["systemctl", "is-active", service_name],
            capture_output=True, text=True, timeout=5
        )
        return result.stdout.strip()
    except Exception:
        return "unknown"


def check_services_healthy() -> bool:
    for svc in SERVICES:
        try:
            result = subprocess.run(
                ["systemctl", "is-active", svc],
                capture_output=True, text=True, timeout=5
            )
            if result.stdout.strip() != "active":
                return False
        except Exception:
            return False
    return True


def _read_status_file(filename: str) -> dict | None:
    try:
        path = STATUS_DIR / filename
        if path.exists():
            return json.loads(path.read_text())
    except Exception:
        pass
    return None


def get_camera_status(config: dict) -> dict:
    global _cached_sensor_model
    try:
        connected = os.path.exists("/dev/video0")
        healthy = connected

        motion_status = _read_status_file("motion-status.json")
        if motion_status is not None:
            healthy = motion_status.get("cameraOk", connected)

        result = {"connected": connected, "healthy": healthy}

        if _cached_sensor_model is None:
            try:
                out = subprocess.run(
                    ["journalctl", "-u", "snoutspotter-motion", "--no-pager", "-n", "50"],
                    capture_output=True, text=True, timeout=5
                )
                match = re.search(r"Sensor: .*/(\w+)@", out.stdout)
                if match:
                    _cached_sensor_model = match.group(1)
            except Exception:
                pass
        if _cached_sensor_model:
            result["sensor"] = _cached_sensor_model

        if connected:
            try:
                out = subprocess.run(
                    ["v4l2-ctl", "-d", "/dev/video0", "--get-fmt-video"],
                    capture_output=True, text=True, timeout=5
                )
                match = re.search(r"Width/Height\s*:\s*(\d+/\d+)", out.stdout)
                if match:
                    result["resolution"] = match.group(1).replace("/", "x")
            except Exception:
                pass

        rec_res = config.get("camera", {}).get("record_resolution")
        if rec_res:
            result["recordResolution"] = rec_res if isinstance(rec_res, str) else f"{rec_res[0]}x{rec_res[1]}"

        return result
    except Exception:
        return {"connected": False, "healthy": False}


def get_last_motion_time() -> str | None:
    try:
        status = _read_status_file("motion-status.json")
        if status:
            return status.get("lastMotionAt")
    except Exception:
        pass
    return None


def get_last_upload_time() -> str | None:
    try:
        db_path = STATUS_DIR / "uploads.db"
        if not db_path.exists():
            return None
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        cursor = conn.execute("SELECT MAX(uploaded_at) FROM uploads WHERE status = 'uploaded'")
        row = cursor.fetchone()
        conn.close()
        return row[0] if row and row[0] else None
    except Exception:
        return None


def get_upload_stats() -> dict:
    try:
        db_path = STATUS_DIR / "uploads.db"
        if not db_path.exists():
            return {"uploadsToday": 0, "failedToday": 0, "totalUploaded": 0}
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")

        uploaded_today = conn.execute(
            "SELECT COUNT(*) FROM uploads WHERE status = 'uploaded' AND uploaded_at >= ?",
            (today,)
        ).fetchone()[0]

        failed_today = conn.execute(
            "SELECT COUNT(*) FROM uploads WHERE status = 'failed' AND last_attempt >= ?",
            (today,)
        ).fetchone()[0]

        total = conn.execute(
            "SELECT COUNT(*) FROM uploads WHERE status = 'uploaded'"
        ).fetchone()[0]

        conn.close()
        return {"uploadsToday": uploaded_today, "failedToday": failed_today, "totalUploaded": total}
    except Exception:
        return {"uploadsToday": 0, "failedToday": 0, "totalUploaded": 0}


def get_clips_pending(config: dict) -> int:
    try:
        clips_dir = Path(config.get("recording", {}).get("output_dir", "/home/admin/clips"))
        if clips_dir.exists():
            return len(list(clips_dir.glob("*.mp4")))
    except Exception:
        pass
    return 0


def get_system_health() -> dict:
    global _cached_pi_model, _cached_python_version
    result = {}

    try:
        temp = Path("/sys/class/thermal/thermal_zone0/temp").read_text().strip()
        result["cpuTempC"] = round(int(temp) / 1000, 1)
    except Exception:
        pass

    try:
        meminfo = Path("/proc/meminfo").read_text()
        total = int(re.search(r"MemTotal:\s+(\d+)", meminfo).group(1))
        available = int(re.search(r"MemAvailable:\s+(\d+)", meminfo).group(1))
        result["memUsedPercent"] = round((1 - available / total) * 100, 1)
    except Exception:
        pass

    try:
        usage = shutil.disk_usage("/")
        result["diskUsedPercent"] = round(usage.used / usage.total * 100, 1)
        result["diskFreeGb"] = round(usage.free / (1024 ** 3), 1)
    except Exception:
        pass

    try:
        uptime_str = Path("/proc/uptime").read_text().strip().split()[0]
        result["uptimeSeconds"] = int(float(uptime_str))
    except Exception:
        pass

    try:
        result["loadAvg"] = [round(x, 2) for x in os.getloadavg()]
    except Exception:
        pass

    if _cached_pi_model is None:
        try:
            _cached_pi_model = Path("/proc/device-tree/model").read_text().strip().rstrip("\x00")
        except Exception:
            _cached_pi_model = ""
    if _cached_pi_model:
        result["piModel"] = _cached_pi_model

    if _cached_python_version is None:
        _cached_python_version = platform.python_version()
    result["pythonVersion"] = _cached_python_version

    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        result["ipAddress"] = s.getsockname()[0]
        s.close()
    except Exception:
        pass

    try:
        wireless = Path("/proc/net/wireless").read_text()
        lines = wireless.strip().split("\n")
        if len(lines) >= 3:
            parts = lines[2].split()
            result["wifiSignalDbm"] = int(float(parts[3]))
    except Exception:
        pass

    try:
        out = subprocess.run(["iwgetid", "-r"], capture_output=True, text=True, timeout=5)
        ssid = out.stdout.strip()
        if ssid:
            result["wifiSsid"] = ssid
    except Exception:
        pass

    return result
