# SnoutSpotter Pi Setup Guide

Complete setup instructions for the Raspberry Pi Zero 2 W with Pi Camera Module 3.

## Hardware Requirements

- Raspberry Pi Zero 2 W
- Pi Camera Module 3 (~$25)
- 15-pin to 22-pin CSI camera cable adapter
- MicroSD card (16GB+ recommended)
- Power supply (5V 2.5A recommended)

## Software Setup

### 1. Install Raspberry Pi OS

1. Download [Raspberry Pi Imager](https://www.raspberrypi.com/software/)
2. Flash **Raspberry Pi OS Lite (64-bit)** to SD card
3. During setup, configure:
   - Hostname: `snoutspotter`
   - Enable SSH
   - Set WiFi credentials
   - Create user account (e.g., `admin`)

### 2. Boot and Connect

```bash
# Find Pi IP address from your router, then:
ssh admin@192.168.x.x
```

### 3. Run Automated Setup

```bash
# Clone the repository
git clone https://github.com/Greg-Boyles/SnoutSpotter.git
cd SnoutSpotter/src/pi

# Run setup script (requires AWS credentials)
./setup-pi.sh
```

**You'll need:**
- AWS Access Key ID
- AWS Secret Access Key
- S3 Bucket name (from CDK deployment)

### 4. Manual Setup (Alternative)

If you prefer manual setup or troubleshooting:

#### a) Enable Camera
```bash
sudo raspi-config
# Navigate to: Interface Options → Camera → Enable
sudo reboot
```

#### b) Install Dependencies
```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y python3-pip python3-opencv ffmpeg git
```

#### c) Install Python Packages
```bash
cd SnoutSpotter/src/pi
pip3 install -r requirements.txt --break-system-packages
```

#### d) Configure AWS Credentials
```bash
mkdir -p ~/.aws

cat > ~/.aws/credentials << 'EOF'
[default]
aws_access_key_id = YOUR_ACCESS_KEY_HERE
aws_secret_access_key = YOUR_SECRET_KEY_HERE
EOF

cat > ~/.aws/config << 'EOF'
[default]
region = eu-west-1
output = json
EOF

chmod 600 ~/.aws/credentials ~/.aws/config
```

#### e) Update Configuration
Edit `config.yaml`:
```bash
nano config.yaml
```

Update these values:
- `upload.bucket_name`: Your S3 bucket name (e.g., `snout-spotter-490204853569`)
- `recording.output_dir`: `/home/YOUR_USERNAME/clips`

#### f) Fix Uploader Database Path
```bash
# Update hardcoded paths in uploader.py
sed -i "s|/home/pi/snout-spotter/uploads.db|/home/$USER/.snoutspotter/uploads.db|" uploader.py
mkdir -p ~/.snoutspotter
```

#### g) Create Output Directory
```bash
mkdir -p ~/clips
```

#### h) Install Systemd Services

Create service files with correct paths:
```bash
# Motion detection service
cat > /tmp/snoutspotter-motion.service << EOF
[Unit]
Description=SnoutSpotter Motion Detection Service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$HOME/SnoutSpotter/src/pi
ExecStart=/usr/bin/python3 $HOME/SnoutSpotter/src/pi/motion_detector.py
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

# Uploader service
cat > /tmp/snoutspotter-uploader.service << EOF
[Unit]
Description=SnoutSpotter Upload Service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$HOME/SnoutSpotter/src/pi
ExecStart=/usr/bin/python3 $HOME/SnoutSpotter/src/pi/uploader.py
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

# Health monitoring service
cat > /tmp/snoutspotter-health.service << EOF
[Unit]
Description=SnoutSpotter Health Monitoring Service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$HOME/SnoutSpotter/src/pi
ExecStart=/usr/bin/python3 $HOME/SnoutSpotter/src/pi/health.py
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

# Install services
sudo cp /tmp/snoutspotter-*.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable snoutspotter-motion snoutspotter-uploader snoutspotter-health
sudo systemctl start snoutspotter-motion snoutspotter-uploader snoutspotter-health
```

## Verify Installation

### Check Service Status
```bash
sudo systemctl status snoutspotter-motion
sudo systemctl status snoutspotter-uploader
sudo systemctl status snoutspotter-health
```

All three should show `active (running)`.

### Monitor Logs
```bash
# Motion detection
sudo journalctl -u snoutspotter-motion -f

# Uploader
sudo journalctl -u snoutspotter-uploader -f

# Health monitoring
sudo journalctl -u snoutspotter-health -f
```

### Test Motion Detection
Walk in front of the camera. Check for recorded clips:
```bash
ls -lh ~/clips/
```

### Verify Uploads
Check S3 bucket (from your development machine):
```bash
aws s3 ls s3://YOUR-BUCKET-NAME/raw-clips/ --recursive
```

## Troubleshooting

### Camera Not Detected
```bash
# Test camera
libcamera-still -o test.jpg

# If fails, enable camera interface
sudo raspi-config
# Interface Options → Camera → Enable
sudo reboot
```

### Uploader Crashes with "unable to open database file"
The uploader.py has a hardcoded path. Fix with:
```bash
mkdir -p ~/.snoutspotter
sed -i "s|/home/pi/snout-spotter/uploads.db|$HOME/.snoutspotter/uploads.db|" uploader.py
sudo systemctl restart snoutspotter-uploader
```

### Services Not Starting
Check logs:
```bash
sudo journalctl -u snoutspotter-motion -n 50 --no-pager
```

Common issues:
- Wrong user in service file (should be your username, not `pi`)
- Wrong paths in service file
- Missing Python dependencies
- Missing AWS credentials

### No Clips Being Uploaded
1. Check uploader is running: `sudo systemctl status snoutspotter-uploader`
2. Check AWS credentials: `aws s3 ls` (should list buckets)
3. Check config.yaml has correct bucket name
4. Check uploader logs: `sudo journalctl -u snoutspotter-uploader -n 50`

### High CPU Usage
Motion detection is CPU-intensive. This is normal on Pi Zero 2 W. Consider:
- Reducing `camera.detection_fps` in config.yaml (default: 5)
- Reducing `camera.preview_resolution` (default: 640x480)
- Increasing `motion.threshold` to reduce false triggers

## Configuration

Edit `config.yaml` to adjust:

- **Motion sensitivity**: `motion.threshold` (higher = less sensitive)
- **Recording duration**: `recording.max_clip_length` (max seconds)
- **Post-motion buffer**: `recording.post_motion_buffer` (seconds after motion stops)
- **Video quality**: `camera.record_resolution`, `camera.record_fps`
- **Upload behavior**: `upload.delete_after_upload` (true/false)

## Uninstall

```bash
# Stop and disable services
sudo systemctl stop snoutspotter-motion snoutspotter-uploader snoutspotter-health
sudo systemctl disable snoutspotter-motion snoutspotter-uploader snoutspotter-health

# Remove service files
sudo rm /etc/systemd/system/snoutspotter-*.service
sudo systemctl daemon-reload

# Remove code and data
rm -rf ~/SnoutSpotter
rm -rf ~/clips
rm -rf ~/.snoutspotter
```
