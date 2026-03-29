# On-Demand Live Streaming via AWS Kinesis Video Streams

## Problem

The SnoutSpotter web UI has no live camera view. Users can only see recorded clips after they've been uploaded and processed. We want to add an on-demand live stream that starts when a user opens the Live View page and stops when they leave, minimizing cost and Pi resource usage.

## Current State

- **Pi Zero 2 W** (512MB RAM, quad-core ARM Cortex-A53) runs three systemd services: `snoutspotter-motion` (motion detection), `snoutspotter-uploader` (S3 upload), `snoutspotter-agent` (merged: shadow reporting + OTA, single MQTT connection)
- Camera: Pi Camera Module 3, using `picamera2` with `libcamera` stack
- Pi communicates with AWS via IoT Core using X.509 certificates and MQTT (`awsiotsdk`). The `agent.py` service owns the single MQTT connection, handles shadow reporting (health, camera, system metrics) and OTA updates via device shadow deltas.
- Multiple Pi devices are supported — each registered as an IoT Thing named `snoutspotter-{name}` in the `snoutspotter-pis` thing group, provisioned via `PiMgmtStack` / `setup-pi.sh`
- **Config system**: Two-layer — `defaults.yaml` (shipped via OTA, all default values) deep-merged with `config.yaml` (device-specific overrides, excluded from OTA). `config_loader.py` handles the merge and is used by all Pi scripts.
- **OTA system**: Can deliver Python code, default config changes (`defaults.yaml`), system apt packages (`system-deps.txt`), and pip dependencies (`requirements.txt`). Device-specific `config.yaml` is preserved across updates.
- Frontend: React SPA on CloudFront. API: ASP.NET Core Lambda behind API Gateway (Okta JWT auth)

## Architecture

### Signal Flow

1. User opens Live View page, selects a device
2. Frontend calls `POST /api/stream/{thingName}/start` on the API
3. API writes `desired.streaming: true` to the device's IoT shadow
4. Pi agent receives shadow delta, spawns `stream_manager.py` which starts a GStreamer+kvssink pipeline streaming to KVS stream `snoutspotter-{thingName}-live`
5. API returns the KVS stream name and signaling channel info to the frontend
6. Frontend uses KVS WebRTC JavaScript SDK to display the stream
7. User leaves Live View → frontend calls `POST /api/stream/{thingName}/stop` → API writes `desired.streaming: false` → agent kills `stream_manager.py`

### Component Diagram

```
Browser → API Gateway → Lambda (API) → IoT Core (Device Shadow)
                                            ↓ (shadow delta)
                                      Pi Zero 2 W (agent.py)
                                            ↓ (spawns subprocess)
                                      stream_manager.py → GStreamer → kvssink
                                            ↓
                                   Kinesis Video Streams (snoutspotter-{thingName}-live)
                                            ↓
                                    Browser (WebRTC viewer)
```

## Implementation Plan

### Phase 1: AWS Infrastructure (CDK)

**New resources in a LiveStreamStack (or extend existing stacks):**

- **KVS Signaling Channel** per device: `snoutspotter-{thingName}-live` — used for WebRTC signaling. Can be created on-demand via the API when streaming starts, or pre-provisioned per device.
- **IoT Credentials Provider (Role Alias)**: Creates an IAM role that Pi devices can assume via their IoT certificates to get temporary AWS credentials for KVS. This avoids long-lived IAM user credentials.
  - IAM Role with permissions: `kinesisvideo:PutMedia`, `kinesisvideo:GetSignalingChannelEndpoint`, `kinesisvideo:ConnectAsMaster`, `kinesisvideo:GetIceServerConfig`, `kinesisvideo:DescribeSignalingChannel`, `kinesisvideo:CreateSignalingChannel`
  - IoT Role Alias pointing to this role
  - Update IoT policy to allow `iot:AssumeRoleWithCertificate` on the role alias
- **IAM permissions for API Lambda**: Allow `iot:UpdateThingShadow` (already has this for OTA), `kinesisvideo:GetSignalingChannelEndpoint`, `kinesisvideo:ConnectAsViewer`, `kinesisvideo:GetIceServerConfig`, `kinesisvideo:DescribeSignalingChannel`, `kinesisvideo:CreateSignalingChannel`, `kinesisvideo:DeleteSignalingChannel`

### Phase 2: Pi-Side Streaming (delivered via OTA)

All Pi-side changes are deployed through the existing OTA system — no manual SSH or setup required on devices.

**OTA-delivered changes:**

1. **`system-deps.txt`** — add GStreamer and KVS build dependencies:
   ```
   # Existing deps
   python3-pip
   python3-opencv
   python3-picamera2
   ffmpeg
   git
   # Streaming deps
   cmake
   g++
   libgstreamer1.0-dev
   libgstreamer-plugins-base1.0-dev
   gstreamer1.0-plugins-good
   gstreamer1.0-plugins-bad
   gstreamer1.0-tools
   libssl-dev
   libcurl4-openssl-dev
   liblog4cplus-dev
   ```
   The OTA agent diffs `system-deps.txt` against the previous version's backup and runs `apt-get install` only for changed/new packages.

2. **`defaults.yaml`** — add new streaming config defaults:
   ```yaml
   streaming:
     timeout_seconds: 600        # Auto-stop after 10 minutes
     resolution: [640, 480]
     framerate: 15
     bitrate: 800                # kbps
     credentials_endpoint: ""    # IoT Credentials Provider endpoint (set in config.yaml)
     kvs_region: eu-west-1
   ```
   These defaults are delivered via OTA. Device-specific overrides (like `credentials_endpoint`) go in `config.yaml` and are set once via `setup-pi.sh` or remote config.

3. **`stream_manager.py`** (new file) — standalone script spawned by agent.py on demand:
   - Reads merged config via `config_loader.load_config()` for streaming settings
   - Obtains temporary AWS credentials via IoT Credentials Provider endpoint
   - Starts a GStreamer subprocess:
     ```
     gst-launch-1.0 libcamerasrc ! video/x-raw,width=640,height=480,framerate=15/1,format=I420 ! videoconvert ! x264enc speed-preset=ultrafast tune=zerolatency byte-stream=true key-int-max=45 bitrate=800 ! h264parse ! video/x-h264,stream-format=avc,alignment=au ! kvssink stream-name="snoutspotter-{thingName}-live" aws-region="eu-west-1"
     ```
   - Monitors the GStreamer process, restarts on failure
   - Auto-stops after `streaming.timeout_seconds` (default 10 minutes) as a safety net
   - Checks camera contention: won't start if motion detection is currently recording (checks `motion-status.json`). Writes `camera-busy` status for agent to report.
   - On exit (SIGTERM from agent.py or timeout), kills GStreamer cleanly

4. **`agent.py`** (updated) — add shadow delta handler for `streaming` field:
   - On delta `streaming: true`: spawn `stream_manager.py` as a subprocess
   - On delta `streaming: false`: send SIGTERM to `stream_manager.py` subprocess
   - Report `streaming: true/false` (and `streamError` if camera busy) in shadow reported state
   - On agent startup, check current shadow for any pending streaming delta (same as OTA reconnect pattern — gotcha #4)

**KVS Producer SDK build:**

The KVS C++ producer SDK (`kvssink` GStreamer plugin) must be compiled natively. This is a one-time step that cannot be delivered via OTA due to build time (~30-60 minutes on Pi Zero 2W). Two options:

- **Option A (recommended): Pre-built .deb package** — cross-compile `kvssink` once, host the `.deb` in S3, and add it to `system-deps.txt` as a local package install. Fastest rollout to multiple devices.
- **Option B: Build script** — include a `build-kvssink.sh` script in the OTA package. Agent runs it post-install if `kvssink` is not found. Simpler but slow on first deploy (30-60 min).
- **Option C: Manual one-time setup** — SSH to each Pi and run the build commands. Acceptable for a small number of devices.

```
# Build commands (Option B/C):
git clone https://github.com/awslabs/amazon-kinesis-video-streams-producer-sdk-cpp.git
mkdir -p amazon-kinesis-video-streams-producer-sdk-cpp/build
cd amazon-kinesis-video-streams-producer-sdk-cpp/build
cmake .. -DBUILD_GSTREAMER_PLUGIN=ON -DBUILD_DEPENDENCIES=OFF -DALIGNED_MEMORY_MODEL=ON
make -j$(nproc)
sudo cp libgstkvssink.so /usr/lib/$(dpkg-architecture -qDEB_HOST_MULTIARCH)/gstreamer-1.0/
```

### Phase 3: API Endpoints

**New controller: `src/api/Controllers/StreamController.cs`**

- `POST /api/stream/{thingName}/start` → Writes `desired.streaming: true` to device shadow, creates KVS signaling channel if it doesn't exist, returns `{ streamName, signalingChannelArn, region }`
- `POST /api/stream/{thingName}/stop` → Writes `desired.streaming: false` to device shadow
- `GET /api/stream/{thingName}/status` → Reads device shadow for `reported.streaming` state

**New service: `src/api/Services/StreamService.cs`**

- Injects `IAmazonIoTDataPlane` to update device shadows (same client already used for OTA)
- Injects `IAmazonKinesisVideo` to manage signaling channels and get endpoints for the frontend

### Phase 4: Frontend Live View Page

**New page: `src/web/src/pages/LiveView.tsx`**

- Device selector dropdown (populated from existing `/api/stats/health` device list)
- "Start Stream" button → calls `POST /api/stream/{thingName}/start`
- Displays connection status (connecting, live, stopped, camera busy)
- **WebRTC playback** (preferred, sub-second latency): Uses `amazon-kinesis-video-streams-webrtc` npm package as a viewer connecting to the KVS signaling channel
- Auto-calls `POST /api/stream/{thingName}/stop` on page unmount (React `useEffect` cleanup)
- Shows stream duration timer
- Fallback: if WebRTC fails, show error with retry option

**Route:** `/live` in the React router

**Nav update:** Add "Live" link to sidebar navigation

## Deployment Strategy

The two-layer config system and enhanced OTA make most of this deployable without touching any Pi:

| Change | Delivery method |
|--------|----------------|
| `stream_manager.py` (new script) | OTA (included in tarball) |
| `agent.py` (streaming delta handler) | OTA (included in tarball) |
| `defaults.yaml` (streaming config defaults) | OTA (included in tarball) |
| `system-deps.txt` (GStreamer packages) | OTA (agent runs `apt-get install` on diff) |
| `requirements.txt` (if new pip deps) | OTA (agent runs `pip install`) |
| `streaming.credentials_endpoint` in `config.yaml` | Remote config via shadow delta (no OTA needed) |
| KVS `kvssink` GStreamer plugin | Pre-built .deb in S3 (Option A) or manual build (Option C) |
| CDK infrastructure (KVS, IAM, Role Alias) | CI/CD pipeline (`deploy.yml`) |
| API endpoints + frontend | CI/CD pipeline (`deploy.yml`) |

**Rollout order:**
1. Deploy CDK infrastructure (Phase 1) via CI/CD
2. Build and distribute `kvssink` plugin to Pis (one-time, Option A or C)
3. Push Pi code changes to `main` → triggers `package-pi.yml` → OTA delivers to all devices
4. Set `streaming.credentials_endpoint` per device via remote config (shadow delta)
5. Deploy API + frontend via CI/CD

Steps 3-5 are fully automated after the initial `kvssink` setup.

## Cost Estimate

- **KVS ingestion**: $0.0085/GB. At 640x480@15fps ~0.5 Mbps → ~225 MB/hour → ~$0.002/hour
- **WebRTC signaling**: $0.00075/channel/month + $2.25/1000 TURN relay minutes
- **IoT Core MQTT**: $1 per million messages (effectively free at this scale)
- **Estimated monthly cost**: $1-3/month assuming 1-2 hours of live viewing per day

## Risks and Mitigations

- **Pi Zero 2 W resource constraints**: Mitigated by using low resolution (640x480), low framerate (15fps), and software encoding with `x264enc speed-preset=ultrafast`. CPU usage will spike during streaming but is acceptable since it's on-demand. Resolution/framerate/bitrate are configurable via `defaults.yaml` and can be tuned per-device via remote config.
- **Camera contention**: `stream_manager.py` checks `motion-status.json` before starting. Returns "camera busy" error if motion recording is active, rather than trying to share
- **KVS SDK build complexity on Pi**: Only native dependency that can't go through OTA. Mitigated by pre-building as a .deb (Option A). GStreamer system packages are handled by `system-deps.txt` via OTA.
- **Stale streams**: Configurable auto-stop timeout in `defaults.yaml` (`streaming.timeout_seconds`, default 10 minutes). Agent also kills subprocess if shadow delta sets `streaming: false`.
- **Network reliability**: Pi WiFi may be unstable. `stream_manager.py` monitors GStreamer and restarts on failure; agent reports `streaming` state in shadow so frontend knows if stream dropped
- **Single MQTT connection**: Stream commands go through device shadows, not raw MQTT topics. `agent.py` owns the connection and delegates to `stream_manager.py` as a subprocess — no second MQTT client needed
- **OTA delivering streaming deps to all devices**: The `system-deps.txt` diff mechanism means apt packages are only installed when the file changes. First OTA with GStreamer packages will take longer (~2-5 min for apt install) but subsequent updates skip it.

## Recommended Approach: WebRTC

WebRTC is preferred over HLS because:

- Sub-second latency vs 5-10 seconds for HLS
- Lower cost (no KVS Stream resource needed, just signaling channel)
- Better for interactive use (checking what your dog is doing right now)

HLS can be added later as a fallback if WebRTC proves unreliable.

## Implementation Order

1. Phase 1: Infrastructure (CDK) — KVS resources + IoT Credentials Provider + IAM permissions
2. Phase 2: Pi streaming — `stream_manager.py` + agent delta handler + `defaults.yaml` streaming config + `system-deps.txt` GStreamer packages + kvssink build/distribution
3. Phase 3: API endpoints — stream start/stop/status per device
4. Phase 4: Frontend — Live View page with device selector + WebRTC viewer
