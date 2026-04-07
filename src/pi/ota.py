"""OTA update logic — download, extract, install deps, restart services."""

import hashlib
import json
import logging
import os
import shutil
import subprocess
import tarfile
import time
from datetime import datetime, timezone
from pathlib import Path

import boto3

from health import SERVICES, check_services_healthy
from shadow import update_shadow

logger = logging.getLogger("snout-spotter-agent")

INSTALL_DIR = Path(__file__).parent
BACKUP_DIR = Path.home() / ".snoutspotter" / "backups"


def load_version() -> str:
    try:
        with open(INSTALL_DIR / "version.json") as f:
            return json.load(f).get("version", "unknown")
    except Exception:
        return "unknown"


def save_version(version: str):
    with open(INSTALL_DIR / "version.json", "w") as f:
        json.dump({"version": version, "updated_at": datetime.now(timezone.utc).isoformat()}, f, indent=4)


def backup_current(old_version: str):
    backup_path = BACKUP_DIR / old_version
    backup_path.mkdir(parents=True, exist_ok=True)
    for item in INSTALL_DIR.iterdir():
        if item.name.startswith(".") or item.name == "__pycache__":
            continue
        dest = backup_path / item.name
        if item.is_dir():
            shutil.copytree(item, dest, dirs_exist_ok=True)
        else:
            shutil.copy2(item, dest)
    logger.info(f"Backed up current version ({old_version}) to {backup_path}")


def rollback(old_version: str):
    backup_path = BACKUP_DIR / old_version
    if not backup_path.exists():
        logger.error(f"Backup not found for rollback: {backup_path}")
        return False
    for item in backup_path.iterdir():
        dest = INSTALL_DIR / item.name
        if item.is_dir():
            shutil.copytree(item, dest, dirs_exist_ok=True)
        else:
            shutil.copy2(item, dest)
    logger.info(f"Rolled back to version {old_version}")
    return True


def parse_system_deps(path: Path) -> set[str]:
    if not path.exists():
        return set()
    packages = set()
    for line in path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            packages.add(line)
    return packages


def install_system_deps(old_version: str | None):
    new_deps_path = INSTALL_DIR / "system-deps.txt"
    new_deps = parse_system_deps(new_deps_path)
    if not new_deps:
        return

    old_deps = set()
    if old_version:
        old_deps_path = BACKUP_DIR / old_version / "system-deps.txt"
        old_deps = parse_system_deps(old_deps_path)

    if new_deps == old_deps:
        logger.info("System deps unchanged — skipping apt install")
        return

    added = new_deps - old_deps
    if added:
        logger.info(f"New system packages to install: {added}")
    else:
        logger.info("System deps file changed — ensuring all packages are installed")

    packages = sorted(new_deps)
    logger.info(f"Running apt install for: {packages}")
    subprocess.run(["sudo", "apt-get", "update", "-qq"], timeout=120, check=True)
    subprocess.run(
        ["sudo", "apt-get", "install", "-y", "-qq", "--no-install-recommends"] + packages,
        timeout=600, check=True,
    )
    logger.info("System packages installed successfully")


def parse_custom_debs(path: Path) -> list[str]:
    if not path.exists():
        return []
    entries = []
    for line in path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            entries.append(line)
    return entries


def _deb_package_name(deb_path: str) -> str:
    return Path(deb_path).name.split("_")[0]


def _is_deb_installed(package_name: str) -> bool:
    try:
        result = subprocess.run(
            ["dpkg", "-s", package_name],
            capture_output=True, text=True, timeout=5,
        )
        return "Status: install ok installed" in result.stdout
    except Exception:
        return False


def install_custom_debs(old_version: str | None, bucket: str, region: str, session: boto3.Session = None):
    new_debs_path = INSTALL_DIR / "custom-debs.txt"
    new_debs = parse_custom_debs(new_debs_path)
    if not new_debs:
        return

    old_debs = []
    if old_version:
        old_debs_path = BACKUP_DIR / old_version / "custom-debs.txt"
        old_debs = parse_custom_debs(old_debs_path)

    missing = [p for p in new_debs if not _is_deb_installed(_deb_package_name(p))]

    if new_debs == old_debs and not missing:
        logger.info("Custom debs unchanged and all installed — skipping")
        return

    if missing:
        logger.info(f"Missing packages detected: {[_deb_package_name(p) for p in missing]}")

    s3 = (session or boto3).client("s3", region_name=region)
    for s3_path in (missing if new_debs == old_debs else new_debs):
        deb_name = Path(s3_path).name
        local_path = Path(f"/tmp/{deb_name}")
        try:
            logger.info(f"Downloading s3://{bucket}/{s3_path}")
            s3.download_file(bucket, s3_path, str(local_path))
            logger.info(f"Installing {deb_name}")
            subprocess.run(
                ["sudo", "dpkg", "-i", str(local_path)],
                timeout=120, check=True,
            )
            logger.info(f"Installed {deb_name} successfully")
        except subprocess.CalledProcessError:
            logger.warning(f"dpkg failed for {deb_name} — attempting to fix dependencies")
            subprocess.run(
                ["sudo", "apt-get", "install", "-f", "-y", "-qq"],
                timeout=300, check=True,
            )
        except Exception as e:
            logger.error(f"Failed to install {deb_name}: {e}")
        finally:
            if local_path.exists():
                local_path.unlink()


def verify_checksum(filepath: Path, expected_sha256: str) -> bool:
    """Verify SHA256 checksum of a downloaded file."""
    sha256 = hashlib.sha256()
    with open(filepath, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            sha256.update(chunk)
    actual = sha256.hexdigest()
    if actual != expected_sha256:
        logger.error(f"Checksum mismatch: expected {expected_sha256}, got {actual}")
        return False
    logger.info(f"Checksum verified: {actual}")
    return True


def safe_extract(tar: tarfile.TarFile, dest: Path) -> list[tarfile.TarInfo]:
    """Extract tar members, skipping config.yaml and rejecting path traversal."""
    dest = dest.resolve()
    safe_members = []
    for member in tar.getmembers():
        if member.name in ("./config.yaml", "config.yaml"):
            continue
        member_path = (dest / member.name).resolve()
        if not str(member_path).startswith(str(dest) + os.sep) and member_path != dest:
            logger.warning(f"Skipping tar member with path traversal: {member.name}")
            continue
        safe_members.append(member)
    tar.extractall(path=dest, members=safe_members)
    return safe_members


# Maps service short name to its Python script and description.
# Used to generate systemd service files for new services during OTA.
SERVICE_MANIFEST = {
    "motion":   {"script": "motion_detector.py", "desc": "Motion Detection"},
    "uploader": {"script": "uploader.py",        "desc": "Upload"},
    "agent":    {"script": "agent.py",           "desc": "Health & OTA Agent"},
    "watchdog": {"script": "watchdog.py",        "desc": "Service Watchdog"},
}

SYSTEMD_UNIT_TEMPLATE = """\
[Unit]
Description=SnoutSpotter {desc} Service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User={user}
WorkingDirectory={work_dir}
ExecStart=/usr/bin/python3 {work_dir}/{script}
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
"""


def sync_service_files():
    """Create and enable systemd service files for any services missing from /etc/systemd/system.

    This allows OTA to deliver new services (e.g. watchdog) without requiring
    a full re-run of setup-pi.sh.
    """
    user = os.environ.get("USER", "pi")
    work_dir = str(INSTALL_DIR)
    created = []

    for short_name, info in SERVICE_MANIFEST.items():
        svc_name = f"snoutspotter-{short_name}"
        unit_path = Path(f"/etc/systemd/system/{svc_name}.service")

        if unit_path.exists():
            continue

        # Only create if the script actually exists in the package
        script_path = INSTALL_DIR / info["script"]
        if not script_path.exists():
            continue

        unit_content = SYSTEMD_UNIT_TEMPLATE.format(
            desc=info["desc"],
            user=user,
            work_dir=work_dir,
            script=info["script"],
        )

        tmp_path = Path(f"/tmp/{svc_name}.service")
        tmp_path.write_text(unit_content)
        subprocess.run(["sudo", "cp", str(tmp_path), str(unit_path)], timeout=5, check=True)
        tmp_path.unlink(missing_ok=True)

        subprocess.run(["sudo", "systemctl", "enable", svc_name], timeout=10, check=True)
        created.append(svc_name)
        logger.info(f"Created and enabled new service: {svc_name}")

    if created:
        subprocess.run(["sudo", "systemctl", "daemon-reload"], timeout=10, check=True)
        logger.info(f"Synced {len(created)} new service file(s): {created}")


def restart_services():
    for svc in SERVICES:
        try:
            subprocess.run(["sudo", "systemctl", "restart", svc], timeout=30, check=True)
            logger.info(f"Restarted {svc}")
        except Exception as e:
            logger.error(f"Failed to restart {svc}: {e}")


def apply_update(version: str, bucket: str, region: str, connection, thing_name: str, session: boto3.Session = None):
    old_version = load_version()
    s3_key = f"releases/pi/v{version}.tar.gz"
    local_path = Path(f"/tmp/pi-v{version}.tar.gz")

    logger.info(f"Starting update: {old_version} -> {version}")
    update_shadow(connection, thing_name, {"updateStatus": "updating"})

    try:
        logger.info(f"Downloading s3://{bucket}/{s3_key}")
        s3 = (session or boto3).client("s3", region_name=region)
        s3.download_file(bucket, s3_key, str(local_path))

        # Verify checksum if available (stored as S3 object metadata)
        try:
            head = s3.head_object(Bucket=bucket, Key=s3_key)
            expected_sha256 = head.get("Metadata", {}).get("sha256")
            if expected_sha256:
                if not verify_checksum(local_path, expected_sha256):
                    raise RuntimeError(f"Checksum verification failed for {s3_key}")
            else:
                logger.warning("No sha256 metadata on release — skipping checksum verification")
        except s3.exceptions.ClientError as e:
            logger.warning(f"Could not fetch object metadata for checksum: {e}")

        backup_current(old_version)

        logger.info(f"Extracting to {INSTALL_DIR}")
        with tarfile.open(local_path, "r:gz") as tar:
            safe_extract(tar, INSTALL_DIR)

        install_system_deps(old_version)
        install_custom_debs(old_version, bucket, region, session)

        logger.info("Installing Python dependencies")
        subprocess.run(
            ["pip3", "install", "-r", str(INSTALL_DIR / "requirements.txt"),
             "--break-system-packages", "--quiet"],
            timeout=120, check=True
        )

        save_version(version)
        sync_service_files()
        restart_services()

        logger.info("Waiting 30 seconds for services to stabilize...")
        time.sleep(30)

        if check_services_healthy():
            logger.info(f"Update successful: now running v{version}")
            update_shadow(connection, thing_name, {
                "version": version,
                "updateStatus": "success"
            })
            time.sleep(60)
            update_shadow(connection, thing_name, {"updateStatus": "idle"})
        else:
            raise RuntimeError("Services unhealthy after update")

    except Exception as e:
        logger.error(f"Update failed: {e}")
        logger.info("Attempting rollback...")
        if rollback(old_version):
            save_version(old_version)
            restart_services()
        update_shadow(connection, thing_name, {
            "version": old_version,
            "updateStatus": "failed"
        })
        time.sleep(60)
        update_shadow(connection, thing_name, {"updateStatus": "idle"})
    finally:
        if local_path.exists():
            local_path.unlink()
