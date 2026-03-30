# KVS WebRTC Migration Plan

## Problem Statement

The current live streaming implementation uses **kvssink** (a prebuilt GStreamer plugin) on the Pi to push H.264 video to a **KVS Video Stream**, which the frontend consumes via **HLS** (HTTP Live Streaming). This approach was chosen initially because:

- **kvssink** is a prebuilt GStreamer plugin distributed as a `.deb` package -- no custom compilation required
- HLS is a standard video playback format supported natively by browsers and via HLS.js
- The pipeline is straightforward: GStreamer source to encoder to kvssink, with minimal integration code

However, HLS introduces **5-10 seconds of latency**, which is not acceptable for an interactive "check on the dog" use case where the user wants to see what is happening right now. Additionally, KVS Video Streams incur **per-GB ingestion charges** for all media pushed through them.

**KVS WebRTC** is the better long-term solution because:

- **Sub-second latency** (typically 200-500ms) via peer-to-peer video
- **Lower cost** -- signaling channel charges only, no per-GB ingestion fees since media flows peer-to-peer
- **Better suited for interactive use** -- the user can see and react to what the dog is doing in near real-time

---

## Current Architecture (kvssink + HLS)

### Pi Side

GStreamer pipeline:

```
libcamerasrc -> x264enc -> kvssink -> KVS Video Stream
```

- `stream_manager.py` manages the GStreamer subprocess lifecycle
- AWS credentials injected via `iot_credential_provider.py`
- Streaming triggered via IoT Device Shadow updates handled in `agent.py`

### API Side

- `StreamService` creates a KVS Video Stream for the device
- Returns an HLS playback URL to the frontend

### Frontend

- HLS.js or native `<video>` tag for playback
- Connects to the HLS URL returned by the API

### Characteristics

| Property | Value |
|----------|-------|
| Latency | 5-10 seconds |
| Cost model | KVS ingestion charges per GB |
| Pi CPU usage | Moderate (H.264 encoding + kvssink) |
| Browser support | Universal (HLS) |

---

## Target Architecture (KVS WebRTC)

### Pi Side

The Pi acts as the WebRTC **master** using the **KVS WebRTC C SDK** (`amazon-kinesis-video-streams-webrtc-sdk-c`). This is a completely different SDK from the KVS Producer SDK used by kvssink.

Responsibilities of the WebRTC master on the Pi:

- **Signaling** -- connects to the KVS Signaling Channel via WSS, exchanges SDP offers/answers
- **ICE negotiation** -- gathers ICE candidates, performs connectivity checks, establishes peer-to-peer path (or falls back to TURN relay)
- **DTLS-SRTP** -- encrypts the media stream end-to-end
- **Media capture and encoding** -- uses libcamera or v4l2 for camera capture, encodes H.264, packetises as RTP and sends via the WebRTC connection

Integration options:

1. **Standalone binary** -- compile the WebRTC C SDK sample (`kvsWebrtcClientMaster`) for ARM64, invoke as a subprocess from Python (similar to how `stream_manager.py` wraps kvssink today)
2. **Python subprocess wrapper** -- a new `webrtc_stream_manager.py` that manages the WebRTC master binary lifecycle, credential injection, and timeout handling

### API Side

- Creates a **KVS Signaling Channel** (not a video stream)
- Returns a **presigned WSS URL** for the signaling channel and **ICE server configuration** (STUN/TURN endpoints)
- No longer needs `CreateStream`, `GetHLSStreamingSessionURL`, or related video stream operations

### Frontend

- Uses `RTCPeerConnection` to connect as a WebRTC **viewer** via the KVS signaling channel
- `LiveView.tsx` already contains WebRTC viewer code -- it needs the correct presigned WSS URL and ICE server config from the API
- No HLS.js dependency required

### Characteristics

| Property | Value |
|----------|-------|
| Latency | Sub-second (200-500ms typical) |
| Cost model | Signaling channel charges only (much cheaper) |
| Pi CPU usage | Higher (DTLS-SRTP + ICE + H.264 encoding) |
| Browser support | Universal (WebRTC) |

---

## Migration Steps

### Step 1: Build the KVS WebRTC C SDK for ARM64

- Clone `amazon-kinesis-video-streams-webrtc-sdk-c`
- Either **cross-compile** on an x86 build machine targeting aarch64, or **native compile** directly on a Pi
- Dependencies: libsrtp2, openssl, libwebsockets, usrsctp, libcurl, and their dev headers
- Produce a statically-linked `kvsWebrtcClientMaster` binary (or a shared library build)
- Test the binary on a Pi Zero 2 W to validate it runs within resource constraints

### Step 2: Create `webrtc_stream_manager.py`

- New Python module in `src/pi/` that wraps the WebRTC master binary as a subprocess
- Follow the same patterns as `stream_manager.py`: subprocess management, credential injection via environment variables, configurable timeout, graceful shutdown
- Accept parameters: signaling channel name, AWS region, credentials (from `iot_credential_provider.py`)
- Handle process lifecycle: start, monitor, stop, restart on failure

### Step 3: Update or replace `stream_manager.py`

- Replace the kvssink GStreamer pipeline launch with the WebRTC master binary launch
- Alternatively, keep both and select based on a configuration flag during the transition period
- The IoT shadow streaming trigger mechanism in `agent.py` remains unchanged -- it just calls the new stream manager

### Step 4: Update IAM permissions

**Remove** (no longer needed):

- `kinesisvideo:CreateStream`
- `kinesisvideo:PutMedia`
- `kinesisvideo:GetDataEndpoint`
- `kinesisvideo:DescribeStream`
- `kinesisvideo:GetHLSStreamingSessionURL`

**Keep / Add** (for signaling channels):

- `kinesisvideo:ConnectAsMaster`
- `kinesisvideo:GetSignalingChannelEndpoint`
- `kinesisvideo:CreateSignalingChannel`
- `kinesisvideo:DescribeSignalingChannel`
- `kinesisvideo:GetIceServerConfig`

Update the IAM policies in `IoTStack.cs` and `ApiStack.cs` accordingly.

### Step 5: Update API StreamService

- Remove KVS Video Stream creation logic
- Add KVS Signaling Channel creation (if not already present)
- Return presigned WSS URL for the signaling channel endpoint
- Return ICE server configuration (STUN/TURN URIs and credentials) via `GetIceServerConfig`
- Endpoint response shape changes from `{ hlsUrl: string }` to `{ signalingUrl: string, iceServers: RTCIceServer[] }`

### Step 6: Update Frontend LiveView.tsx

- `LiveView.tsx` already has WebRTC viewer code
- Update to consume the new API response shape (presigned WSS URL + ICE servers)
- Remove HLS.js dependency and any HLS-specific playback code
- Ensure proper cleanup of `RTCPeerConnection` on component unmount

### Step 7: Package the WebRTC SDK binary

- Option A: Update the existing kvssink `.deb` package to also include the WebRTC master binary
- Option B: Create a separate `.deb` package for the WebRTC SDK binary
- Include any required shared libraries that are not available in the base Raspberry Pi OS
- Update the Pi provisioning/setup scripts to install the new package

---

## Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| **WebRTC C SDK build complexity** | The SDK has significantly more dependencies than kvssink (libsrtp2, openssl, libwebsockets, usrsctp). Cross-compilation for ARM64 can be fragile. | Document the build process thoroughly. Consider a Docker-based cross-compile environment for reproducibility. |
| **Pi Zero 2 W resource constraints** | The WebRTC master process needs CPU for DTLS-SRTP encryption, ICE candidate gathering, and H.264 encoding simultaneously. The Pi Zero 2 W has only 4 Cortex-A53 cores at 1GHz. | Profile CPU and memory usage early. Consider hardware H.264 encoding via the Pi's GPU (v4l2 codec) to offload the CPU. Set conservative bitrate and resolution defaults. |
| **NAT traversal reliability** | Peer-to-peer connections can fail behind symmetric NATs or restrictive firewalls. | Always configure TURN relay as a fallback via the KVS ICE server config. Monitor connection success rates. |
| **Camera access contention** | Both the WebRTC binary and any other process (e.g., motion detection) may compete for exclusive camera access via libcamera. | Ensure only one process accesses the camera at a time, or use a shared capture approach. |
| **Transition period complexity** | Running both kvssink and WebRTC code paths increases maintenance burden. | Keep the transition period short. Use a feature flag in the device shadow to toggle between modes. |

---

## What Can Be Reused

| Component | Location | Reuse Notes |
|-----------|----------|-------------|
| `stream_manager.py` patterns | `src/pi/stream_manager.py` | Subprocess management, credential injection, timeout logic -- same patterns apply to the WebRTC binary wrapper |
| IoT shadow streaming trigger | `src/pi/agent.py` | Shadow-based start/stop mechanism is transport-agnostic -- works for both kvssink and WebRTC |
| Frontend WebRTC viewer code | `src/web/.../LiveView.tsx` | Already implements `RTCPeerConnection` viewer logic -- needs presigned URL and ICE server config wired in |
| Signaling channel IAM permissions | `IoTStack.cs`, `ApiStack.cs` | Some signaling channel permissions are already defined in the CDK stacks |
| IoT credential provider | `src/pi/iot_credential_provider.py` | AWS credential injection for the WebRTC binary works the same way as for kvssink |
| Device shadow config pattern | `src/pi/agent.py` | Configuration via IoT Device Shadow (e.g., resolution, bitrate) transfers directly |
