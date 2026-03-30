import { useEffect, useRef, useState } from "react";
import { VideoOff, Play, Square, AlertCircle } from "lucide-react";
import { api } from "../api";
import type { PiDevice, StreamStartResult } from "../types";

type StreamState = "idle" | "connecting" | "live" | "stopping" | "error";

export default function LiveView() {
  const [devices, setDevices] = useState<PiDevice[]>([]);
  const [selectedDevice, setSelectedDevice] = useState<string>("");
  const [streamState, setStreamState] = useState<StreamState>("idle");
  const [error, setError] = useState<string | null>(null);
  const [startTime, setStartTime] = useState<number | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const videoRef = useRef<HTMLVideoElement>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const streamInfoRef = useRef<StreamStartResult | null>(null);

  useEffect(() => {
    api.getDevices().then((h) => {
      setDevices(h.devices.filter((d) => d.online));
      if (h.devices.filter((d) => d.online).length === 1) {
        setSelectedDevice(h.devices.filter((d) => d.online)[0].thingName);
      }
    });
  }, []);

  // Elapsed timer
  useEffect(() => {
    if (streamState !== "live" || !startTime) return;
    const interval = setInterval(() => setElapsed(Math.floor((Date.now() - startTime) / 1000)), 1000);
    return () => clearInterval(interval);
  }, [streamState, startTime]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      cleanup();
      if (selectedDevice && streamState !== "idle") {
        api.stopStream(selectedDevice).catch(() => {});
      }
    };
  }, []);

  const cleanup = () => {
    if (pcRef.current) {
      pcRef.current.close();
      pcRef.current = null;
    }
    if (wsRef.current) {
      wsRef.current.close();
      wsRef.current = null;
    }
    if (videoRef.current) {
      videoRef.current.srcObject = null;
    }
  };

  const handleStart = async () => {
    if (!selectedDevice) return;
    setStreamState("connecting");
    setError(null);

    try {
      const result = await api.startStream(selectedDevice);
      streamInfoRef.current = result;

      // Wait for Pi to start streaming
      await new Promise((r) => setTimeout(r, 3000));

      await connectWebRTC(result);
      setStreamState("live");
      setStartTime(Date.now());
    } catch (e) {
      setError((e as Error).message);
      setStreamState("error");
    }
  };

  const handleStop = async () => {
    if (!selectedDevice) return;
    setStreamState("stopping");
    cleanup();
    try {
      await api.stopStream(selectedDevice);
    } catch (e) {
      console.error("Stop failed:", e);
    }
    setStreamState("idle");
    setStartTime(null);
    setElapsed(0);
  };

  const connectWebRTC = async (info: StreamStartResult) => {
    if (!info.wssEndpoint || !info.iceServers) {
      throw new Error("Missing WebRTC endpoint or ICE servers");
    }

    const iceServers: RTCIceServer[] = [
      { urls: "stun:stun.kinesisvideo.eu-west-1.amazonaws.com:443" },
      ...info.iceServers.map((s) => ({
        urls: s.urls,
        username: s.username,
        credential: s.credential,
      })),
    ];

    const pc = new RTCPeerConnection({ iceServers });
    pcRef.current = pc;

    pc.ontrack = (event) => {
      if (videoRef.current && event.streams[0]) {
        videoRef.current.srcObject = event.streams[0];
      }
    };

    // Connect to signaling channel as viewer
    const clientId = `viewer-${Date.now()}`;
    const wsUrl = `${info.wssEndpoint}?X-Amz-ChannelARN=${encodeURIComponent(info.channelArn)}&X-Amz-ClientId=${encodeURIComponent(clientId)}`;

    return new Promise<void>((resolve, reject) => {
      const ws = new WebSocket(wsUrl);
      wsRef.current = ws;

      const timeout = setTimeout(() => {
        reject(new Error("WebRTC connection timeout"));
        ws.close();
      }, 15000);

      ws.onopen = () => {
        // Send SDP offer to master
        pc.createOffer({ offerToReceiveVideo: true, offerToReceiveAudio: false })
          .then((offer) => pc.setLocalDescription(offer))
          .then(() => {
            ws.send(
              JSON.stringify({
                action: "SDP_OFFER",
                messagePayload: btoa(JSON.stringify(pc.localDescription)),
                recipientClientId: "master",
              })
            );
          })
          .catch(reject);
      };

      ws.onmessage = async (event) => {
        const msg = JSON.parse(event.data);
        const payload = JSON.parse(atob(msg.messagePayload));

        if (msg.messageType === "SDP_ANSWER") {
          await pc.setRemoteDescription(new RTCSessionDescription(payload));
          clearTimeout(timeout);
          resolve();
        } else if (msg.messageType === "ICE_CANDIDATE") {
          await pc.addIceCandidate(new RTCIceCandidate(payload));
        }
      };

      pc.onicecandidate = (event) => {
        if (event.candidate && ws.readyState === WebSocket.OPEN) {
          ws.send(
            JSON.stringify({
              action: "ICE_CANDIDATE",
              messagePayload: btoa(JSON.stringify(event.candidate)),
              recipientClientId: "master",
            })
          );
        }
      };

      ws.onerror = () => {
        clearTimeout(timeout);
        reject(new Error("WebSocket connection failed"));
      };
    });
  };

  const formatElapsed = (s: number) => {
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return `${m}:${sec.toString().padStart(2, "0")}`;
  };

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">Live View</h1>

      {/* Device selector */}
      <div className="bg-white rounded-xl border border-gray-200 p-5 mb-4">
        <div className="flex items-center gap-4">
          <select
            value={selectedDevice}
            onChange={(e) => setSelectedDevice(e.target.value)}
            disabled={streamState !== "idle"}
            className="flex-1 px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
          >
            <option value="">Select a device...</option>
            {devices.map((d) => (
              <option key={d.thingName} value={d.thingName}>
                {d.hostname || d.thingName}
              </option>
            ))}
          </select>

          {streamState === "idle" || streamState === "error" ? (
            <button
              onClick={handleStart}
              disabled={!selectedDevice}
              className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-lg disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Play className="w-4 h-4" />
              Start Stream
            </button>
          ) : streamState === "connecting" ? (
            <button disabled className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg opacity-75">
              <div className="w-4 h-4 border-2 border-white border-t-transparent rounded-full animate-spin" />
              Connecting...
            </button>
          ) : (
            <button
              onClick={handleStop}
              disabled={streamState === "stopping"}
              className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-red-600 hover:bg-red-700 rounded-lg disabled:opacity-50"
            >
              <Square className="w-4 h-4" />
              {streamState === "stopping" ? "Stopping..." : "Stop Stream"}
            </button>
          )}
        </div>
      </div>

      {/* Error */}
      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-lg text-sm flex items-center gap-2">
          <AlertCircle className="w-4 h-4 flex-shrink-0" />
          {error}
        </div>
      )}

      {/* Video */}
      <div className="bg-black rounded-xl overflow-hidden relative" style={{ aspectRatio: "4/3" }}>
        <video
          ref={videoRef}
          autoPlay
          playsInline
          muted
          className="w-full h-full object-contain"
        />

        {streamState === "idle" && (
          <div className="absolute inset-0 flex items-center justify-center">
            <div className="text-center text-gray-500">
              <VideoOff className="w-12 h-12 mx-auto mb-2 opacity-50" />
              <p className="text-sm">Select a device and start streaming</p>
            </div>
          </div>
        )}

        {streamState === "connecting" && (
          <div className="absolute inset-0 flex items-center justify-center bg-black bg-opacity-50">
            <div className="text-center text-white">
              <div className="w-8 h-8 border-2 border-white border-t-transparent rounded-full animate-spin mx-auto mb-2" />
              <p className="text-sm">Connecting to camera...</p>
            </div>
          </div>
        )}

        {streamState === "live" && (
          <div className="absolute top-3 left-3 flex items-center gap-2">
            <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium bg-red-600 text-white">
              <span className="w-2 h-2 rounded-full bg-white animate-pulse" />
              LIVE
            </span>
            <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-black bg-opacity-60 text-white">
              {formatElapsed(elapsed)}
            </span>
          </div>
        )}
      </div>
    </div>
  );
}
