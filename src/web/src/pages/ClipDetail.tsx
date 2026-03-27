import { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft } from "lucide-react";
import { api } from "../api";
import type { Clip, Detection } from "../types";
import BoundingBoxOverlay from "../components/BoundingBoxOverlay";

export default function ClipDetail() {
  const { id } = useParams<{ id: string }>();
  const [clip, setClip] = useState<Clip | null>(null);
  const [detections, setDetections] = useState<Detection[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!id) return;
    Promise.all([api.getClip(id), api.getDetections(id)])
      .then(([c, d]) => {
        setClip(c);
        setDetections(d);
      })
      .catch((e: Error) => setError(e.message));
  }, [id]);

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
    );
  }

  if (!clip) return <div className="text-gray-400">Loading...</div>;

  return (
    <div>
      <Link
        to="/clips"
        className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4"
      >
        <ArrowLeft className="w-4 h-4" /> Back to clips
      </Link>

      <h1 className="text-xl font-bold text-gray-900 mb-4">
        Clip {clip.clipId}
      </h1>

      {/* Clip Info */}
      <div className="bg-white rounded-lg border border-gray-200 p-4 mb-6 max-w-md">
        <div className="space-y-2 text-sm">
          <div className="flex justify-between">
            <span className="text-gray-500">Duration:</span>
            <span className="font-medium">{clip.durationSeconds}s</span>
          </div>
          <div className="flex justify-between">
            <span className="text-gray-500">Date:</span>
            <span className="font-medium">{clip.date}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-gray-500">Detection:</span>
            <span className="font-medium">{clip.detectionType}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-gray-500">Keyframes:</span>
            <span className="font-medium">{clip.keyframeCount}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-gray-500">Detections:</span>
            <span className="font-medium">{clip.detectionCount}</span>
          </div>
        </div>
      </div>

      {/* Detections */}
      {detections.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-gray-700 mb-2">
            Detections ({detections.length})
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            {detections.map((d) => (
              <div
                key={d.detectionId}
                className="bg-white rounded-lg border border-gray-200 p-3"
              >
                <BoundingBoxOverlay detection={d} />
                <div className="mt-2 flex items-center justify-between text-sm">
                  <span className="font-medium text-gray-700">{d.label}</span>
                  <span
                    className={`px-2 py-0.5 rounded-full text-xs ${
                      d.isTargetDog
                        ? "bg-amber-100 text-amber-700"
                        : "bg-gray-100 text-gray-600"
                    }`}
                  >
                    {(d.confidence * 100).toFixed(0)}%
                    {d.isTargetDog && " ★"}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
