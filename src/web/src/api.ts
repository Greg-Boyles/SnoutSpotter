import type { Clip, Detection, StatsOverview, SystemHealth } from "./types";

const BASE = import.meta.env.VITE_API_URL || "/api";

async function fetchJson<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`);
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json() as Promise<T>;
}

export const api = {
  getStats: () => fetchJson<StatsOverview>("/stats"),

  getClips: (page = 1, pageSize = 20) =>
    fetchJson<{ clips: Clip[]; nextPageKey: string | null; totalCount: number }>(
      `/clips?page=${page}&pageSize=${pageSize}`,
    ),

  getClip: (id: string) => fetchJson<Clip>(`/clips/${id}`),

  getDetections: (clipId?: string) =>
    fetchJson<Detection[]>(
      clipId ? `/detections?clipId=${clipId}` : "/detections",
    ),

  getHealth: () => fetchJson<SystemHealth>("/stats/health"),
};
