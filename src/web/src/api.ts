import type {
  Clip,
  Detection,
  DeviceLinkDto,
  DeviceListResponse,
  LogEntry,
  Pet,
  SnoutSpotterDeviceDto,
  SpcDeviceRegistryDto,
  SpcEventsPage,
  StatsOverview,
  StreamStartResult,
  SystemHealth,
  UpdateDeviceRequest,
} from "./types";

const BASE = import.meta.env.VITE_API_URL || "/api";
const PI_MGMT_BASE = import.meta.env.VITE_PI_MGMT_URL || "";
const SPC_INTEGRATION_BASE = import.meta.env.VITE_SPC_INTEGRATION_URL || "";

let getAccessToken: (() => string | undefined) | null = null;
let getHouseholdId: (() => string | undefined) | null = null;

export function setAuthGetter(getter: () => string | undefined) {
  getAccessToken = getter;
}

export function setHouseholdGetter(getter: () => string | undefined) {
  getHouseholdId = getter;
}

function authHeaders(): Record<string, string> {
  const headers: Record<string, string> = {};
  const token = getAccessToken?.();
  if (token) headers["Authorization"] = `Bearer ${token}`;
  const hhId = getHouseholdId?.();
  if (hhId) headers["X-Household-Id"] = hhId;
  return headers;
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

// SPC connector calls go to a separate API Gateway. Responses use a typed
// error envelope ({ error: "<code>" }) so the UI can distinguish
// invalid_credentials / token_expired / session_expired etc. without
// string-matching the HTTP status text.

export class SpcApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, message?: string) {
    super(message ?? `SPC ${status}: ${code}`);
  }
}

async function spcFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${SPC_INTEGRATION_BASE}${path}`, {
    ...init,
    headers: {
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...authHeaders(),
      ...(init.headers ?? {}),
    },
  });
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  const body = text ? (JSON.parse(text) as Record<string, unknown>) : {};
  if (!res.ok) {
    const code = typeof body.error === "string" ? body.error : `http_${res.status}`;
    throw new SpcApiError(res.status, code);
  }
  return body as T;
}

export const api = {
  getStats: () => fetchJson<StatsOverview>("/stats"),

  getActivity: (days = 14) =>
    fetchJson<{ activity: { date: string; clips: number }[] }>(`/stats/activity?days=${days}`),

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

  getDevices: () => fetchJson<SystemHealth>("/device/devices"),

  getRawShadow: (thingName: string) =>
    fetch(`${BASE}/device/${thingName}/shadow`, { headers: { ...authHeaders() } })
      .then((r) => { if (!r.ok) throw new Error(`${r.status}`); return r.json(); }),

  triggerPiUpdate: (thingName: string, version?: string) =>
    postJson<{ message: string; version: string }>(`/device/${thingName}/update`, version ? { version } : {}),

  triggerPiUpdateAll: (version?: string) =>
    postJson<{ message: string; deviceCount: number; version: string }>("/device/update-all", version ? { version } : {}),

  getPiConfig: (thingName: string) =>
    fetchJson<{ config: Record<string, number | boolean | string> | null; configErrors: Record<string, string> | null }>(`/device/${thingName}/config`),

  updatePiConfig: (thingName: string, changes: Record<string, number | boolean | string>) =>
    postJson<{ message: string; errors: Record<string, string> }>(`/device/${thingName}/config`, changes),

  getDeviceLogs: (thingName: string, minutes = 60, level?: string, service?: string) => {
    const params = new URLSearchParams({ minutes: String(minutes) });
    if (level) params.set("level", level);
    if (service) params.set("service", service);
    return fetchJson<{ logs: LogEntry[]; thingName: string; queryMinutes: number }>(
      `/device/${thingName}/logs?${params}`,
    );
  },

  // Commands
  sendCommand: (thingName: string, action: string) =>
    postJson<{ commandId: string; message: string }>(`/device/${thingName}/command`, { action }),

  getCommandResult: (thingName: string, commandId: string) =>
    fetchJson<{ commandId: string; status: string; action?: string; message?: string; error?: string; requestedAt?: string; completedAt?: string }>(`/device/${thingName}/command/${commandId}`),

  getCommandHistory: (thingName: string, limit = 50) =>
    fetchJson<{ commands: Record<string, string>[]; thingName: string }>(`/device/${thingName}/commands?limit=${limit}`),

  // ML Labels
  triggerAutoLabel: (date?: string) =>
    postJson<{ message: string }>(`/ml/auto-label${date ? `?date=${date}` : ""}`),

  getLabelStats: () =>
    fetchJson<{ total: number; dogs: number; noDogs: number; reviewed: number; unreviewed: number; myDog: number; otherDog: number; confirmedNoDog: number; myDogWithBoxes: number; myDogWithoutBoxes: number; otherDogWithBoxes: number; otherDogWithoutBoxes: number; petCounts: Record<string, number>; petWithBoxes: Record<string, number>; petWithoutBoxes: Record<string, number>; breeds: Record<string, number> }>("/ml/labels/stats"),

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
  triggerExport: (config?: { maxPerClass?: number; includeBackground?: boolean; backgroundRatio?: number; exportType?: string; cropPadding?: number; mergeClasses?: boolean }) =>
    postJson<{ exportId: string; message: string }>("/ml/export", config),

  listExports: () =>
    fetchJson<{ exports: Record<string, string>[] }>("/ml/exports"),

  getExportDownload: (exportId: string) =>
    fetchJson<{ downloadUrl: string }>(`/ml/exports/${exportId}/download`),

  deleteExport: (exportId: string) =>
    deleteJson<{ message: string }>(`/ml/exports/${exportId}`),

  deleteClip: (id: string) =>
    deleteJson<null>(`/clips/${id}`),

  // Models
  listModels: (type?: string) =>
    fetchJson<{ activeVersion: string | null; versions: { version: string; s3Key: string; sizeBytes: number; lastModified: string; active: boolean; source?: string; trainingJobId?: string; notes?: string; metrics?: Record<string, number> }[] }>(`/ml/models${type ? `?type=${type}` : ""}`),

  getModelUploadUrl: (version: string, type?: string) =>
    postJson<{ uploadUrl: string; s3Key: string; version: string; expiresIn: number }>(`/ml/models/upload-url?version=${encodeURIComponent(version)}${type ? `&type=${type}` : ""}`),

  activateModel: (version: string, type?: string) =>
    postJson<{ message: string; version: string }>(`/ml/models/activate?version=${encodeURIComponent(version)}${type ? `&type=${type}` : ""}`),

  rerunInference: (dateFrom?: string, dateTo?: string, clipIds?: string[]) =>
    postJson<{ total: number; queued: number }>("/ml/rerun-inference", { dateFrom, dateTo, clipIds }),

  // Queue Stats
  getQueueStats: () =>
    fetchJson<{ queues: { name: string; pending: number; inFlight: number; dlqPending: number }[] }>("/stats/queues"),

  // Server Settings
  getSettings: () =>
    fetchJson<{ settings: { key: string; value: string; default: string; label: string; type: string; min: number; max: number; description: string }[] }>("/settings"),

  updateSetting: (key: string, value: string) =>
    putJson<{ message: string }>(`/settings/${encodeURIComponent(key)}`, { value }),

  resetSettings: () =>
    postJson<{ message: string }>("/settings/reset"),

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
    fetchJson<{ agents: { thingName: string; online: boolean; version: string | null; hostname: string | null; lastHeartbeat: string | null; currentJobId: string | null; status?: string | null; gpu?: { name: string; vramMb: number; temperatureC: number; utilizationPercent: number } | null; currentJobProgress?: { epoch: number; total_epochs: number; epoch_progress?: number; mAP50?: number } | null }[] }>("/training/agents"),

  getTrainingAgentStatus: (thingName: string) =>
    fetchJson<{ thingName: string; online: boolean; reported: Record<string, unknown> | null }>(`/training/agents/${thingName}`),

  triggerAgentUpdate: (thingName: string, version: string) =>
    postJson<{ message: string }>(`/training/agents/${thingName}/update`, { version }),

  listTrainingAgentReleases: () =>
    fetchJson<{ releases: { version: string; imagePushedAt: string; isLatest: boolean }[]; latestVersion: string | null }>("/training/agents/releases"),

  submitTrainingJob: (config: { exportId: string; exportS3Key: string; epochs?: number; batchSize?: number; imageSize?: number; learningRate?: number; workers?: number; modelBase?: string; resumeFrom?: string | null; notes?: string; jobType?: string }) =>
    postJson<{ jobId: string }>("/training/jobs", config),

  listTrainingJobs: (status?: string, limit = 50) =>
    fetchJson<{ jobs: { jobId: string; status: string; agentThingName: string | null; exportId: string | null; epochs: number | null; createdAt: string | null; startedAt: string | null; completedAt: string | null; finalMAP50: number | null; jobType: string }[] }>(`/training/jobs?${status ? `status=${status}&` : ""}limit=${limit}`),

  getTrainingJob: (jobId: string) =>
    fetchJson<{
      jobId: string; status: string; agentThingName: string | null;
      exportId: string | null; exportS3Key: string | null;
      config: { epochs: number; batch_size: number; image_size: number; learning_rate: number; workers: number; model_base: string; resume_from: string | null } | null;
      progress: { epoch: number; total_epochs: number; epoch_progress?: number; train_loss?: number; val_loss?: number; mAP50?: number; mAP50_95?: number; best_mAP50?: number; elapsed_seconds?: number; eta_seconds?: number; gpu_util_percent?: number; gpu_temp_c?: number; download_bytes?: number; download_total_bytes?: number; download_speed_mbps?: number; accuracy?: number; f1_score?: number } | null;
      result: { model_s3_key: string; model_size_mb: number; final_mAP50: number; final_mAP50_95?: number; total_epochs: number; best_epoch: number; training_time_seconds: number; dataset_images: number; classes: string[]; precision?: number; recall?: number; accuracy?: number; f1_score?: number } | null;
      checkpointS3Key: string | null; error: string | null; failedStage: string | null;
      createdAt: string | null; startedAt: string | null; completedAt: string | null;
      jobType: string;
    }>(`/training/jobs/${jobId}`),

  cancelTrainingJob: (jobId: string) =>
    postJson<{ message: string }>(`/training/jobs/${jobId}/cancel`),

  deleteTrainingJob: (jobId: string) =>
    deleteJson<null>(`/training/jobs/${jobId}`),

  // Pi Releases
  listPiReleases: () =>
    fetchJson<{ releases: { version: string; s3Key: string; sizeBytes: number; lastModified: string; isLatest: boolean }[]; latestVersion: string | null }>("/device/releases"),

  deletePiRelease: (version: string) =>
    deleteJson<{ message: string }>(`/device/releases/${encodeURIComponent(version)}`),


  // Pets
  listPets: () => fetchJson<Pet[]>("/pets"),
  getPet: (petId: string) => fetchJson<Pet>(`/pets/${petId}`),
  createPet: (name: string, breed?: string) => postJson<Pet>("/pets", { name, breed }),
  updatePet: (petId: string, name: string, breed?: string) => putJson<Pet>(`/pets/${petId}`, { name, breed }),
  deletePet: (petId: string) => deleteJson<null>(`/pets/${petId}`),

  listSpcEventsForPet: (petId: string, limit = 50, nextPageKey?: string) => {
    const qs = new URLSearchParams({ limit: String(limit) });
    if (nextPageKey) qs.set("nextPageKey", nextPageKey);
    return fetchJson<SpcEventsPage>(`/pets/${encodeURIComponent(petId)}/spc-events?${qs}`);
  },

  // Households
  listHouseholds: () =>
    fetchJson<{ households: { householdId: string; name: string; createdAt: string }[]; userId: string; email: string | null; name: string | null }>("/households"),

  createHousehold: (name: string) =>
    postJson<{ householdId: string; name: string; createdAt: string }>("/households", { name }),

  // Pi Management API (separate endpoint)
  registerDevice: (name: string) =>
    postJson<DeviceRegistrationResult>("/api/devices/register", { name }, PI_MGMT_BASE),

  deregisterDevice: (thingName: string) =>
    deleteJson<{ message: string }>(`/api/devices/${thingName}`, PI_MGMT_BASE),

  listManagedDevices: () =>
    fetchJson<{ devices: string[] }>("/api/devices"),

  // Sure Pet Care integration (separate endpoint)
  spc: {
    status: () =>
      spcFetch<{
        status: "unlinked" | "linked" | "token_expired" | "error";
        spcUserEmail: string | null;
        spcHouseholdId: string | null;
        spcHouseholdName: string | null;
        linkedAt: string | null;
        lastSyncAt: string | null;
        lastError: string | null;
      }>("/api/integrations/spc/status"),

    validate: (email: string, password: string) =>
      spcFetch<{ sessionId: string; spcUserEmail: string; expiresAt: string }>(
        "/api/integrations/spc/validate",
        { method: "POST", body: JSON.stringify({ email, password }) },
      ),

    listSpcHouseholds: (sessionId: string) =>
      spcFetch<{ households: { id: string; name: string }[] }>(
        `/api/integrations/spc/spc-households?session=${encodeURIComponent(sessionId)}`,
      ),

    listSpcPets: (sessionId: string, spcHouseholdId: string) =>
      spcFetch<{ pets: { id: string; name: string; species: string | null; photoUrl: string | null }[] }>(
        `/api/integrations/spc/spc-pets?session=${encodeURIComponent(sessionId)}&spcHouseholdId=${encodeURIComponent(spcHouseholdId)}`,
      ),

    link: (body: { sessionId: string; spcHouseholdId: string; mappings: { petId: string; spcPetId: string | null; spcPetName: string | null }[] }) =>
      spcFetch<{ status: string; mappedCount: number }>("/api/integrations/spc/link", {
        method: "POST",
        body: JSON.stringify(body),
      }),

    unlink: () =>
      spcFetch<void>("/api/integrations/spc", { method: "DELETE" }),

    devices: () =>
      spcFetch<{ devices: { id: string; productId: number; name: string; serialNumber: string | null; lastActivityAt: string | null }[] }>(
        "/api/integrations/spc/devices",
      ),

    updatePetLinks: (mappings: { petId: string; spcPetId: string | null; spcPetName: string | null }[]) =>
      spcFetch<{ updatedCount: number }>("/api/integrations/spc/pet-links", {
        method: "PUT",
        body: JSON.stringify({ mappings }),
      }),
  },

  // Device registry — both SnoutSpotter cameras and Sure Pet Care devices
  // plus the many-to-many links between them. Served by the main API.
  devices: {
    list: () =>
      fetchJson<DeviceListResponse>("/devices"),

    updateSnoutSpotter: (thingName: string, body: UpdateDeviceRequest) =>
      putJson<SnoutSpotterDeviceDto>(`/devices/snoutspotter/${encodeURIComponent(thingName)}`, body),

    updateSpc: (spcDeviceId: string, body: UpdateDeviceRequest) =>
      putJson<SpcDeviceRegistryDto>(`/devices/spc/${encodeURIComponent(spcDeviceId)}`, body),

    refreshSpc: (spcDeviceId: string) =>
      postJson<SpcDeviceRegistryDto>(`/devices/spc/${encodeURIComponent(spcDeviceId)}/refresh`),

    link: (spcDeviceId: string, snoutspotterThingName: string) =>
      postJson<DeviceLinkDto>("/devices/links", { spcDeviceId, snoutspotterThingName }),

    unlink: (spcDeviceId: string, thingName: string) =>
      deleteJson<null>(`/devices/links/${encodeURIComponent(spcDeviceId)}/${encodeURIComponent(thingName)}`),
  },
};
