"""OTA update logic — download, extract, install deps, restart services."""

import json
import logging
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

        backup_current(old_version)

        logger.info(f"Extracting to {INSTALL_DIR}")
        with tarfile.open(local_path, "r:gz") as tar:
            members = [m for m in tar.getmembers() if m.name not in ("./config.yaml", "config.yaml")]
            tar.extractall(path=INSTALL_DIR, members=members)

        install_system_deps(old_version)
        install_custom_debs(old_version, bucket, region, session)

        logger.info("Installing Python dependencies")
        subprocess.run(
            ["pip3", "install", "-r", str(INSTALL_DIR / "requirements.txt"),
             "--break-system-packages", "--quiet"],
            timeout=120, check=True
        )

        save_version(version)
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
