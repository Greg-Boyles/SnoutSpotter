#!/usr/bin/env python3
"""SnoutSpotter live stream manager — spawns a GStreamer/kvssink pipeline on demand."""

import logging
import os
import signal
import subprocess
import time
from pathlib import Path

import config_loader
import iot_credential_provider

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("snout-spotter-stream")


def _build_pipeline_cmd(thing_name: str, config: dict) -> str:
    stream_cfg = config.get("streaming", {})
    res = stream_cfg.get("resolution", [640, 480])
    fps = stream_cfg.get("framerate", 15)
    bitrate = stream_cfg.get("bitrate", 800)
    region = stream_cfg.get("kvs_region", "eu-west-1")
    stream_name = f"snoutspotter-{thing_name.replace('snoutspotter-', '')}-live"

    return (
        f"gst-launch-1.0 -e "
        f"libcamerasrc ! "
        f"video/x-raw,width={res[0]},height={res[1]},framerate={fps}/1,format=I420 ! "
        f"videoconvert ! "
        f"x264enc speed-preset=ultrafast tune=zerolatency byte-stream=true "
        f"key-int-max={fps * 3} bitrate={bitrate} ! "
        f"h264parse ! "
        f"video/x-h264,stream-format=avc,alignment=au ! "
        f'kvssink stream-name="{stream_name}" aws-region="{region}"'
    )


def _get_aws_env(config: dict) -> dict:
    """Get environment variables with AWS credentials for kvssink."""
    env = os.environ.copy()
    creds = iot_credential_provider.get_raw_credentials(config)
    if creds:
        env["AWS_ACCESS_KEY_ID"] = creds["access_key"]
        env["AWS_SECRET_ACCESS_KEY"] = creds["secret_key"]
        env["AWS_SESSION_TOKEN"] = creds["token"]
    return env


MAX_RESTARTS = 5


def _launch(cmd: str, env: dict) -> subprocess.Popen:
    return subprocess.Popen(cmd, shell=True, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)


def main():
    config = config_loader.load_config()
    iot_cfg = config.get("iot", {})
    thing_name = iot_cfg.get("thing_name", "")
    os.environ["IOT_THING_NAME"] = thing_name

    stream_cfg = config.get("streaming", {})
    timeout = stream_cfg.get("timeout_seconds", 600)

    cmd = _build_pipeline_cmd(thing_name, config)
    logger.info(f"Pipeline: {cmd}")

    shutdown = False

    def handle_signal(signum, frame):
        nonlocal shutdown
        logger.info(f"Received signal {signum}, shutting down")
        shutdown = True

    signal.signal(signal.SIGTERM, handle_signal)
    signal.signal(signal.SIGINT, handle_signal)

    start_time = time.time()
    cred_refresh_time = start_time

    env = _get_aws_env(config)
    proc = _launch(cmd, env)
    logger.info(f"GStreamer started (pid={proc.pid}), timeout={timeout}s")
    restart_count = 0

    try:
        while not shutdown:
            if time.time() - start_time >= timeout:
                logger.info(f"Stream timeout reached ({timeout}s)")
                break

            # Refresh credentials every 45 minutes (tokens last 1 hour)
            if time.time() - cred_refresh_time >= 2700:
                try:
                    env = _get_aws_env(config)
                    cred_refresh_time = time.time()
                    logger.info("Refreshed AWS credentials for next pipeline restart")
                except Exception as e:
                    logger.warning(f"Failed to refresh credentials: {e}")

            ret = proc.poll()
            if ret is not None:
                if proc.stderr:
                    stderr_output = proc.stderr.read().decode(errors="replace").strip()
                    for line in stderr_output.splitlines()[-10:]:
                        logger.error(f"GStreamer: {line}")
                logger.warning(f"GStreamer exited with code {ret}")
                if shutdown:
                    break
                restart_count += 1
                if restart_count > MAX_RESTARTS:
                    logger.error(f"GStreamer failed {restart_count} times, giving up")
                    break
                logger.info(f"Restarting GStreamer pipeline (attempt {restart_count}/{MAX_RESTARTS})...")
                time.sleep(2)
                env = _get_aws_env(config)
                cred_refresh_time = time.time()
                proc = _launch(cmd, env)
                logger.info(f"GStreamer restarted (pid={proc.pid})")

            time.sleep(1)
    finally:
        if proc.poll() is None:
            logger.info("Stopping GStreamer pipeline...")
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        logger.info("Stream manager exiting")


if __name__ == "__main__":
    main()
