import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { Video, Clock, Trash2 } from "lucide-react";
import { api } from "../api";
import type { Clip } from "../types";

const DETECTION_OPTIONS = [
  { value: "", label: "All detections" },
  { value: "my_dog", label: "My Dog" },
  { value: "other_dog", label: "Other Dog" },
  { value: "no_dog", label: "No Dog" },
  { value: "pending", label: "Pending" },
];

export default function ClipsBrowser() {
  const [searchParams, setSearchParams] = useSearchParams();

  const deviceFilter = searchParams.get("device") ?? "";
  const dateFilter = searchParams.get("date") ?? "";
  const detectionFilter = searchParams.get("detection") ?? "";

  const setParam = (key: string, value: string) => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (value) next.set(key, value); else next.delete(key);
      // reset pagination when a filter changes
      next.delete("page");
      return next;
    }, { replace: true });
  };

  const [clips, setClips] = useState<Clip[]>([]);
  const [total, setTotal] = useState(0);
  const [nextPageKey, setNextPageKey] = useState<string | null>(null);
  const [pageKeys, setPageKeys] = useState<(string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [devices, setDevices] = useState<string[]>([]);
  const pageSize = 20;

  useEffect(() => {
    api.getDevices().then((data) => setDevices(data.devices.map((d) => d.thingName))).catch(console.error);
  }, []);

  // Reset pagination when filters change
  useEffect(() => {
    setPageKeys([undefined]);
    setPageIndex(0);
  }, [deviceFilter, dateFilter, detectionFilter]);

  useEffect(() => {
    // Convert date from YYYY-MM-DD (input) to YYYY/MM/DD (API)
    const apiDate = dateFilter ? dateFilter.replace(/-/g, "/") : undefined;
    api
      .getClips(pageSize, pageKeys[pageIndex], deviceFilter || undefined, apiDate, detectionFilter || undefined)
      .then((data) => {
        setClips(data.clips);
        setTotal(data.totalCount);
        setNextPageKey(data.nextPageKey);
      })
      .catch((e: Error) => setError(e.message));
  }, [pageIndex, pageKeys, deviceFilter, dateFilter, detectionFilter]);

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">
        Failed to load clips: {error}
      </div>
    );
  }

  return (
    <div>
      <div className="flex flex-wrap items-center gap-3 mb-6">
        <h1 className="text-2xl font-bold text-gray-900 mr-auto">Clips</h1>

        <input
          type="date"
          value={dateFilter}
          onChange={(e) => setParam("date", e.target.value)}
          className="text-sm border border-gray-300 rounded-lg px-3 py-1.5"
        />

        <select
          value={detectionFilter}
          onChange={(e) => setParam("detection", e.target.value)}
          className="text-sm border border-gray-300 rounded-lg px-3 py-1.5"
        >
          {DETECTION_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>{o.label}</option>
          ))}
        </select>

        {devices.length > 0 && (
          <select
            value={deviceFilter}
            onChange={(e) => setParam("device", e.target.value)}
            className="text-sm border border-gray-300 rounded-lg px-3 py-1.5"
          >
            <option value="">All devices</option>
            {devices.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        )}

        <span className="text-sm text-gray-500">{total} total</span>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {clips.map((clip) => (
          <div key={clip.clipId} className="group relative bg-white rounded-xl border border-gray-200 overflow-hidden hover:shadow-md transition-shadow">
            <Link to={`/clips/${clip.clipId}`} className="block">
              <div className="aspect-video bg-gray-100 flex items-center justify-center">
                {clip.thumbnailUrl ? (
                  <img
                    src={clip.thumbnailUrl}
                    alt="Clip thumbnail"
                    className="w-full h-full object-cover"
                  />
                ) : (
                  <Video className="w-10 h-10 text-gray-300" />
                )}
              </div>
              <div className="p-3">
                <div className="flex items-center gap-2 text-xs text-gray-500">
                  <Clock className="w-3 h-3" />
                  {formatDistanceToNow(new Date(clip.createdAt), { addSuffix: true })}
                </div>
                <div className="flex items-center justify-between mt-1">
                  <span className="text-xs text-gray-400">{clip.durationSeconds}s</span>
                  <span className={`text-xs px-2 py-0.5 rounded-full ${
                    clip.detectionType === "my_dog"
                      ? "bg-amber-50 text-amber-700"
                      : clip.detectionType === "other_dog"
                        ? "bg-blue-50 text-blue-700"
                        : "bg-gray-50 text-gray-700"
                  }`}>
                    {clip.detectionType}
                  </span>
                </div>
              </div>
            </Link>
            <button
              onClick={async (e) => {
                e.stopPropagation();
                if (!window.confirm("Delete this clip and all associated keyframes and labels? This cannot be undone.")) return;
                await api.deleteClip(clip.clipId);
                setClips((prev) => prev.filter((c) => c.clipId !== clip.clipId));
              }}
              className="absolute top-2 right-2 p-1.5 bg-white/80 hover:bg-red-50 text-gray-400 hover:text-red-600 rounded-lg opacity-0 group-hover:opacity-100 transition-opacity"
              title="Delete clip"
            >
              <Trash2 className="w-4 h-4" />
            </button>
          </div>
        ))}
      </div>

      {(pageIndex > 0 || nextPageKey) && (
        <div className="flex items-center justify-center gap-2 mt-6">
          <button
            onClick={() => setPageIndex((i) => i - 1)}
            disabled={pageIndex === 0}
            className="px-3 py-1 text-sm rounded-lg border border-gray-200 disabled:opacity-40"
          >
            Prev
          </button>
          <span className="text-sm text-gray-500">Page {pageIndex + 1}</span>
          <button
            onClick={() => {
              if (nextPageKey) {
                const newKeys = [...pageKeys];
                if (newKeys.length <= pageIndex + 1) newKeys.push(nextPageKey);
                setPageKeys(newKeys);
                setPageIndex((i) => i + 1);
              }
            }}
            disabled={!nextPageKey}
            className="px-3 py-1 text-sm rounded-lg border border-gray-200 disabled:opacity-40"
          >
            Next
          </button>
        </div>
      )}
    </div>
  );
}
