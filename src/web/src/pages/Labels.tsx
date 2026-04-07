import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { Dog, Ban, CheckCircle, Loader2, Play, ChevronRight, Upload, SlidersHorizontal, Crosshair, ExternalLink } from "lucide-react";
import { api } from "../api";
import { DOG_BREEDS } from "../constants";
import { LabelBadge } from "../components/LabelBadge";

type Filter = "all" | "dog" | "no_dog" | "unreviewed" | "confirmed_my_dog" | "confirmed_other_dog" | "confirmed_no_dog";

interface LabelItem {
  keyframe_key: string;
  clip_id?: string;
  auto_label: string;
  confirmed_label?: string;
  confidence?: string;
  bounding_boxes?: string;
  reviewed?: string;
  imageUrl?: string;
  breed?: string;
}

export default function Labels() {
  const [searchParams, setSearchParams] = useSearchParams();

  const filter = (searchParams.get("filter") as Filter) ?? "unreviewed";
  const breedFilter = searchParams.get("breed") ?? "";
  const deviceFilter = searchParams.get("device") ?? "";
  const confidenceMin = Number(searchParams.get("confidence") ?? "0");
  const sortOrder = (searchParams.get("sort") as "asc" | "desc" | "none") ?? "none";
  const boxFilter = (searchParams.get("boxes") as "all" | "has_boxes" | "no_boxes") ?? "all";

  const setParam = (key: string, value: string, defaultValue: string) => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (value === defaultValue) next.delete(key); else next.set(key, value);
      return next;
    }, { replace: true });
  };

  const setBreedFilter = (v: string) => setParam("breed", v, "");
  const setDeviceFilter = (v: string) => setParam("device", v, "");
  const setConfidenceMin = (v: number) => setParam("confidence", String(v), "0");
  const setSortOrder = (v: "asc" | "desc" | "none") => setParam("sort", v, "none");
  const setBoxFilter = (v: "all" | "has_boxes" | "no_boxes") => setParam("boxes", v, "all");

  // Switch filter tab — resets sub-filters in one setSearchParams call to avoid batching issues
  const switchFilter = (v: Filter) => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (v === "unreviewed") next.delete("filter"); else next.set("filter", v);
      next.delete("breed");
      next.delete("confidence");
      next.delete("sort");
      return next;
    }, { replace: true });
  };

  const [stats, setStats] = useState<{ total: number; dogs: number; noDogs: number; reviewed: number; unreviewed: number; myDog: number; otherDog: number; confirmedNoDog: number; myDogWithBoxes: number; myDogWithoutBoxes: number; otherDogWithBoxes: number; otherDogWithoutBoxes: number; breeds: Record<string, number> } | null>(null);
  const [labels, setLabels] = useState<LabelItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [labelling, setLabelling] = useState(false);
  const [nextPageKey, setNextPageKey] = useState<string | null>(null);
  const [updating, setUpdating] = useState<Set<string>>(new Set());
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState("");
  const [backfillingBoxes, setBackfillingBoxes] = useState(false);
  const [backfillResult, setBackfillResult] = useState("");
  const [showUploadPicker, setShowUploadPicker] = useState(false);
  const [uploadLabel, setUploadLabel] = useState<"my_dog" | "other_dog" | "no_dog">("my_dog");
  const [uploadBreed, setUploadBreed] = useState("");
  const [uploadStep, setUploadStep] = useState<"label" | "breed">("label");
  const fileInputRef = useRef<HTMLInputElement>(null);

  // Natural image dimensions for bounding box scaling
  const [imageDims, setImageDims] = useState<Record<string, { w: number; h: number }>>({});

  // Confidence filter & sort

  // Multi-select
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkActioning, setBulkActioning] = useState(false);
  const [bulkBreed, setBulkBreed] = useState("");

  // Per-item rebox
  const [reboxing, setReboxing] = useState<Set<string>>(new Set());

  // Per-card breed selection (two-step confirm for dog labels)
  const [pendingConfirm, setPendingConfirm] = useState<{ key: string; label: string } | null>(null);
  const [pendingBreed, setPendingBreed] = useState("Unknown");

  // Breed sub-filter for Other Dog tab

  // Device filter
  const [devices, setDevices] = useState<string[]>([]);

  // Bounding box filter

  const hasBoxes = (item: LabelItem) => {
    const b = item.bounding_boxes;
    return !!b && b !== "[]";
  };

  const filteredLabels = useMemo(() => {
    let items = labels.filter((item) => {
      const conf = item.confidence ? parseFloat(item.confidence) : 0;
      if (conf < confidenceMin) return false;
      if (boxFilter === "has_boxes") return hasBoxes(item);
      if (boxFilter === "no_boxes") return !hasBoxes(item);
      return true;
    });
    if (sortOrder === "asc") {
      items = [...items].sort((a, b) => parseFloat(a.confidence || "0") - parseFloat(b.confidence || "0"));
    } else if (sortOrder === "desc") {
      items = [...items].sort((a, b) => parseFloat(b.confidence || "0") - parseFloat(a.confidence || "0"));
    }
    return items;
  }, [labels, confidenceMin, sortOrder, boxFilter]);

  const loadStats = () => api.getLabelStats().then(setStats).catch(console.error);

  const loadLabels = (pageKey?: string) => {
    setLoading(true);
    const params: { reviewed?: string; label?: string; confirmedLabel?: string; breed?: string; device?: string; limit?: number; nextPageKey?: string } = { limit: 30 };
    if (filter === "unreviewed") params.reviewed = "false";
    else if (filter === "dog") params.label = "dog";
    else if (filter === "no_dog") params.label = "no_dog";
    else if (filter === "confirmed_my_dog") params.confirmedLabel = "my_dog";
    else if (filter === "confirmed_other_dog") params.confirmedLabel = "other_dog";
    else if (filter === "confirmed_no_dog") params.confirmedLabel = "no_dog";
    if (breedFilter) params.breed = breedFilter;
    if (deviceFilter) params.device = deviceFilter;
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
    api.getDevices().then((data) => setDevices(data.devices.map((d: any) => d.thingName))).catch(console.error);
  }, []);

  useEffect(() => {
    loadStats();
    loadLabels();
  }, [filter, breedFilter, deviceFilter]);

  const handleAutoLabel = async () => {
    setLabelling(true);
    try {
      await api.triggerAutoLabel();
    } catch (e) {
      console.error("Auto-label failed:", e);
    }
    setLabelling(false);
  };

  const handleBackfillBoxes = async () => {
    setBackfillingBoxes(true);
    setBackfillResult("");
    try {
      const result = await api.backfillBoundingBoxes();
      if (result.total === 0) {
        setBackfillResult("No labels found with missing bounding boxes.");
      } else {
        setBackfillResult(`Backfilling ${result.total} labels across ${result.batches} batch${result.batches !== 1 ? "es" : ""} — running in background.`);
      }
      setTimeout(() => setBackfillResult(""), 8000);
    } catch (e) {
      console.error("Backfill failed:", e);
      setBackfillResult("Backfill failed — check console.");
      setTimeout(() => setBackfillResult(""), 5000);
    }
    setBackfillingBoxes(false);
  };

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files || []);
    if (files.length === 0) return;

    setUploading(true);
    let uploaded = 0;
    let failed = 0;

    for (let i = 0; i < files.length; i++) {
      setUploadProgress(`Uploading ${i + 1} of ${files.length}...`);
      try {
        await api.uploadTrainingImage(files[i], uploadLabel, uploadBreed || undefined);
        uploaded++;
      } catch {
        failed++;
      }
    }

    setUploadProgress(`Uploaded ${uploaded} photo${uploaded !== 1 ? "s" : ""}${failed > 0 ? ` (${failed} failed)` : ""}`);
    loadStats();
    loadLabels();
    setTimeout(() => setUploadProgress(""), 5000);
    setUploading(false);
    if (fileInputRef.current) fileInputRef.current.value = "";
  };

  const handleConfirm = async (keyframeKey: string, confirmedLabel: string, breed?: string) => {
    setUpdating((prev) => new Set(prev).add(keyframeKey));
    try {
      await api.updateLabel(keyframeKey, confirmedLabel, breed);
      setLabels((prev) =>
        prev.map((l) =>
          l.keyframe_key === keyframeKey
            ? { ...l, confirmed_label: confirmedLabel, auto_label: confirmedLabel === "my_dog" || confirmedLabel === "other_dog" ? "dog" : "no_dog", reviewed: "true", breed: breed || l.breed }
            : l
        )
      );
      setPendingConfirm(null);
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

  const handleBulkConfirmSelected = async (label: "my_dog" | "other_dog" | "no_dog") => {
    const keys = Array.from(selected);
    if (keys.length === 0) return;
    if (label !== "no_dog" && !bulkBreed) {
      window.alert("Please select a breed before confirming dog labels.");
      return;
    }
    if (!window.confirm(`Confirm ${keys.length} item${keys.length > 1 ? "s" : ""} as "${label}"${bulkBreed && label !== "no_dog" ? ` (${bulkBreed})` : ""}?`)) return;

    setBulkActioning(true);
    try {
      await api.bulkConfirmLabels(keys, label, label !== "no_dog" ? bulkBreed : undefined);
      setSelected(new Set());
      loadLabels();
      loadStats();
    } catch (e) {
      console.error("Bulk confirm failed:", e);
    }
    setBulkActioning(false);
  };

  const handleReboxSingle = async (keyframeKey: string) => {
    setReboxing((prev) => new Set(prev).add(keyframeKey));
    try {
      await api.backfillBoundingBoxes(undefined, [keyframeKey]);
    } catch (e) {
      console.error("Rebox failed:", e);
    }
    setReboxing((prev) => { const next = new Set(prev); next.delete(keyframeKey); return next; });
  };

  const handleReboxSelected = async () => {
    const keys = Array.from(selected);
    if (keys.length === 0) return;
    setBulkActioning(true);
    try {
      const result = await api.backfillBoundingBoxes(undefined, keys);
      setBackfillResult(`Queued ${result.total} label${result.total !== 1 ? "s" : ""} for re-boxing.`);
      setTimeout(() => setBackfillResult(""), 6000);
    } catch (e) {
      console.error("Rebox selected failed:", e);
    }
    setBulkActioning(false);
  };

  const filters: { key: Filter; label: string }[] = [
    { key: "unreviewed", label: "Unreviewed" },
    { key: "dog", label: "Dogs" },
    { key: "no_dog", label: "No Dog" },
    { key: "confirmed_my_dog", label: "My Dog" },
    { key: "confirmed_other_dog", label: "Other Dog" },
    { key: "confirmed_no_dog", label: "Confirmed No Dog" },
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
          <div className="relative">
            <button
              onClick={() => setShowUploadPicker((v) => !v)}
              disabled={uploading}
              className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-lg disabled:opacity-50"
            >
              {uploading ? <Loader2 className="w-4 h-4 animate-spin" /> : <Upload className="w-4 h-4" />}
              {uploading ? "Uploading..." : "Upload Photos"}
            </button>
            {showUploadPicker && (
              <div className="absolute right-0 mt-1 w-56 bg-white border border-gray-200 rounded-lg shadow-lg z-20 p-2">
                {uploadStep === "label" ? (
                  <>
                    {([
                      { value: "my_dog" as const, label: "My Dog", color: "text-green-700 hover:bg-green-50" },
                      { value: "other_dog" as const, label: "Other Dog", color: "text-orange-700 hover:bg-orange-50" },
                      { value: "no_dog" as const, label: "No Dog", color: "text-gray-700 hover:bg-gray-50" },
                    ]).map(({ value, label, color }) => (
                      <button
                        key={value}
                        onClick={() => {
                          setUploadLabel(value);
                          if (value === "no_dog") {
                            setUploadBreed("");
                            setShowUploadPicker(false);
                            setUploadStep("label");
                            fileInputRef.current?.click();
                          } else {
                            setUploadStep("breed");
                          }
                        }}
                        className={`w-full text-left px-3 py-2 text-sm font-medium rounded ${color}`}
                      >
                        {label}
                      </button>
                    ))}
                  </>
                ) : (
                  <>
                    <p className="text-xs text-gray-500 mb-1 px-1">Select breed for {uploadLabel === "my_dog" ? "My Dog" : "Other Dog"}:</p>
                    <select
                      value={uploadBreed}
                      onChange={(e) => setUploadBreed(e.target.value)}
                      className="w-full text-sm border border-gray-300 rounded px-2 py-1.5 mb-2"
                    >
                      <option value="">-- Select breed --</option>
                      {DOG_BREEDS.map((b) => <option key={b} value={b}>{b}</option>)}
                    </select>
                    <div className="flex gap-1">
                      <button
                        onClick={() => setUploadStep("label")}
                        className="flex-1 px-2 py-1.5 text-xs font-medium text-gray-600 bg-gray-100 hover:bg-gray-200 rounded"
                      >
                        Back
                      </button>
                      <button
                        disabled={!uploadBreed}
                        onClick={() => {
                          setShowUploadPicker(false);
                          setUploadStep("label");
                          fileInputRef.current?.click();
                        }}
                        className="flex-1 px-2 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded disabled:opacity-50"
                      >
                        Choose Files
                      </button>
                    </div>
                  </>
                )}
              </div>
            )}
          </div>
          <button
            onClick={handleAutoLabel}
            disabled={labelling}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
          >
            {labelling ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
            {labelling ? "Running..." : "Auto-Label Keyframes"}
          </button>
          <button
            onClick={handleBackfillBoxes}
            disabled={backfillingBoxes}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-violet-600 hover:bg-violet-700 rounded-lg disabled:opacity-50"
          >
            {backfillingBoxes ? <Loader2 className="w-4 h-4 animate-spin" /> : <Crosshair className="w-4 h-4" />}
            {backfillingBoxes ? "Queuing..." : "Add Missing Bounding Boxes"}
          </button>
        </div>
      </div>

      {uploadProgress && (
        <div className="mb-4 p-3 bg-green-50 text-green-700 rounded-lg text-sm">
          {uploadProgress}
        </div>
      )}

      {backfillResult && (
        <div className="mb-4 p-3 bg-violet-50 text-violet-700 rounded-lg text-sm">
          {backfillResult}
        </div>
      )}

      {/* Stats */}
      {!stats ? (
        // Skeleton loaders
        <div className="animate-pulse">
          <div className="grid grid-cols-4 gap-3 mb-3">
            {Array.from({ length: 4 }).map((_, i) => (
              <div key={i} className="bg-white rounded-lg border border-gray-200 p-3 text-center">
                <div className="h-8 bg-gray-200 rounded w-16 mx-auto mb-2" />
                <div className="h-3 bg-gray-100 rounded w-12 mx-auto" />
              </div>
            ))}
          </div>
          <div className="grid grid-cols-3 gap-3 mb-3">
            {Array.from({ length: 3 }).map((_, i) => (
              <div key={i} className="bg-white rounded-lg border border-gray-200 p-3 text-center">
                <div className="h-8 bg-gray-200 rounded w-16 mx-auto mb-2" />
                <div className="h-3 bg-gray-100 rounded w-24 mx-auto" />
              </div>
            ))}
          </div>
          <div className="grid grid-cols-2 gap-3 mb-6">
            {Array.from({ length: 2 }).map((_, i) => (
              <div key={i} className="bg-white rounded-lg border border-gray-200 p-3">
                <div className="flex items-center justify-between mb-2">
                  <div className="h-3 bg-gray-200 rounded w-36" />
                  <div className="h-3 bg-gray-100 rounded w-16" />
                </div>
                <div className="w-full bg-gray-100 rounded-full h-2 mb-3" />
                <div className="flex justify-between">
                  <div className="h-3 bg-gray-100 rounded w-24" />
                  <div className="h-3 bg-gray-100 rounded w-20" />
                </div>
              </div>
            ))}
          </div>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-4 gap-3 mb-3">
            {[
              { label: "Total", value: stats.total },
              { label: "Dogs", value: stats.dogs },
              { label: "No Dog", value: stats.noDogs },
              { label: "Unreviewed", value: stats.unreviewed },
            ].map(({ label, value }) => (
              <div key={label} className="bg-white rounded-lg border border-gray-200 p-3 text-center">
                <p className="text-2xl font-bold text-gray-900">{value}</p>
                <p className="text-xs text-gray-500">{label}</p>
              </div>
            ))}
          </div>
          <div className="grid grid-cols-3 gap-3 mb-3">
            {[
              { label: "Confirmed My Dog", value: stats.myDog, color: "text-green-600" },
              { label: "Confirmed Other Dog", value: stats.otherDog, color: "text-orange-600" },
              { label: "Confirmed No Dog", value: stats.confirmedNoDog, color: "text-gray-600" },
            ].map(({ label, value, color }) => (
              <div key={label} className="bg-white rounded-lg border border-gray-200 p-3 text-center">
                <p className={`text-2xl font-bold ${color}`}>{value}</p>
                <p className="text-xs text-gray-500">{label}</p>
              </div>
            ))}
          </div>
          {/* Bounding box coverage */}
          <div className="grid grid-cols-2 gap-3 mb-6">
            {[
              { label: "My Dog", withBoxes: stats.myDogWithBoxes, withoutBoxes: stats.myDogWithoutBoxes, color: "text-green-600" },
              { label: "Other Dog", withBoxes: stats.otherDogWithBoxes, withoutBoxes: stats.otherDogWithoutBoxes, color: "text-orange-600" },
            ].map(({ label, withBoxes, withoutBoxes, color }) => {
              const total = withBoxes + withoutBoxes;
              const pct = total > 0 ? Math.round((withBoxes / total) * 100) : 0;
              return (
                <div key={label} className="bg-white rounded-lg border border-gray-200 p-3">
                  <div className="flex items-center justify-between mb-1">
                    <span className={`text-xs font-medium ${color}`}>{label} — Bounding Boxes</span>
                    <span className="text-xs text-gray-500">{pct}% covered</span>
                  </div>
                  <div className="w-full bg-gray-100 rounded-full h-2 mb-2">
                    <div className="bg-violet-500 h-2 rounded-full" style={{ width: `${pct}%` }} />
                  </div>
                  <div className="flex justify-between text-xs text-gray-500">
                    <span><span className="font-medium text-violet-600">{withBoxes.toLocaleString()}</span> with boxes</span>
                    <span><span className="font-medium text-gray-400">{withoutBoxes.toLocaleString()}</span> without</span>
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}

      {/* Filter tabs */}
      <div className="flex items-center gap-2 mb-4">
        {filters.map(({ key, label }) => (
          <button
            key={key}
            onClick={() => { switchFilter(key); setLabels([]); setNextPageKey(null); setSelected(new Set()); setPendingConfirm(null); }}
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

      {/* Secondary filters */}
      <div className="flex items-center gap-4 mb-4 text-sm">
        <label className="flex items-center gap-2 text-gray-600">
          <SlidersHorizontal className="w-4 h-4" />
          <span className="whitespace-nowrap">Min confidence:</span>
          <input
            type="range"
            min={0} max={1} step={0.05}
            value={confidenceMin}
            onChange={(e) => setConfidenceMin(parseFloat(e.target.value))}
            className="w-32"
          />
          <span className="w-10 text-right font-mono text-xs">
            {(confidenceMin * 100).toFixed(0)}%
          </span>
        </label>

        <label className="flex items-center gap-2 text-gray-600">
          <span>Sort:</span>
          <select
            value={sortOrder}
            onChange={(e) => setSortOrder(e.target.value as "asc" | "desc" | "none")}
            className="text-xs border border-gray-300 rounded px-2 py-1"
          >
            <option value="none">Default</option>
            <option value="asc">Confidence ↑ (low first)</option>
            <option value="desc">Confidence ↓ (high first)</option>
          </select>
        </label>

        {(filter === "confirmed_other_dog" || filter === "confirmed_my_dog") && (
          <label className="flex items-center gap-2 text-gray-600">
            <span>Breed:</span>
            <select
              value={breedFilter}
              onChange={(e) => { setBreedFilter(e.target.value); setLabels([]); setNextPageKey(null); }}
              className="text-xs border border-gray-300 rounded px-2 py-1"
            >
              <option value="">All breeds</option>
              {DOG_BREEDS.map((b) => <option key={b} value={b}>{b}</option>)}
            </select>
          </label>
        )}

        {devices.length > 0 && (
          <label className="flex items-center gap-2 text-gray-600">
            <span>Device:</span>
            <select
              value={deviceFilter}
              onChange={(e) => { setDeviceFilter(e.target.value); setLabels([]); setNextPageKey(null); }}
              className="text-xs border border-gray-300 rounded px-2 py-1"
            >
              <option value="">All devices</option>
              {devices.map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
          </label>
        )}

        <label className="flex items-center gap-2 text-gray-600">
          <Crosshair className="w-4 h-4" />
          <span>Boxes:</span>
          <select
            value={boxFilter}
            onChange={(e) => setBoxFilter(e.target.value as "all" | "has_boxes" | "no_boxes")}
            className="text-xs border border-gray-300 rounded px-2 py-1"
          >
            <option value="all">All</option>
            <option value="has_boxes">Has boxes</option>
            <option value="no_boxes">No boxes</option>
          </select>
        </label>

        <span className="text-xs text-gray-400 ml-auto">
          Showing {filteredLabels.length} of {labels.length} loaded
        </span>
      </div>

      {/* Selection toolbar */}
      {selected.size > 0 && (
        <div className="sticky top-0 z-10 flex items-center gap-3 mb-4 p-3 bg-blue-50 border border-blue-200 rounded-lg shadow-sm">
          <span className="text-sm font-medium text-blue-800">
            {selected.size} selected
          </span>
          <button
            onClick={() => {
              const allVisibleUnreviewed = filteredLabels
                .filter((l) => l.reviewed !== "true")
                .map((l) => l.keyframe_key);
              setSelected(new Set(allVisibleUnreviewed));
            }}
            className="px-2 py-1 text-xs font-medium text-blue-700 hover:bg-blue-100 rounded"
          >
            Select All Visible
          </button>
          <button
            onClick={() => setSelected(new Set())}
            className="px-2 py-1 text-xs font-medium text-blue-700 hover:bg-blue-100 rounded"
          >
            Deselect All
          </button>
          <div className="ml-auto flex items-center gap-2">
            <button
              onClick={handleReboxSelected}
              disabled={bulkActioning}
              className="inline-flex items-center gap-1 px-3 py-1.5 text-xs font-medium text-white bg-violet-600 hover:bg-violet-700 rounded disabled:opacity-50"
            >
              {bulkActioning ? <Loader2 className="w-3 h-3 animate-spin" /> : <Crosshair className="w-3 h-3" />}
              Rebox
            </button>
            <select
              value={bulkBreed}
              onChange={(e) => setBulkBreed(e.target.value)}
              className="text-xs border border-gray-300 rounded px-2 py-1.5"
            >
              <option value="">-- Breed --</option>
              {DOG_BREEDS.map((b) => <option key={b} value={b}>{b}</option>)}
            </select>
            <button
              onClick={() => handleBulkConfirmSelected("my_dog")}
              disabled={bulkActioning}
              className="inline-flex items-center gap-1 px-3 py-1.5 text-xs font-medium text-white bg-green-600 hover:bg-green-700 rounded disabled:opacity-50"
            >
              {bulkActioning ? <Loader2 className="w-3 h-3 animate-spin" /> : <Dog className="w-3 h-3" />}
              My Dog
            </button>
            <button
              onClick={() => handleBulkConfirmSelected("other_dog")}
              disabled={bulkActioning}
              className="inline-flex items-center gap-1 px-3 py-1.5 text-xs font-medium text-white bg-orange-600 hover:bg-orange-700 rounded disabled:opacity-50"
            >
              {bulkActioning ? <Loader2 className="w-3 h-3 animate-spin" /> : <Dog className="w-3 h-3" />}
              Other Dog
            </button>
            <button
              onClick={() => handleBulkConfirmSelected("no_dog")}
              disabled={bulkActioning}
              className="inline-flex items-center gap-1 px-3 py-1.5 text-xs font-medium text-white bg-gray-600 hover:bg-gray-700 rounded disabled:opacity-50"
            >
              {bulkActioning ? <Loader2 className="w-3 h-3 animate-spin" /> : <Ban className="w-3 h-3" />}
              No Dog
            </button>
          </div>
        </div>
      )}

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
      ) : filteredLabels.length === 0 ? (
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <p className="text-gray-500">No items match the current confidence filter</p>
          <button onClick={() => setConfidenceMin(0)} className="mt-2 text-sm text-blue-600 hover:underline">
            Reset filter
          </button>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
            {filteredLabels.map((item) => {
              const isUpdating = updating.has(item.keyframe_key);
              const isReviewed = item.reviewed === "true";
              const isSelected = selected.has(item.keyframe_key);
              return (
                <div
                  key={item.keyframe_key}
                  onClick={() => {
                    if (isReviewed) return;
                    setSelected((prev) => {
                      const next = new Set(prev);
                      if (next.has(item.keyframe_key)) next.delete(item.keyframe_key);
                      else next.add(item.keyframe_key);
                      return next;
                    });
                  }}
                  className={`bg-white rounded-lg border overflow-hidden ${
                    !isReviewed ? "cursor-pointer" : ""
                  } ${
                    isSelected
                      ? "border-blue-400 ring-2 ring-blue-200"
                      : isReviewed ? "border-green-200" : "border-gray-200"
                  }`}
                >
                  <div className="aspect-video bg-gray-100 relative">
                    {item.imageUrl && (
                      <img
                        src={item.imageUrl}
                        alt=""
                        className="w-full h-full object-cover"
                        loading="lazy"
                        onLoad={(e) => {
                          const img = e.target as HTMLImageElement;
                          setImageDims((prev) => ({
                            ...prev,
                            [item.keyframe_key]: { w: img.naturalWidth, h: img.naturalHeight },
                          }));
                        }}
                      />
                    )}
                    {/* Bounding box overlay */}
                    {hasBoxes(item) && imageDims[item.keyframe_key] && (() => {
                      const { w, h } = imageDims[item.keyframe_key];
                      const boxes: number[][] = JSON.parse(item.bounding_boxes!);
                      return (
                        <svg
                          className="absolute inset-0 w-full h-full pointer-events-none"
                          viewBox={`0 0 ${w} ${h}`}
                          preserveAspectRatio="xMidYMid slice"
                        >
                          {boxes.map((box, i) => (
                            <rect
                              key={i}
                              x={box[0]} y={box[1]}
                              width={box[2]} height={box[3]}
                              fill="none"
                              stroke="#7c3aed"
                              strokeWidth={Math.max(w, h) / 200}
                              strokeDasharray="none"
                            />
                          ))}
                        </svg>
                      );
                    })()}
                    <div className="absolute top-1 left-1">
                      <LabelBadge
                        label={item.confirmed_label || item.auto_label}
                        type={item.confirmed_label ? "confirmed" : "auto"}
                      />
                    </div>
                    {isSelected && (
                      <div className="absolute top-1 right-8">
                        <CheckCircle className="w-5 h-5 text-blue-600 drop-shadow" />
                      </div>
                    )}
                    {item.confidence && parseFloat(item.confidence) > 0 && (
                      <span className="absolute top-1 right-1 px-1.5 py-0.5 bg-black bg-opacity-60 text-white text-xs rounded">
                        {(parseFloat(item.confidence) * 100).toFixed(0)}%
                      </span>
                    )}
                    <Link
                      to={`/labels/${encodeURIComponent(item.keyframe_key)}`}
                      onClick={(e) => e.stopPropagation()}
                      title="Open detail"
                      className="absolute bottom-1 left-1 p-0.5 bg-black bg-opacity-40 rounded hover:bg-opacity-70"
                    >
                      <ExternalLink className="w-3 h-3 text-white" />
                    </Link>
                  </div>
                  {!isReviewed && pendingConfirm?.key !== item.keyframe_key && (
                    <div className="p-2 flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
                      <button
                        onClick={() => { setPendingConfirm({ key: item.keyframe_key, label: "my_dog" }); setPendingBreed("Labrador Retriever"); }}
                        disabled={isUpdating}
                        className="flex-1 inline-flex items-center justify-center gap-1 px-2 py-1.5 text-xs font-medium text-green-700 bg-green-50 hover:bg-green-100 rounded disabled:opacity-50"
                      >
                        <Dog className="w-3 h-3" /> My Dog
                      </button>
                      <button
                        onClick={() => { setPendingConfirm({ key: item.keyframe_key, label: "other_dog" }); setPendingBreed("Unknown"); }}
                        disabled={isUpdating}
                        className="flex-1 inline-flex items-center justify-center gap-1 px-2 py-1.5 text-xs font-medium text-orange-700 bg-orange-50 hover:bg-orange-100 rounded disabled:opacity-50"
                      >
                        <Dog className="w-3 h-3" /> Other Dog
                      </button>
                      <button
                        onClick={() => handleConfirm(item.keyframe_key, "no_dog")}
                        disabled={isUpdating}
                        className="flex-1 inline-flex items-center justify-center gap-1 px-2 py-1.5 text-xs font-medium text-gray-600 bg-gray-50 hover:bg-gray-100 rounded disabled:opacity-50"
                      >
                        <Ban className="w-3 h-3" /> No Dog
                      </button>
                      <button
                        onClick={() => handleReboxSingle(item.keyframe_key)}
                        disabled={reboxing.has(item.keyframe_key)}
                        title="Re-run bounding box detection"
                        className="p-1.5 text-violet-500 hover:text-violet-700 hover:bg-violet-50 rounded disabled:opacity-50"
                      >
                        {reboxing.has(item.keyframe_key) ? <Loader2 className="w-3 h-3 animate-spin" /> : <Crosshair className="w-3 h-3" />}
                      </button>
                    </div>
                  )}
                  {!isReviewed && pendingConfirm?.key === item.keyframe_key && (
                    <div className="p-2 space-y-1" onClick={(e) => e.stopPropagation()}>
                      <p className="text-xs text-gray-500">
                        {pendingConfirm.label === "my_dog" ? "My Dog" : "Other Dog"} — select breed:
                      </p>
                      <select
                        value={pendingBreed}
                        onChange={(e) => setPendingBreed(e.target.value)}
                        className="w-full text-xs border border-gray-300 rounded px-2 py-1"
                      >
                        {DOG_BREEDS.map((b) => <option key={b} value={b}>{b}</option>)}
                      </select>
                      <div className="flex gap-1">
                        <button
                          onClick={() => setPendingConfirm(null)}
                          className="flex-1 px-2 py-1.5 text-xs font-medium text-gray-600 bg-gray-100 hover:bg-gray-200 rounded"
                        >
                          Cancel
                        </button>
                        <button
                          onClick={() => handleConfirm(item.keyframe_key, pendingConfirm.label, pendingBreed)}
                          disabled={isUpdating}
                          className="flex-1 px-2 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded disabled:opacity-50"
                        >
                          Confirm
                        </button>
                      </div>
                    </div>
                  )}
                  {isReviewed && (
                    <div className="p-2 flex items-center gap-1 text-xs text-green-600">
                      <CheckCircle className="w-3 h-3" /> Reviewed
                      {item.breed && <span className="text-gray-500 ml-1">({item.breed})</span>}
                      {(item.confirmed_label === "my_dog" || item.confirmed_label === "other_dog") && (
                        <button
                          onClick={(e) => { e.stopPropagation(); handleReboxSingle(item.keyframe_key); }}
                          disabled={reboxing.has(item.keyframe_key)}
                          title="Re-run bounding box detection"
                          className="ml-auto p-1 text-violet-500 hover:text-violet-700 hover:bg-violet-50 rounded disabled:opacity-50"
                        >
                          {reboxing.has(item.keyframe_key) ? <Loader2 className="w-3 h-3 animate-spin" /> : <Crosshair className="w-3 h-3" />}
                        </button>
                      )}
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
