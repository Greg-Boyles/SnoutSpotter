import type { Clip, Detection, LogEntry, StatsOverview, StreamStartResult, SystemHealth } from "./types";

const BASE = import.meta.env.VITE_API_URL || "/api";
const PI_MGMT_BASE = import.meta.env.VITE_PI_MGMT_URL || "";

let getAccessToken: (() => string | undefined) | null = null;

export function setAuthGetter(getter: () => string | undefined) {
  getAccessToken = getter;
}

function authHeaders(): Record<string, string> {
  const token = getAccessToken?.();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

interface DeviceRegistrationResult {
  thingName: string;
  certificatePem: string;
  privateKey: string;
  certificateArn: string;
  ioTEndpoint: string;
  rootCaUrl: string;
}

async function fetchJson<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { ...authHeaders() },
  });
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function postJson<T>(path: string, body?: unknown, baseUrl = BASE): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function deleteJson<T>(path: string, baseUrl = BASE): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    method: "DELETE",
    headers: { ...authHeaders() },
  });
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json() as Promise<T>;
}

export const api = {
  getStats: () => fetchJson<StatsOverview>("/stats"),

  getClips: (limit = 20, nextPageKey?: string) =>
    fetchJson<{ clips: Clip[]; nextPageKey: string | null; totalCount: number }>(
      `/clips?limit=${limit}${nextPageKey ? `&nextPageKey=${encodeURIComponent(nextPageKey)}` : ""}`,
    ),

  getClip: (id: string) => fetchJson<Clip>(`/clips/${id}`),

  getDetections: (clipId?: string) =>
    fetchJson<Detection[]>(
      clipId ? `/detections?clipId=${clipId}` : "/detections",
    ),

  getHealth: () => fetchJson<SystemHealth>("/stats/health"),

  getDevices: () => fetchJson<SystemHealth>("/pi/devices"),

  triggerPiUpdate: (thingName: string, version?: string) =>
    postJson<{ message: string; version: string }>(`/pi/${thingName}/update`, version ? { version } : {}),

  triggerPiUpdateAll: (version?: string) =>
    postJson<{ message: string; deviceCount: number; version: string }>("/pi/update-all", version ? { version } : {}),

  getPiConfig: (thingName: string) =>
    fetchJson<{ config: Record<string, number | boolean | string> | null; configErrors: Record<string, string> | null }>(`/pi/${thingName}/config`),

  updatePiConfig: (thingName: string, changes: Record<string, number | boolean | string>) =>
    postJson<{ message: string; errors: Record<string, string> }>(`/pi/${thingName}/config`, changes),

  getDeviceLogs: (thingName: string, minutes = 60, level?: string, service?: string) => {
    const params = new URLSearchParams({ minutes: String(minutes) });
    if (level) params.set("level", level);
    if (service) params.set("service", service);
    return fetchJson<{ logs: LogEntry[]; thingName: string; queryMinutes: number }>(
      `/pi/${thingName}/logs?${params}`,
    );
  },

  // Streaming
  startStream: (thingName: string) =>
    postJson<StreamStartResult>(`/stream/${thingName}/start`),

  getStreamHlsUrl: (thingName: string) =>
    fetchJson<{ hlsUrl: string }>(`/stream/${thingName}/hls`),

  stopStream: (thingName: string) =>
    postJson<{ message: string }>(`/stream/${thingName}/stop`),

  getStreamStatus: (thingName: string) =>
    fetchJson<{ thingName: string; streaming: boolean; streamError?: string }>(`/stream/${thingName}/status`),

  // Pi Management API (separate endpoint)
  registerDevice: (name: string) =>
    postJson<DeviceRegistrationResult>("/api/devices/register", { name }, PI_MGMT_BASE),

  deregisterDevice: (thingName: string) =>
    deleteJson<{ message: string }>(`/api/devices/${thingName}`, PI_MGMT_BASE),

  listManagedDevices: () =>
    fetchJson<{ devices: string[] }>("/api/devices"),
};
