#!/usr/bin/env python3
"""SnoutSpotter motion detection and recording service for Pi Zero 2 W."""

import json
import os
import time
import logging
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np
import yaml

try:
    from picamera2 import Picamera2
    from picamera2.encoders import H264Encoder
    from picamera2.outputs import CircularOutput, FfmpegOutput
except ImportError:
    Picamera2 = None  # Allow importing on non-Pi systems for testing

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
logger = logging.getLogger("snout-spotter")

STATUS_DIR = Path.home() / ".snoutspotter"
STATUS_FILE = STATUS_DIR / "motion-status.json"
SHADOW_DIRTY_FLAG = STATUS_DIR / "shadow-dirty"
CONFIG_RELOAD_FLAG = STATUS_DIR / "config-reload-motion"
import config_loader


def load_config(path: str | None = None) -> dict:
    return config_loader.load_config()


class MotionDetector:
    def __init__(self, config: dict):
        self.config = config
        self.motion_cfg = config["motion"]
        self.camera_cfg = config["camera"]
        self.record_cfg = config["recording"]

        self.output_dir = Path(self.record_cfg["output_dir"])
        self.output_dir.mkdir(parents=True, exist_ok=True)

        self.prev_frame = None
        self.recording = False
        self.record_start_time = 0.0
        self.last_motion_time = 0.0
        self.picam2 = None
        self.encoder = None
        self.circular_output = None
        self._raw_h264_path = None

        # Status tracking
        self._camera_ok = False
        self._last_motion_at = None
        self._last_recording_started = None
        self._last_recording_stopped = None
        self._recordings_today = 0
        self._recordings_today_date = None
        self._last_status_write = 0.0

    def _touch_shadow_dirty(self):
        try:
            SHADOW_DIRTY_FLAG.touch(exist_ok=True)
        except Exception:
            pass

    def _write_status(self):
        try:
            STATUS_DIR.mkdir(parents=True, exist_ok=True)
            status = {
                "cameraOk": self._camera_ok,
                "lastMotionAt": self._last_motion_at,
                "lastRecordingStartedAt": self._last_recording_started,
                "lastRecordingStoppedAt": self._last_recording_stopped,
                "recordingsToday": self._recordings_today,
                "pid": os.getpid(),
                "updatedAt": datetime.now(timezone.utc).isoformat(),
            }
            tmp = STATUS_FILE.with_suffix(".tmp")
            tmp.write_text(json.dumps(status, indent=2))
            tmp.rename(STATUS_FILE)
        except Exception as e:
            logger.warning(f"Failed to write status file: {e}")

    def _check_today_reset(self):
        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        if self._recordings_today_date != today:
            self._recordings_today = 0
            self._recordings_today_date = today

    def setup_camera(self):
        if Picamera2 is None:
            raise RuntimeError("picamera2 not available - must run on Raspberry Pi")

        self.picam2 = Picamera2()

        # Configure for preview (low-res for motion detection)
        preview_w, preview_h = self.camera_cfg["preview_resolution"]
        record_w, record_h = self.camera_cfg["record_resolution"]

        config = self.picam2.create_video_configuration(
            main={"size": (record_w, record_h)},
            lores={"size": (preview_w, preview_h), "format": "YUV420"},
        )
        self.picam2.configure(config)
        self.picam2.start()

        # Start encoder with circular buffer for pre-motion recording (if enabled)
        if self.record_cfg.get("pre_buffer_enabled", True):
            pre_buffer = self.record_cfg.get("pre_buffer", 3)
            record_fps = self.camera_cfg.get("record_fps", 30)
            buffersize = record_fps * pre_buffer

            self.encoder = H264Encoder(bitrate=5_000_000)
            self.circular_output = CircularOutput(buffersize=buffersize)
            self.picam2.start_encoder(self.encoder, self.circular_output)
            logger.info(f"Camera started: preview={preview_w}x{preview_h}, record={record_w}x{record_h}, pre_buffer={pre_buffer}s ({buffersize} frames)")
        else:
            logger.info(f"Camera started: preview={preview_w}x{preview_h}, record={record_w}x{record_h}, pre_buffer=disabled")

        self._camera_ok = True
        self._write_status()

    def detect_motion(self, frame: np.ndarray) -> bool:
        """Compare current frame with previous to detect motion."""
        gray = cv2.cvtColor(frame, cv2.COLOR_YUV2GRAY_I420) if len(frame.shape) > 2 else frame

        # Apply Gaussian blur to reduce noise
        kernel = self.motion_cfg["blur_kernel"]
        blurred = cv2.GaussianBlur(gray, (kernel, kernel), 0)

        if self.prev_frame is None:
            self.prev_frame = blurred
            return False

        # Compute absolute difference
        diff = cv2.absdiff(self.prev_frame, blurred)
        self.prev_frame = blurred

        # Threshold the difference
        _, thresh = cv2.threshold(diff, 25, 255, cv2.THRESH_BINARY)

        # Count changed pixels
        changed_pixels = cv2.countNonZero(thresh)
        return changed_pixels > self.motion_cfg["threshold"]

    def start_recording(self) -> str:
        """Start recording video to a file, flushing the pre-motion buffer if enabled."""
        timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%S")
        filename = f"{timestamp}.mp4"
        filepath = self.output_dir / filename

        if self.circular_output is not None:
            # CircularOutput only accepts file paths — write raw H.264, remux to MP4 in stop_recording
            self._raw_h264_path = str(filepath.with_suffix(".h264"))
            self.circular_output.fileoutput = self._raw_h264_path
            self.circular_output.start()
        else:
            # No pre-buffer — start encoder on demand with FfmpegOutput for proper MP4
            self.encoder = H264Encoder(bitrate=5_000_000)
            output = FfmpegOutput(str(filepath))
            self.picam2.start_encoder(self.encoder, output)

        self.recording = True
        self.record_start_time = time.time()
        self.last_motion_time = time.time()

        now = datetime.now(timezone.utc).isoformat()
        self._last_motion_at = now
        self._last_recording_started = now
        self._check_today_reset()
        self._write_status()
        self._touch_shadow_dirty()

        logger.info(f"Recording started{' (with pre-buffer)' if self.circular_output else ''}: {filepath}")
        return str(filepath)

    def stop_recording(self, filepath: str):
        """Stop recording and finalize the clip."""
        if self.circular_output is not None:
            # Stop file output but keep encoder running for the ring buffer
            self.circular_output.stop()
            self.circular_output.fileoutput = None

            # Remux raw H.264 to MP4 container
            if self._raw_h264_path and Path(self._raw_h264_path).exists():
                try:
                    import subprocess
                    subprocess.run(
                        ["ffmpeg", "-y", "-i", self._raw_h264_path, "-c", "copy", filepath],
                        capture_output=True, timeout=30, check=True
                    )
                    Path(self._raw_h264_path).unlink(missing_ok=True)
                    logger.info(f"Remuxed H.264 to MP4: {filepath}")
                except Exception as e:
                    logger.warning(f"FFmpeg remux failed: {e}, keeping raw H.264")
                    # Fall back to renaming raw file as .mp4
                    Path(self._raw_h264_path).rename(filepath)
                self._raw_h264_path = None
        else:
            # No pre-buffer — stop the encoder entirely
            if self.encoder:
                self.picam2.stop_encoder(self.encoder)
                self.encoder = None

        duration = int(time.time() - self.record_start_time)

        # Rename file to include duration
        src = Path(filepath)
        dst = src.with_name(f"{src.stem}_{duration}s.mp4")
        src.rename(dst)

        self.recording = False

        self._last_recording_stopped = datetime.now(timezone.utc).isoformat()
        self._recordings_today += 1
        self._write_status()
        self._touch_shadow_dirty()

        logger.info(f"Recording stopped: {dst} ({duration}s)")

    def run(self):
        """Main detection loop."""
        self.setup_camera()
        current_filepath = None

        fps_delay = 1.0 / self.camera_cfg["detection_fps"]
        max_clip = self.record_cfg["max_clip_length"]
        post_buffer = self.record_cfg["post_motion_buffer"]

        logger.info("Motion detection started. Waiting for motion...")

        try:
            while True:
                try:
                    # Capture low-res frame for motion detection
                    frame = self.picam2.capture_array("lores")
                    motion = self.detect_motion(frame)

                    if not self._camera_ok:
                        self._camera_ok = True
                        self._write_status()

                    if motion:
                        self.last_motion_time = time.time()
                        self._last_motion_at = datetime.now(timezone.utc).isoformat()

                        if not self.recording:
                            current_filepath = self.start_recording()

                    if self.recording:
                        elapsed = time.time() - self.record_start_time
                        since_motion = time.time() - self.last_motion_time

                        # Stop if max clip length reached or motion stopped + buffer expired
                        if elapsed >= max_clip or since_motion >= post_buffer:
                            self.stop_recording(current_filepath)
                            current_filepath = None

                except Exception as e:
                    if "timed out" in str(e).lower() or "camera" in str(e).lower():
                        if self._camera_ok:
                            self._camera_ok = False
                            self._write_status()
                            logger.error(f"Camera error: {e}")
                    else:
                        raise

                # Periodic status write every 30s
                now = time.time()
                if now - self._last_status_write >= 30:
                    self._write_status()
                    self._last_status_write = now

                # Hot-reload config when agent signals a change
                if CONFIG_RELOAD_FLAG.exists():
                    try:
                        CONFIG_RELOAD_FLAG.unlink(missing_ok=True)
                        new_config = load_config()
                        self.motion_cfg = new_config["motion"]
                        self.camera_cfg = new_config["camera"]
                        self.record_cfg = new_config["recording"]
                        fps_delay = 1.0 / self.camera_cfg["detection_fps"]
                        max_clip = self.record_cfg["max_clip_length"]
                        post_buffer = self.record_cfg["post_motion_buffer"]
                        logger.info("Config reloaded (motion detector)")
                    except Exception as e:
                        logger.warning(f"Failed to reload config: {e}")

                time.sleep(fps_delay)

        except KeyboardInterrupt:
            logger.info("Shutting down...")
            if self.recording and current_filepath:
                self.stop_recording(current_filepath)
            if self.encoder and self.picam2:
                self.picam2.stop_encoder(self.encoder)
            if self.picam2:
                self.picam2.stop()


def main():
    config = load_config()
    detector = MotionDetector(config)
    detector.run()


if __name__ == "__main__":
    main()
