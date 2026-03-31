import { useEffect, useRef, useState } from "react";
import { Dog, Ban, CheckCircle, Loader2, Play, ChevronRight, Upload } from "lucide-react";
import { api } from "../api";

type Filter = "all" | "dog" | "no_dog" | "unreviewed";

interface LabelItem {
  keyframe_key: string;
  clip_id?: string;
  auto_label: string;
  confirmed_label?: string;
  confidence?: string;
  bounding_boxes?: string;
  reviewed?: string;
  imageUrl?: string;
}

function LabelBadge({ label, type }: { label: string; type: "auto" | "confirmed" }) {
  const isDog = label === "dog" || label === "my_dog";
  const color = type === "confirmed"
    ? isDog ? "bg-green-100 text-green-800" : "bg-gray-100 text-gray-700"
    : isDog ? "bg-amber-50 text-amber-700" : "bg-gray-50 text-gray-500";
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${color}`}>
      {isDog ? <Dog className="w-3 h-3" /> : <Ban className="w-3 h-3" />}
      {label}
    </span>
  );
}

export default function Labels() {
  const [stats, setStats] = useState<{ total: number; dogs: number; noDogs: number; reviewed: number; unreviewed: number } | null>(null);
  const [labels, setLabels] = useState<LabelItem[]>([]);
  const [filter, setFilter] = useState<Filter>("unreviewed");
  const [loading, setLoading] = useState(true);
  const [labelling, setLabelling] = useState(false);
  const [nextPageKey, setNextPageKey] = useState<string | null>(null);
  const [updating, setUpdating] = useState<Set<string>>(new Set());
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState("");
  const fileInputRef = useRef<HTMLInputElement>(null);

  const loadStats = () => api.getLabelStats().then(setStats).catch(console.error);

  const loadLabels = (pageKey?: string) => {
    setLoading(true);
    const params: { reviewed?: string; label?: string; limit?: number; nextPageKey?: string } = { limit: 30 };
    if (filter === "unreviewed") params.reviewed = "false";
    else if (filter === "dog") params.label = "dog";
    else if (filter === "no_dog") params.label = "no_dog";
    if (pageKey) params.nextPageKey = pageKey;

    api.getLabels(params)
      .then((data) => {
        const items = data.labels.map((l) => l as unknown as LabelItem);
        setLabels(pageKey ? (prev) => [...prev, ...items] : items);
        setNextPageKey(data.nextPageKey);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadStats();
    loadLabels();
  }, [filter]);

  const handleAutoLabel = async () => {
    setLabelling(true);
    try {
      await api.triggerAutoLabel();
    } catch (e) {
      console.error("Auto-label failed:", e);
    }
    setLabelling(false);
  };

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files || []);
    if (files.length === 0) return;

    setUploading(true);
    setUploadProgress(`Uploading ${files.length} photo${files.length > 1 ? "s" : ""}...`);
    try {
      const result = await api.uploadTrainingImages(files);
      setUploadProgress(`Uploaded ${result.uploaded} photo${result.uploaded !== 1 ? "s" : ""}${result.errors.length > 0 ? ` (${result.errors.length} failed)` : ""}`);
      loadStats();
      loadLabels();
      setTimeout(() => setUploadProgress(""), 5000);
    } catch (err) {
      setUploadProgress(`Upload failed: ${(err as Error).message}`);
      setTimeout(() => setUploadProgress(""), 5000);
    }
    setUploading(false);
    if (fileInputRef.current) fileInputRef.current.value = "";
  };

  const handleConfirm = async (keyframeKey: string, confirmedLabel: string) => {
    setUpdating((prev) => new Set(prev).add(keyframeKey));
    try {
      await api.updateLabel(keyframeKey, confirmedLabel);
      setLabels((prev) =>
        prev.map((l) =>
          l.keyframe_key === keyframeKey
            ? { ...l, confirmed_label: confirmedLabel, auto_label: confirmedLabel === "my_dog" ? "dog" : "no_dog", reviewed: "true" }
            : l
        )
      );
      loadStats();
    } catch (e) {
      console.error("Update failed:", e);
    }
    setUpdating((prev) => {
      const next = new Set(prev);
      next.delete(keyframeKey);
      return next;
    });
  };

  const handleBulkConfirmNoDog = async () => {
    const keys = labels
      .filter((l) => l.reviewed !== "true" && l.auto_label === "no_dog")
      .map((l) => l.keyframe_key);
    if (keys.length === 0) return;
    if (!window.confirm(`Confirm ${keys.length} keyframes as "no_dog"?`)) return;

    setLoading(true);
    try {
      await api.bulkConfirmLabels(keys, "no_dog");
      loadLabels();
      loadStats();
    } catch (e) {
      console.error("Bulk confirm failed:", e);
    }
  };

  const filters: { key: Filter; label: string }[] = [
    { key: "unreviewed", label: "Unreviewed" },
    { key: "dog", label: "Dogs" },
    { key: "no_dog", label: "No Dog" },
    { key: "all", label: "All" },
  ];

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Labels</h1>
        <div className="flex items-center gap-2">
          <input
            ref={fileInputRef}
            type="file"
            accept="image/jpeg,image/png"
            multiple
            onChange={handleUpload}
            className="hidden"
          />
          <button
            onClick={() => fileInputRef.current?.click()}
            disabled={uploading}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-lg disabled:opacity-50"
          >
            {uploading ? <Loader2 className="w-4 h-4 animate-spin" /> : <Upload className="w-4 h-4" />}
            {uploading ? "Uploading..." : "Upload Photos"}
          </button>
          <button
            onClick={handleAutoLabel}
            disabled={labelling}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
          >
            {labelling ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
            {labelling ? "Running..." : "Run Auto-Label"}
          </button>
        </div>
      </div>

      {uploadProgress && (
        <div className="mb-4 p-3 bg-green-50 text-green-700 rounded-lg text-sm">
          {uploadProgress}
        </div>
      )}

      {/* Stats */}
      {stats && (
        <div className="grid grid-cols-5 gap-3 mb-6">
          {[
            { label: "Total", value: stats.total },
            { label: "Dogs", value: stats.dogs },
            { label: "No Dog", value: stats.noDogs },
            { label: "Reviewed", value: stats.reviewed },
            { label: "Unreviewed", value: stats.unreviewed },
          ].map(({ label, value }) => (
            <div key={label} className="bg-white rounded-lg border border-gray-200 p-3 text-center">
              <p className="text-2xl font-bold text-gray-900">{value}</p>
              <p className="text-xs text-gray-500">{label}</p>
            </div>
          ))}
        </div>
      )}

      {/* Filter tabs */}
      <div className="flex items-center gap-2 mb-4">
        {filters.map(({ key, label }) => (
          <button
            key={key}
            onClick={() => { setFilter(key); setLabels([]); setNextPageKey(null); }}
            className={`px-3 py-1.5 text-xs font-medium rounded-lg ${
              filter === key
                ? "bg-blue-600 text-white"
                : "bg-gray-100 text-gray-600 hover:bg-gray-200"
            }`}
          >
            {label}
          </button>
        ))}
        {filter === "no_dog" && (
          <button
            onClick={handleBulkConfirmNoDog}
            className="ml-auto px-3 py-1.5 text-xs font-medium text-amber-700 bg-amber-50 hover:bg-amber-100 rounded-lg"
          >
            Confirm all as No Dog
          </button>
        )}
      </div>

      {/* Grid */}
      {loading && labels.length === 0 ? (
        <div className="flex items-center gap-2 text-gray-400 justify-center py-12">
          <Loader2 className="w-4 h-4 animate-spin" />
          Loading...
        </div>
      ) : labels.length === 0 ? (
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <p className="text-gray-500">No keyframes found for this filter</p>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
            {labels.map((item) => {
              const isUpdating = updating.has(item.keyframe_key);
              const isReviewed = item.reviewed === "true";
              return (
                <div
                  key={item.keyframe_key}
                  className={`bg-white rounded-lg border overflow-hidden ${
                    isReviewed ? "border-green-200" : "border-gray-200"
                  }`}
                >
                  <div className="aspect-video bg-gray-100 relative">
                    {item.imageUrl && (
                      <img src={item.imageUrl} alt="" className="w-full h-full object-cover" loading="lazy" />
                    )}
                    <div className="absolute top-1 left-1">
                      <LabelBadge
                        label={item.confirmed_label || item.auto_label}
                        type={item.confirmed_label ? "confirmed" : "auto"}
                      />
                    </div>
                    {item.confidence && parseFloat(item.confidence) > 0 && (
                      <span className="absolute top-1 right-1 px-1.5 py-0.5 bg-black bg-opacity-60 text-white text-xs rounded">
                        {(parseFloat(item.confidence) * 100).toFixed(0)}%
                      </span>
                    )}
                  </div>
                  {!isReviewed && (
                    <div className="p-2 flex items-center gap-1">
                      <button
                        onClick={() => handleConfirm(item.keyframe_key, "my_dog")}
                        disabled={isUpdating}
                        className="flex-1 inline-flex items-center justify-center gap-1 px-2 py-1.5 text-xs font-medium text-green-700 bg-green-50 hover:bg-green-100 rounded disabled:opacity-50"
                      >
                        <Dog className="w-3 h-3" /> My Dog
                      </button>
                      <button
                        onClick={() => handleConfirm(item.keyframe_key, "no_dog")}
                        disabled={isUpdating}
                        className="flex-1 inline-flex items-center justify-center gap-1 px-2 py-1.5 text-xs font-medium text-gray-600 bg-gray-50 hover:bg-gray-100 rounded disabled:opacity-50"
                      >
                        <Ban className="w-3 h-3" /> No Dog
                      </button>
                    </div>
                  )}
                  {isReviewed && (
                    <div className="p-2 flex items-center justify-center gap-1 text-xs text-green-600">
                      <CheckCircle className="w-3 h-3" /> Reviewed
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          {nextPageKey && (
            <div className="mt-4 text-center">
              <button
                onClick={() => loadLabels(nextPageKey)}
                disabled={loading}
                className="inline-flex items-center gap-1 px-4 py-2 text-sm font-medium text-blue-600 hover:bg-blue-50 rounded-lg"
              >
                Load More <ChevronRight className="w-4 h-4" />
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
