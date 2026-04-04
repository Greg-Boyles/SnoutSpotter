import { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft, Loader2, Play } from "lucide-react";
import { api } from "../api";
import type { Clip, KeyframeDetection } from "../types";
import BoundingBoxOverlay from "../components/BoundingBoxOverlay";

export default function ClipDetail() {
  const { id } = useParams<{ id: string }>();
  const [clip, setClip] = useState<Clip | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [inferring, setInferring] = useState(false);

  useEffect(() => {
    if (!id) return;
    api
      .getClip(id)
      .then(setClip)
      .catch((e: Error) => setError(e.message));
  }, [id]);

  const handleRunInference = async () => {
    if (!id) return;
    setInferring(true);
    try {
      await api.runInference(id);
    } catch (e) {
      console.error("Inference trigger failed:", e);
    } finally {
      setInferring(false);
    }
  };

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
    );
  }

  if (!clip) return <div className="text-gray-400">Loading...</div>;

  // Build a map of keyframe key → detection data for overlay rendering
  const detectionsByKeyframe = new Map<string, KeyframeDetection>();
  clip.keyframeDetections?.forEach((kd) => {
    detectionsByKeyframe.set(kd.keyframeKey, kd);
  });

  return (
    <div>
      <Link
        to="/clips"
        className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4"
      >
        <ArrowLeft className="w-4 h-4" /> Back to clips
      </Link>

      <div className="flex items-center gap-3 mb-4">
        <h1 className="text-xl font-bold text-gray-900">
          Clip {clip.clipId}
        </h1>
        <button
          onClick={handleRunInference}
          disabled={inferring}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
        >
          {inferring ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
          {inferring ? "Running..." : "Run Inference"}
        </button>
      </div>

      {/* Video Player */}
      {clip.videoUrl && (
        <div className="mb-6 max-w-2xl">
          <video controls className="w-full rounded-lg border border-gray-200">
            <source src={clip.videoUrl} type="video/mp4" />
            Your browser does not support the video tag.
          </video>
        </div>
      )}

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

      {/* Keyframes with detection overlays */}
      {clip.keyframeUrls && clip.keyframeUrls.length > 0 && (
        <div className="mb-6">
          <h2 className="text-sm font-semibold text-gray-700 mb-2">
            Keyframes ({clip.keyframeUrls.length})
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            {clip.keyframeUrls.map((url, idx) => {
              // Match keyframe URL to detection data by index
              // keyframeUrls and keyframeDetections are ordered the same way
              const kd = clip.keyframeDetections?.[idx];
              const hasDetections = kd && kd.detections.length > 0;

              return (
                <div key={idx} className="bg-white rounded-lg border border-gray-200 p-2">
                  <div className="relative">
                    <img
                      src={url}
                      alt={`Keyframe ${idx + 1}`}
                      className="w-full rounded-lg"
                    />
                    {hasDetections && kd.detections.map((d, di) => (
                      <BoundingBoxOverlay
                        key={di}
                        detection={{
                          detectionId: `${idx}-${di}`,
                          clipId: clip.clipId,
                          timestamp: clip.createdAt,
                          confidence: d.confidence,
                          label: d.label,
                          boundingBox: d.boundingBox,
                          isTargetDog: d.label === "my_dog",
                        }}
                      />
                    ))}
                  </div>
                  {kd && (
                    <div className="mt-1 flex items-center gap-2 text-xs">
                      <span
                        className={`px-2 py-0.5 rounded-full ${
                          kd.label === "my_dog"
                            ? "bg-amber-100 text-amber-700"
                            : kd.label === "other_dog"
                              ? "bg-blue-100 text-blue-700"
                              : "bg-gray-100 text-gray-600"
                        }`}
                      >
                        {kd.label}
                      </span>
                      {kd.detections.length > 0 && (
                        <span className="text-gray-400">
                          {kd.detections.length} detection{kd.detections.length !== 1 ? "s" : ""}
                        </span>
                      )}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
