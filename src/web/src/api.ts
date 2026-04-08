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

async function putJson<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function patchJson<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function deleteJson<T>(path: string, baseUrl = BASE): Promise<T | null> {
  const res = await fetch(`${baseUrl}${path}`, {
    method: "DELETE",
    headers: { ...authHeaders() },
  });
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  if (res.status === 204) return null;
  return res.json() as Promise<T>;
}

export const api = {
  getStats: () => fetchJson<StatsOverview>("/stats"),

  getActivity: (days = 14) =>
    fetchJson<{ activity: { date: string; count: number }[] }>(`/stats/activity?days=${days}`),

  getClips: (limit = 20, nextPageKey?: string, device?: string, date?: string, detectionType?: string) =>
    fetchJson<{ clips: Clip[]; nextPageKey: string | null; totalCount: number }>(
      `/clips?limit=${limit}${nextPageKey ? `&nextPageKey=${encodeURIComponent(nextPageKey)}` : ""}${device ? `&device=${encodeURIComponent(device)}` : ""}${date ? `&date=${encodeURIComponent(date)}` : ""}${detectionType ? `&detectionType=${encodeURIComponent(detectionType)}` : ""}`,
    ),

  getClip: (id: string) => fetchJson<Clip>(`/clips/${id}`),

  getDetections: (clipId?: string) =>
    fetchJson<Detection[]>(
      clipId ? `/detections?clipId=${clipId}` : "/detections",
    ),

  runInference: (clipId: string) =>
    postJson<{ message: string; clipId: string }>(`/clips/${clipId}/infer`),

  getHealth: () => fetchJson<SystemHealth>("/stats/health"),

  getDevices: () => fetchJson<SystemHealth>("/pi/devices"),

  getRawShadow: (thingName: string) =>
    fetch(`${BASE}/pi/${thingName}/shadow`, { headers: { ...authHeaders() } })
      .then((r) => { if (!r.ok) throw new Error(`${r.status}`); return r.json(); }),

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

  // Commands
  sendCommand: (thingName: string, action: string) =>
    postJson<{ commandId: string; message: string }>(`/pi/${thingName}/command`, { action }),

  getCommandResult: (thingName: string, commandId: string) =>
    fetchJson<{ commandId: string; status: string; action?: string; message?: string; error?: string; requestedAt?: string; completedAt?: string }>(`/pi/${thingName}/command/${commandId}`),

  getCommandHistory: (thingName: string, limit = 50) =>
    fetchJson<{ commands: Record<string, string>[]; thingName: string }>(`/pi/${thingName}/commands?limit=${limit}`),

  // ML Labels
  triggerAutoLabel: (date?: string) =>
    postJson<{ message: string }>(`/ml/auto-label${date ? `?date=${date}` : ""}`),

  getLabelStats: () =>
    fetchJson<{ total: number; dogs: number; noDogs: number; reviewed: number; unreviewed: number; myDog: number; otherDog: number; confirmedNoDog: number; myDogWithBoxes: number; myDogWithoutBoxes: number; otherDogWithBoxes: number; otherDogWithoutBoxes: number; breeds: Record<string, number> }>("/ml/labels/stats"),

  getLabel: (keyframeKey: string) =>
    fetchJson<Record<string, string | null>>(`/ml/labels/${encodeURIComponent(keyframeKey)}`),

  getLabels: (params: { reviewed?: string; label?: string; confirmedLabel?: string; breed?: string; device?: string; limit?: number; nextPageKey?: string } = {}) => {
    const qs = new URLSearchParams();
    if (params.reviewed) qs.set("reviewed", params.reviewed);
    if (params.label) qs.set("label", params.label);
    if (params.confirmedLabel) qs.set("confirmedLabel", params.confirmedLabel);
    if (params.breed) qs.set("breed", params.breed);
    if (params.device) qs.set("device", params.device);
    if (params.limit) qs.set("limit", String(params.limit));
    if (params.nextPageKey) qs.set("nextPageKey", params.nextPageKey);
    return fetchJson<{ labels: Record<string, string | null>[]; nextPageKey: string | null }>(`/ml/labels?${qs}`);
  },

  updateLabel: (keyframeKey: string, confirmedLabel: string, breed?: string) =>
    putJson<{ message: string }>(`/ml/labels/${keyframeKey}`, { confirmedLabel, breed }),

  bulkConfirmLabels: (keyframeKeys: string[], confirmedLabel: string, breed?: string) =>
    postJson<{ message: string }>("/ml/labels/bulk-confirm", { keyframeKeys, confirmedLabel, breed }),

  updateBoundingBoxes: (keyframeKey: string, boxes: number[][]) =>
    patchJson<{ message: string; count: number }>("/ml/labels/bounding-boxes", { keyframeKey, boxes }),

  backfillBoundingBoxes: (confirmedLabel?: string, keys?: string[]) =>
    postJson<{ total: number; batches: number; message?: string }>("/ml/labels/backfill-boxes", { confirmedLabel, keys }),

  uploadTrainingImage: async (file: File, label: string = "my_dog", breed?: string) => {
    const formData = new FormData();
    formData.append("files", file);
    const qs = `label=${encodeURIComponent(label)}${breed ? `&breed=${encodeURIComponent(breed)}` : ""}`;
    const res = await fetch(`${BASE}/ml/labels/upload?${qs}`, {
      method: "POST",
      headers: { ...authHeaders() },
      body: formData,
    });
    if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
    return res.json() as Promise<{ uploaded: number; errors: string[]; labels: Record<string, string>[] }>;
  },

  // Training exports
  triggerExport: () =>
    postJson<{ exportId: string; message: string }>("/ml/export"),

  listExports: () =>
    fetchJson<{ exports: Record<string, string>[] }>("/ml/exports"),

  getExportDownload: (exportId: string) =>
    fetchJson<{ downloadUrl: string }>(`/ml/exports/${exportId}/download`),

  deleteExport: (exportId: string) =>
    deleteJson<{ message: string }>(`/ml/exports/${exportId}`),

  deleteClip: (id: string) =>
    deleteJson<null>(`/clips/${id}`),

  // Models
  listModels: () =>
    fetchJson<{ activeVersion: string | null; versions: { version: string; s3Key: string; sizeBytes: number; lastModified: string; active: boolean }[] }>("/ml/models"),

  getModelUploadUrl: (version: string) =>
    postJson<{ uploadUrl: string; s3Key: string; version: string; expiresIn: number }>(`/ml/models/upload-url?version=${encodeURIComponent(version)}`),

  activateModel: (version: string) =>
    postJson<{ message: string; version: string }>(`/ml/models/activate?version=${encodeURIComponent(version)}`),

  rerunInference: (dateFrom?: string, dateTo?: string) =>
    postJson<{ total: number; queued: number }>("/ml/rerun-inference", { dateFrom, dateTo }),

  // Streaming
  startStream: (thingName: string) =>
    postJson<StreamStartResult>(`/stream/${thingName}/start`),

  getStreamHlsUrl: (thingName: string) =>
    fetchJson<{ hlsUrl: string }>(`/stream/${thingName}/hls`),

  stopStream: (thingName: string) =>
    postJson<{ message: string }>(`/stream/${thingName}/stop`),

  getStreamStatus: (thingName: string) =>
    fetchJson<{ thingName: string; streaming: boolean; streamError?: string }>(`/stream/${thingName}/status`),

  // Training
  listTrainingAgents: () =>
    fetchJson<{ agents: { thingName: string; online: boolean; version: string | null; hostname: string | null; lastHeartbeat: string | null }[] }>("/training/agents"),

  getTrainingAgentStatus: (thingName: string) =>
    fetchJson<{ thingName: string; online: boolean; reported: Record<string, unknown> | null }>(`/training/agents/${thingName}`),

  triggerAgentUpdate: (thingName: string, version: string) =>
    postJson<{ message: string }>(`/training/agents/${thingName}/update`, { version }),

  submitTrainingJob: (config: { exportId: string; exportS3Key: string; epochs?: number; batchSize?: number; imageSize?: number; learningRate?: number; workers?: number; modelBase?: string; resumeFrom?: string | null; notes?: string }) =>
    postJson<{ jobId: string }>("/training/jobs", config),

  listTrainingJobs: (status?: string, limit = 50) =>
    fetchJson<{ jobs: { jobId: string; status: string; agentThingName: string | null; exportId: string | null; epochs: number | null; createdAt: string | null; completedAt: string | null }[] }>(`/training/jobs?${status ? `status=${status}&` : ""}limit=${limit}`),

  getTrainingJob: (jobId: string) =>
    fetchJson<{ jobId: string; status: string; agentThingName: string | null; exportId: string | null; exportS3Key: string | null; config: string | null; progress: string | null; result: string | null; checkpointS3Key: string | null; error: string | null; createdAt: string | null; startedAt: string | null; completedAt: string | null }>(`/training/jobs/${jobId}`),

  cancelTrainingJob: (jobId: string) =>
    postJson<{ message: string }>(`/training/jobs/${jobId}/cancel`),

  // Pi Releases
  listPiReleases: () =>
    fetchJson<{ releases: { version: string; s3Key: string; sizeBytes: number; lastModified: string; isLatest: boolean }[]; latestVersion: string | null }>("/pi/releases"),

  deletePiRelease: (version: string) =>
    deleteJson<{ message: string }>(`/pi/releases/${encodeURIComponent(version)}`),


  // Pi Management API (separate endpoint)
  registerDevice: (name: string) =>
    postJson<DeviceRegistrationResult>("/api/devices/register", { name }, PI_MGMT_BASE),

  deregisterDevice: (thingName: string) =>
    deleteJson<{ message: string }>(`/api/devices/${thingName}`, PI_MGMT_BASE),

  listManagedDevices: () =>
    fetchJson<{ devices: string[] }>("/api/devices"),
};
