import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { Video, Clock } from "lucide-react";
import { api } from "../api";
import type { Clip } from "../types";

export default function ClipsBrowser() {
  const [clips, setClips] = useState<Clip[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const pageSize = 20;

  useEffect(() => {
    api
      .getClips(page, pageSize)
      .then((data) => {
        setClips(data.items);
        setTotal(data.total);
      })
      .catch((e: Error) => setError(e.message));
  }, [page]);

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">
        Failed to load clips: {error}
      </div>
    );
  }

  const totalPages = Math.ceil(total / pageSize);

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Clips</h1>
        <span className="text-sm text-gray-500">{total} total</span>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {clips.map((clip) => (
          <Link
            key={clip.clipId}
            to={`/clips/${clip.clipId}`}
            className="group bg-white rounded-xl border border-gray-200 overflow-hidden hover:shadow-md transition-shadow"
          >
            <div className="aspect-video bg-gray-100 flex items-center justify-center">
              {clip.thumbnailUrl ? (
                <img
                  src={clip.thumbnailUrl}
                  alt=""
                  className="w-full h-full object-cover"
                />
              ) : (
                <Video className="w-10 h-10 text-gray-300" />
              )}
            </div>
            <div className="p-3">
              <div className="flex items-center gap-2 text-xs text-gray-500">
                <Clock className="w-3 h-3" />
                {formatDistanceToNow(new Date(clip.uploadedAt), {
                  addSuffix: true,
                })}
              </div>
              <div className="flex items-center justify-between mt-1">
                <span className="text-xs text-gray-400">
                  {clip.durationSeconds}s
                </span>
                <span
                  className={`text-xs px-2 py-0.5 rounded-full ${
                    clip.status === "complete"
                      ? "bg-green-50 text-green-700"
                      : clip.status === "error"
                        ? "bg-red-50 text-red-700"
                        : "bg-yellow-50 text-yellow-700"
                  }`}
                >
                  {clip.status}
                </span>
              </div>
            </div>
          </Link>
        ))}
      </div>

      {totalPages > 1 && (
        <div className="flex items-center justify-center gap-2 mt-6">
          <button
            onClick={() => setPage((p) => Math.max(1, p - 1))}
            disabled={page === 1}
            className="px-3 py-1 text-sm rounded-lg border border-gray-200 disabled:opacity-40"
          >
            Prev
          </button>
          <span className="text-sm text-gray-500">
            {page} / {totalPages}
          </span>
          <button
            onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
            disabled={page === totalPages}
            className="px-3 py-1 text-sm rounded-lg border border-gray-200 disabled:opacity-40"
          >
            Next
          </button>
        </div>
      )}
    </div>
  );
}
