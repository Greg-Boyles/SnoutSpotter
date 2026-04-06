import { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft, Loader2, Play, Tag, ExternalLink, CheckCircle, Clock } from "lucide-react";
import { api } from "../api";
import type { Clip } from "../types";

type OverlayMode = "inference" | "labels";

interface LabelRecord {
  auto_label?: string | null;
  confirmed_label?: string | null;
  breed?: string | null;
  reviewed?: string | null;
  bounding_boxes?: string | null;
  confidence?: string | null;
}

function DetectionBadge({ label, type }: { label: string; type: "inference" | "auto" | "confirmed" }) {
  const colors =
    label === "my_dog" ? "bg-amber-100 text-amber-700" :
    label === "other_dog" ? "bg-blue-100 text-blue-700" :
    label === "dog" ? "bg-amber-50 text-amber-600" :
    "bg-gray-100 text-gray-600";
  const prefix = type === "inference" ? "Detected" : type === "confirmed" ? "Confirmed" : "Auto";
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${colors}`}>
      {prefix}: {label}
    </span>
  );
}

export default function ClipDetail() {
  const { id } = useParams<{ id: string }>();
  const [clip, setClip] = useState<Clip | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [inferring, setInferring] = useState(false);
  const [autoLabelling, setAutoLabelling] = useState(false);

  // Per-keyframe label data
  const [labels, setLabels] = useState<Record<string, LabelRecord>>({});
  // Per-keyframe overlay toggle
  const [overlayMode, setOverlayMode] = useState<Record<string, OverlayMode>>({});
  // Natural image dimensions for accurate box scaling
  const [imageDims, setImageDims] = useState<Record<string, { w: number; h: number }>>({});

  useEffect(() => {
    if (!id) return;
    api.getClip(id).then(setClip).catch((e: Error) => setError(e.message));
  }, [id]);

  // Fetch labels for all keyframes once clip loads
  useEffect(() => {
    if (!clip?.keyframeKeys?.length) return;
    Promise.allSettled(
      clip.keyframeKeys.map((key) =>
        api.getLabel(key).then((data) => ({ key, data: data as LabelRecord }))
      )
    ).then((results) => {
      const map: Record<string, LabelRecord> = {};
      for (const r of results) {
        if (r.status === "fulfilled") map[r.value.key] = r.value.data;
      }
      setLabels(map);
    });
  }, [clip?.keyframeKeys]);

  const handleRunInference = async () => {
    if (!id) return;
    setInferring(true);
    try { await api.runInference(id); } catch (e) { console.error(e); }
    setInferring(false);
  };

  const handleAutoLabel = async () => {
    if (!clip) return;
    setAutoLabelling(true);
    try { await api.triggerAutoLabel(clip.date); } catch (e) { console.error(e); }
    setAutoLabelling(false);
  };

  if (error) return <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>;
  if (!clip) return <div className="text-gray-400">Loading...</div>;

  // Build detection lookup by keyframe key
  const detectionsByKey = new Map(clip.keyframeDetections?.map((kd) => [kd.keyframeKey, kd]) ?? []);

  return (
    <div>
      <Link to="/clips" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> Back to clips
      </Link>

      <div className="flex flex-wrap items-center gap-2 mb-4">
        <h1 className="text-xl font-bold text-gray-900 mr-auto">Clip {clip.clipId}</h1>
        <button
          onClick={handleRunInference}
          disabled={inferring}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
        >
          {inferring ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
          {inferring ? "Running..." : "Run Inference"}
        </button>
        <button
          onClick={handleAutoLabel}
          disabled={autoLabelling}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-sm font-medium text-white bg-violet-600 hover:bg-violet-700 rounded-lg disabled:opacity-50"
        >
          {autoLabelling ? <Loader2 className="w-4 h-4 animate-spin" /> : <Tag className="w-4 h-4" />}
          {autoLabelling ? "Labelling..." : "Auto-Label Keyframes"}
        </button>
      </div>

      {/* Video Player */}
      {clip.videoUrl && (
        <div className="mb-6 max-w-2xl">
          <video controls className="w-full rounded-lg border border-gray-200">
            <source src={clip.videoUrl} type="video/mp4" />
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

      {/* Keyframes */}
      {clip.keyframeUrls && clip.keyframeUrls.length > 0 && (
        <div className="mb-6">
          <h2 className="text-sm font-semibold text-gray-700 mb-3">
            Keyframes ({clip.keyframeUrls.length})
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            {clip.keyframeUrls.map((url, idx) => {
              const keyframeKey = clip.keyframeKeys?.[idx];
              const kd = keyframeKey ? detectionsByKey.get(keyframeKey) : clip.keyframeDetections?.[idx];
              const label = keyframeKey ? labels[keyframeKey] : undefined;
              const mode = keyframeKey ? (overlayMode[keyframeKey] ?? "inference") : "inference";
              const dims = keyframeKey ? imageDims[keyframeKey] : undefined;

              const hasInferenceBoxes = kd && kd.detections.length > 0;
              const labelBoxes: number[][] | null = (() => {
                if (!label?.bounding_boxes || label.bounding_boxes === "[]") return null;
                try { return JSON.parse(label.bounding_boxes); } catch { return null; }
              })();
              const hasLabelBoxes = labelBoxes && labelBoxes.length > 0;

              return (
                <div key={idx} className="bg-white rounded-lg border border-gray-200 overflow-hidden">
                  {/* Overlay toggle */}
                  {keyframeKey && (hasInferenceBoxes || hasLabelBoxes) && (
                    <div className="flex items-center gap-1 px-2 pt-2">
                      <button
                        onClick={() => setOverlayMode((p) => ({ ...p, [keyframeKey]: "inference" }))}
                        className={`px-2 py-0.5 text-xs rounded-md font-medium transition-colors ${mode === "inference" ? "bg-amber-100 text-amber-700" : "text-gray-400 hover:text-gray-600"}`}
                      >
                        Inference
                      </button>
                      <button
                        onClick={() => setOverlayMode((p) => ({ ...p, [keyframeKey]: "labels" }))}
                        className={`px-2 py-0.5 text-xs rounded-md font-medium transition-colors ${mode === "labels" ? "bg-violet-100 text-violet-700" : "text-gray-400 hover:text-gray-600"}`}
                      >
                        Labels
                      </button>
                    </div>
                  )}

                  {/* Image + overlay */}
                  <div className="relative p-2">
                    <img
                      src={url}
                      alt={`Keyframe ${idx + 1}`}
                      className="w-full rounded"
                      onLoad={(e) => {
                        if (!keyframeKey) return;
                        const img = e.currentTarget;
                        setImageDims((p) => ({ ...p, [keyframeKey]: { w: img.naturalWidth, h: img.naturalHeight } }));
                      }}
                    />

                    {/* Inference boxes */}
                    {mode === "inference" && hasInferenceBoxes && dims && (
                      <svg
                        className="absolute inset-0 w-full h-full pointer-events-none"
                        viewBox={`0 0 ${dims.w} ${dims.h}`}
                        preserveAspectRatio="xMidYMid meet"
                        style={{ top: "0.5rem", left: "0.5rem", width: "calc(100% - 1rem)", height: "calc(100% - 1rem)" }}
                      >
                        {kd!.detections.map((d, di) => (
                          <g key={di}>
                            <rect
                              x={d.boundingBox.x} y={d.boundingBox.y}
                              width={d.boundingBox.width} height={d.boundingBox.height}
                              fill="none"
                              stroke={d.label === "my_dog" ? "#d97706" : "#6b7280"}
                              strokeWidth={Math.max(dims.w, dims.h) / 300}
                              rx={4}
                            />
                            <rect
                              x={d.boundingBox.x} y={d.boundingBox.y - 20}
                              width={d.boundingBox.width} height={20}
                              fill={d.label === "my_dog" ? "#d97706" : "#6b7280"}
                              rx={4}
                            />
                            <text
                              x={d.boundingBox.x + 4} y={d.boundingBox.y - 5}
                              fill="white" fontSize={12} fontFamily="system-ui"
                            >
                              {d.label} {(d.confidence * 100).toFixed(0)}%
                            </text>
                          </g>
                        ))}
                      </svg>
                    )}

                    {/* Label boxes */}
                    {mode === "labels" && hasLabelBoxes && dims && (
                      <svg
                        className="absolute inset-0 w-full h-full pointer-events-none"
                        viewBox={`0 0 ${dims.w} ${dims.h}`}
                        preserveAspectRatio="xMidYMid meet"
                        style={{ top: "0.5rem", left: "0.5rem", width: "calc(100% - 1rem)", height: "calc(100% - 1rem)" }}
                      >
                        {labelBoxes!.map((box, bi) => (
                          <rect
                            key={bi}
                            x={box[0]} y={box[1]} width={box[2]} height={box[3]}
                            fill="none"
                            stroke="#7c3aed"
                            strokeWidth={Math.max(dims.w, dims.h) / 300}
                          />
                        ))}
                      </svg>
                    )}
                  </div>

                  {/* Label info strip */}
                  <div className="px-2 pb-2 space-y-1.5">
                    {/* Inference result */}
                    {kd && (
                      <div className="flex items-center gap-1.5 flex-wrap">
                        <DetectionBadge label={kd.label} type="inference" />
                        {kd.detections.length > 0 && (
                          <span className="text-xs text-gray-400">
                            {kd.detections.length} box{kd.detections.length !== 1 ? "es" : ""}
                            {kd.detections[0] && ` · ${(kd.detections[0].confidence * 100).toFixed(0)}% conf`}
                          </span>
                        )}
                      </div>
                    )}

                    {/* Label data */}
                    {label && (
                      <div className="flex items-center gap-1.5 flex-wrap border-t border-gray-100 pt-1.5">
                        {label.auto_label && (
                          <DetectionBadge label={label.auto_label} type="auto" />
                        )}
                        {label.confirmed_label && (
                          <DetectionBadge label={label.confirmed_label} type="confirmed" />
                        )}
                        {label.breed && (
                          <span className="text-xs text-gray-500">{label.breed}</span>
                        )}
                        {label.reviewed === "true" ? (
                          <span className="inline-flex items-center gap-0.5 text-xs text-green-600">
                            <CheckCircle className="w-3 h-3" /> Reviewed
                          </span>
                        ) : label.auto_label ? (
                          <span className="inline-flex items-center gap-0.5 text-xs text-amber-500">
                            <Clock className="w-3 h-3" /> Unreviewed
                          </span>
                        ) : (
                          <span className="text-xs text-gray-400">Not labelled</span>
                        )}
                        {keyframeKey && (
                          <Link
                            to={`/labels/${encodeURIComponent(keyframeKey)}`}
                            className="ml-auto inline-flex items-center gap-0.5 text-xs text-blue-600 hover:text-blue-700"
                          >
                            Review <ExternalLink className="w-3 h-3" />
                          </Link>
                        )}
                      </div>
                    )}

                    {/* No label yet — offer link */}
                    {!label && keyframeKey && (
                      <div className="flex items-center border-t border-gray-100 pt-1.5">
                        <span className="text-xs text-gray-400 mr-auto">Not labelled</span>
                        <Link
                          to={`/labels/${encodeURIComponent(keyframeKey)}`}
                          className="inline-flex items-center gap-0.5 text-xs text-blue-600 hover:text-blue-700"
                        >
                          Review <ExternalLink className="w-3 h-3" />
                        </Link>
                      </div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
