#!/usr/bin/env python3
"""SnoutSpotter motion detection and recording service for Pi Zero 2 W."""

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
    from picamera2.outputs import FfmpegOutput
except ImportError:
    Picamera2 = None  # Allow importing on non-Pi systems for testing

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
logger = logging.getLogger("snout-spotter")


def load_config(path: str = "config.yaml") -> dict:
    with open(path) as f:
        return yaml.safe_load(f)


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
        logger.info(f"Camera started: preview={preview_w}x{preview_h}, record={record_w}x{record_h}")

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
        """Start recording video to a file."""
        timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%S")
        filename = f"{timestamp}.mp4"
        filepath = self.output_dir / filename

        self.encoder = H264Encoder(bitrate=5_000_000)
        output = FfmpegOutput(str(filepath))
        self.picam2.start_encoder(self.encoder, output)

        self.recording = True
        self.record_start_time = time.time()
        self.last_motion_time = time.time()
        logger.info(f"Recording started: {filepath}")
        return str(filepath)

    def stop_recording(self, filepath: str):
        """Stop recording and finalize the clip."""
        if self.encoder:
            self.picam2.stop_encoder(self.encoder)

        duration = int(time.time() - self.record_start_time)

        # Rename file to include duration
        src = Path(filepath)
        dst = src.with_name(f"{src.stem}_{duration}s.mp4")
        src.rename(dst)

        self.recording = False
        self.encoder = None
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
                # Capture low-res frame for motion detection
                frame = self.picam2.capture_array("lores")
                motion = self.detect_motion(frame)

                if motion:
                    self.last_motion_time = time.time()

                    if not self.recording:
                        current_filepath = self.start_recording()

                if self.recording:
                    elapsed = time.time() - self.record_start_time
                    since_motion = time.time() - self.last_motion_time

                    # Stop if max clip length reached or motion stopped + buffer expired
                    if elapsed >= max_clip or since_motion >= post_buffer:
                        self.stop_recording(current_filepath)
                        current_filepath = None

                time.sleep(fps_delay)

        except KeyboardInterrupt:
            logger.info("Shutting down...")
            if self.recording and current_filepath:
                self.stop_recording(current_filepath)
            if self.picam2:
                self.picam2.stop()


def main():
    config = load_config()
    detector = MotionDetector(config)
    detector.run()


if __name__ == "__main__":
    main()
