import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";
import type { Detection } from "../types";

export default function Detections() {
  const [detections, setDetections] = useState<Detection[]>([]);
  const [filter, setFilter] = useState<"all" | "target">("all");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getDetections()
      .then(setDetections)
      .catch((e: Error) => setError(e.message));
  }, []);

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
    );
  }

  const filtered =
    filter === "target" ? detections.filter((d) => d.isTargetDog) : detections;

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Detections</h1>
        <div className="flex gap-1 bg-gray-100 rounded-lg p-1">
          {(["all", "target"] as const).map((f) => (
            <button
              key={f}
              onClick={() => setFilter(f)}
              className={`px-3 py-1 text-sm rounded-md transition-colors ${
                filter === f
                  ? "bg-white text-gray-900 shadow-sm"
                  : "text-gray-500"
              }`}
            >
              {f === "all" ? "All" : "Target Dog"}
            </button>
          ))}
        </div>
      </div>

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-100 text-left text-gray-500">
              <th className="px-4 py-3 font-medium">Label</th>
              <th className="px-4 py-3 font-medium">Confidence</th>
              <th className="px-4 py-3 font-medium">Clip</th>
              <th className="px-4 py-3 font-medium">When</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((d) => (
              <tr
                key={d.detectionId}
                className="border-b border-gray-50 hover:bg-gray-50"
              >
                <td className="px-4 py-3 flex items-center gap-2">
                  {d.label}
                  {d.isTargetDog && (
                    <span className="text-amber-500 text-xs">★ Target</span>
                  )}
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <div className="w-16 h-1.5 bg-gray-100 rounded-full overflow-hidden">
                      <div
                        className="h-full bg-amber-500 rounded-full"
                        style={{ width: `${d.confidence * 100}%` }}
                      />
                    </div>
                    <span className="text-gray-600">
                      {(d.confidence * 100).toFixed(0)}%
                    </span>
                  </div>
                </td>
                <td className="px-4 py-3">
                  <Link
                    to={`/clips/${d.clipId}`}
                    className="text-amber-600 hover:underline"
                  >
                    {d.clipId.slice(0, 8)}…
                  </Link>
                </td>
                <td className="px-4 py-3 text-gray-500">
                  {formatDistanceToNow(new Date(d.timestamp), {
                    addSuffix: true,
                  })}
                </td>
              </tr>
            ))}
            {filtered.length === 0 && (
              <tr>
                <td colSpan={4} className="px-4 py-8 text-center text-gray-400">
                  No detections found
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
