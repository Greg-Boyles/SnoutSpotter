#!/bin/bash
set -euo pipefail

#
# SnoutSpotter Pi Setup Script
# Run from: ~/SnoutSpotter/src/pi/
#

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

echo "============================================"
echo "  SnoutSpotter Pi Setup"
echo "============================================"
echo ""

# ── Collect configuration ──────────────────────

read -rp "S3 Bucket Name (e.g. snout-spotter-490204853569): " BUCKET_NAME
read -rp "AWS Region [eu-west-1]: " AWS_REGION
AWS_REGION="${AWS_REGION:-eu-west-1}"
read -rp "AWS Access Key ID: " AWS_ACCESS_KEY
read -rsp "AWS Secret Access Key: " AWS_SECRET_KEY
echo ""

if [[ -z "$BUCKET_NAME" || -z "$AWS_ACCESS_KEY" || -z "$AWS_SECRET_KEY" ]]; then
    echo "ERROR: All fields are required."
    exit 1
fi

echo ""
echo "[1/7] Enabling camera interface..."
sudo raspi-config nonint do_camera 0 2>/dev/null || true

echo "[2/7] Installing system dependencies..."
sudo apt update -qq
sudo apt install -y -qq python3-pip python3-opencv python3-picamera2 ffmpeg git

echo "[3/7] Installing Python packages..."
pip3 install -r "$SCRIPT_DIR/requirements.txt" --break-system-packages --quiet

echo "[4/7] Configuring AWS credentials..."
mkdir -p ~/.aws

cat > ~/.aws/credentials << EOF
[default]
aws_access_key_id = $AWS_ACCESS_KEY
aws_secret_access_key = $AWS_SECRET_KEY
EOF

cat > ~/.aws/config << EOF
[default]
region = $AWS_REGION
output = json
EOF

chmod 600 ~/.aws/credentials ~/.aws/config

echo "[5/7] Updating config.yaml..."
sed -i "s|bucket_name: .*|bucket_name: \"$BUCKET_NAME\"|" "$SCRIPT_DIR/config.yaml"
sed -i "s|region: .*|region: $AWS_REGION|" "$SCRIPT_DIR/config.yaml"
sed -i "s|output_dir: .*|output_dir: $HOME/clips|" "$SCRIPT_DIR/config.yaml"

# Fix hardcoded paths in uploader.py
sed -i "s|/home/pi/snout-spotter/uploads.db|$HOME/.snoutspotter/uploads.db|" "$SCRIPT_DIR/uploader.py"

# Create required directories
mkdir -p "$HOME/clips"
mkdir -p "$HOME/.snoutspotter"

echo "[6/7] Installing systemd services..."

for SERVICE_NAME in motion uploader health; do
    case "$SERVICE_NAME" in
        motion)   SCRIPT="motion_detector.py"; DESC="Motion Detection" ;;
        uploader) SCRIPT="uploader.py";        DESC="Upload" ;;
        health)   SCRIPT="health.py";          DESC="Health Monitoring" ;;
    esac

    cat > "/tmp/snoutspotter-${SERVICE_NAME}.service" << EOF
[Unit]
Description=SnoutSpotter $DESC Service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$SCRIPT_DIR
ExecStart=/usr/bin/python3 $SCRIPT_DIR/$SCRIPT
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
done

sudo cp /tmp/snoutspotter-*.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable snoutspotter-motion snoutspotter-uploader snoutspotter-health

echo "[7/7] Starting services..."
sudo systemctl start snoutspotter-motion snoutspotter-uploader snoutspotter-health

# Wait a moment for services to start
sleep 5

echo ""
echo "============================================"
echo "  Setup Complete!"
echo "============================================"
echo ""

# Check service status
FAILED=0
for SERVICE_NAME in motion uploader health; do
    STATUS=$(systemctl is-active "snoutspotter-${SERVICE_NAME}" 2>/dev/null || true)
    if [[ "$STATUS" == "active" ]]; then
        echo "  ✓ snoutspotter-${SERVICE_NAME}: running"
    else
        echo "  ✗ snoutspotter-${SERVICE_NAME}: $STATUS"
        FAILED=1
    fi
done

echo ""
echo "  Bucket:  $BUCKET_NAME"
echo "  Region:  $AWS_REGION"
echo "  Clips:   $HOME/clips"
echo ""

if [[ $FAILED -eq 1 ]]; then
    echo "WARNING: Some services failed to start."
    echo "Check logs with: sudo journalctl -u snoutspotter-SERVICE -n 50 --no-pager"
else
    echo "All services running! Walk in front of the camera to test."
    echo ""
    echo "Useful commands:"
    echo "  Monitor motion:  sudo journalctl -u snoutspotter-motion -f"
    echo "  Monitor uploads: sudo journalctl -u snoutspotter-uploader -f"
    echo "  Check clips:     ls -lh ~/clips/"
fi
