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
read -rp "Pi Management API URL (e.g. https://xxx.execute-api.eu-west-1.amazonaws.com): " PI_MGMT_URL
read -rp "Device Name (e.g. front-door, garage): " DEVICE_NAME

if [[ -z "$BUCKET_NAME" || -z "$AWS_ACCESS_KEY" || -z "$AWS_SECRET_KEY" || -z "$PI_MGMT_URL" || -z "$DEVICE_NAME" ]]; then
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

echo "[4/7] Registering device with Pi Management API..."
REGISTRATION_RESPONSE=$(curl -s -X POST "$PI_MGMT_URL/api/devices/register" \
    -H "Content-Type: application/json" \
    -d "{\"name\": \"$DEVICE_NAME\"}")

if [[ -z "$REGISTRATION_RESPONSE" ]]; then
    echo "ERROR: Failed to register device. No response from API."
    exit 1
fi

# Extract values from JSON response
THING_NAME=$(echo "$REGISTRATION_RESPONSE" | python3 -c "import sys, json; print(json.load(sys.stdin)['thingName'])" 2>/dev/null || true)
IOT_ENDPOINT=$(echo "$REGISTRATION_RESPONSE" | python3 -c "import sys, json; print(json.load(sys.stdin)['ioTEndpoint'])" 2>/dev/null || true)
CERTIFICATE_PEM=$(echo "$REGISTRATION_RESPONSE" | python3 -c "import sys, json; print(json.load(sys.stdin)['certificatePem'])" 2>/dev/null || true)
PRIVATE_KEY=$(echo "$REGISTRATION_RESPONSE" | python3 -c "import sys, json; print(json.load(sys.stdin)['privateKey'])" 2>/dev/null || true)
ROOT_CA_URL=$(echo "$REGISTRATION_RESPONSE" | python3 -c "import sys, json; print(json.load(sys.stdin)['rootCaUrl'])" 2>/dev/null || true)

if [[ -z "$THING_NAME" || -z "$IOT_ENDPOINT" || -z "$CERTIFICATE_PEM" || -z "$PRIVATE_KEY" ]]; then
    echo "ERROR: Device registration failed or returned incomplete data."
    echo "Response: $REGISTRATION_RESPONSE"
    exit 1
fi

echo "Device registered: $THING_NAME"

echo "[5/7] Saving IoT certificates..."
mkdir -p "$HOME/.snoutspotter/certs"

echo "$CERTIFICATE_PEM" > "$HOME/.snoutspotter/certs/certificate.pem.crt"
echo "$PRIVATE_KEY" > "$HOME/.snoutspotter/certs/private.pem.key"
chmod 600 "$HOME/.snoutspotter/certs/certificate.pem.crt" "$HOME/.snoutspotter/certs/private.pem.key"

# Download Amazon Root CA
curl -s -o "$HOME/.snoutspotter/certs/AmazonRootCA1.pem" "$ROOT_CA_URL"

echo "[6/7] Configuring AWS credentials..."
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

echo "[7/7] Updating config.yaml..."
sed -i "s|bucket_name: .*|bucket_name: \"$BUCKET_NAME\"|" "$SCRIPT_DIR/config.yaml"
sed -i "s|region: .*|region: $AWS_REGION|" "$SCRIPT_DIR/config.yaml"
sed -i "s|output_dir: .*|output_dir: $HOME/clips|" "$SCRIPT_DIR/config.yaml"

# Update IoT configuration
if grep -q "iot:" "$SCRIPT_DIR/config.yaml"; then
    sed -i "s|thing_name: .*|thing_name: \"$THING_NAME\"|" "$SCRIPT_DIR/config.yaml"
    sed -i "s|endpoint: .*|endpoint: \"$IOT_ENDPOINT\"|" "$SCRIPT_DIR/config.yaml"
else
    cat >> "$SCRIPT_DIR/config.yaml" << EOF

iot:
  thing_name: "$THING_NAME"
  endpoint: "$IOT_ENDPOINT"
  cert_path: "$HOME/.snoutspotter/certs/certificate.pem.crt"
  key_path: "$HOME/.snoutspotter/certs/private.pem.key"
  ca_path: "$HOME/.snoutspotter/certs/AmazonRootCA1.pem"
EOF
fi

# Fix hardcoded paths in uploader.py
sed -i "s|/home/pi/snout-spotter/uploads.db|$HOME/.snoutspotter/uploads.db|" "$SCRIPT_DIR/uploader.py"

# Create required directories
mkdir -p "$HOME/clips"
mkdir -p "$HOME/.snoutspotter"

echo "[8/8] Installing systemd services..."

for SERVICE_NAME in motion uploader health ota; do
    case "$SERVICE_NAME" in
        motion)   SCRIPT="motion_detector.py"; DESC="Motion Detection" ;;
        uploader) SCRIPT="uploader.py";        DESC="Upload" ;;
        health)   SCRIPT="health.py";          DESC="Health Monitoring" ;;
        ota)      SCRIPT="ota_agent.py";       DESC="OTA Update" ;;
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
sudo systemctl enable snoutspotter-motion snoutspotter-uploader snoutspotter-health snoutspotter-ota

echo "Starting services..."
sudo systemctl start snoutspotter-motion snoutspotter-uploader snoutspotter-health snoutspotter-ota

# Wait a moment for services to start
sleep 5

echo ""
echo "============================================"
echo "  Setup Complete!"
echo "============================================"
echo ""

# Check service status
FAILED=0
for SERVICE_NAME in motion uploader health ota; do
    STATUS=$(systemctl is-active "snoutspotter-${SERVICE_NAME}" 2>/dev/null || true)
    if [[ "$STATUS" == "active" ]]; then
        echo "  ✓ snoutspotter-${SERVICE_NAME}: running"
    else
        echo "  ✗ snoutspotter-${SERVICE_NAME}: $STATUS"
        FAILED=1
    fi
done

echo ""
echo "  Bucket:       $BUCKET_NAME"
echo "  Region:       $AWS_REGION"
echo "  Thing Name:   $THING_NAME"
echo "  IoT Endpoint: $IOT_ENDPOINT"
echo "  Clips:        $HOME/clips"
echo "  Certs:        $HOME/.snoutspotter/certs"
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
