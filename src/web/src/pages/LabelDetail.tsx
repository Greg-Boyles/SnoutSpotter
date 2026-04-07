import { useEffect, useRef, useState } from "react";
import { useParams, Link, useNavigate } from "react-router-dom";
import { ArrowLeft, Dog, Ban, CheckCircle, Crosshair, Loader2, ExternalLink, PenLine, RotateCcw, Trash2 } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";
import { DOG_BREEDS } from "../constants";
import { LabelBadge } from "../components/LabelBadge";

function MetaRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex justify-between items-start gap-2 py-2 border-b border-gray-100 last:border-0">
      <span className="text-xs text-gray-500 whitespace-nowrap">{label}</span>
      <span className="text-xs text-gray-900 text-right break-all">{children}</span>
    </div>
  );
}

export default function LabelDetail() {
  const { keyframeKey: encodedKey } = useParams<{ keyframeKey: string }>();
  const navigate = useNavigate();
  const keyframeKey = decodeURIComponent(encodedKey ?? "");

  const [label, setLabel] = useState<Record<string, string | null> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reboxing, setReboxing] = useState(false);
  const [reboxResult, setReboxResult] = useState("");
  const [imageDims, setImageDims] = useState<{ w: number; h: number } | null>(null);
  const [editing, setEditing] = useState(false);
  const [pendingLabel, setPendingLabel] = useState<"my_dog" | "other_dog" | "no_dog" | null>(null);
  const [pendingBreed, setPendingBreed] = useState("Unknown");
  const [confirming, setConfirming] = useState(false);

  // Drawing mode
  const svgRef = useRef<SVGSVGElement>(null);
  const [drawMode, setDrawMode] = useState(false);
  const [drawnBoxes, setDrawnBoxes] = useState<number[][]>([]);
  const [dragStart, setDragStart] = useState<{ x: number; y: number } | null>(null);
  const [dragCurrent, setDragCurrent] = useState<{ x: number; y: number } | null>(null);
  const [submittingBoxes, setSubmittingBoxes] = useState(false);

  const loadLabel = () => {
    setLoading(true);
    api.getLabel(keyframeKey)
      .then((data) => { setLabel(data); setLoading(false); })
      .catch((e: Error) => { setError(e.message); setLoading(false); });
  };

  useEffect(() => {
    if (keyframeKey) loadLabel();
  }, [keyframeKey]);

  const handleRebox = async () => {
    setReboxing(true);
    setReboxResult("");
    try {
      await api.backfillBoundingBoxes(undefined, [keyframeKey]);
      setReboxResult("Queued for re-boxing — refresh in a moment to see updated boxes.");
      setTimeout(() => setReboxResult(""), 8000);
    } catch (e) {
      console.error(e);
      setReboxResult("Rebox failed — check console.");
      setTimeout(() => setReboxResult(""), 5000);
    }
    setReboxing(false);
  };

  const handleConfirm = async () => {
    if (!pendingLabel) return;
    setConfirming(true);
    try {
      const breed = pendingLabel !== "no_dog" ? pendingBreed : undefined;
      await api.updateLabel(keyframeKey, pendingLabel, breed);
      setPendingLabel(null);
      setEditing(false);
      loadLabel();
    } catch (e) {
      console.error(e);
    }
    setConfirming(false);
  };

  const toSvgCoords = (e: React.MouseEvent): { x: number; y: number } => {
    const svg = svgRef.current;
    if (!svg) return { x: 0, y: 0 };
    const pt = svg.createSVGPoint();
    pt.x = e.clientX;
    pt.y = e.clientY;
    const svgPt = pt.matrixTransform(svg.getScreenCTM()!.inverse());
    return { x: svgPt.x, y: svgPt.y };
  };

  const handleSvgMouseDown = (e: React.MouseEvent) => {
    if (!drawMode || !imageDims) return;
    e.preventDefault();
    setDragStart(toSvgCoords(e));
    setDragCurrent(null);
  };

  const handleSvgMouseMove = (e: React.MouseEvent) => {
    if (!drawMode || !dragStart) return;
    setDragCurrent(toSvgCoords(e));
  };

  const handleSvgMouseUp = (e: React.MouseEvent) => {
    if (!drawMode || !dragStart || !imageDims) return;
    const end = toSvgCoords(e);
    const x = Math.max(0, Math.min(dragStart.x, end.x));
    const y = Math.max(0, Math.min(dragStart.y, end.y));
    const w = Math.min(Math.abs(end.x - dragStart.x), imageDims.w - x);
    const h = Math.min(Math.abs(end.y - dragStart.y), imageDims.h - y);
    if (w > 5 && h > 5) {
      setDrawnBoxes((prev) => [...prev, [x, y, w, h]]);
    }
    setDragStart(null);
    setDragCurrent(null);
  };

  const handleSubmitBoxes = async () => {
    if (drawnBoxes.length === 0) return;
    setSubmittingBoxes(true);
    try {
      await api.updateBoundingBoxes(keyframeKey, drawnBoxes);
      setDrawMode(false);
      setDrawnBoxes([]);
      loadLabel();
    } catch (err) {
      console.error("Failed to save boxes:", err);
    }
    setSubmittingBoxes(false);
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20 text-gray-400">
        <Loader2 className="w-5 h-5 animate-spin mr-2" /> Loading label...
      </div>
    );
  }

  if (error || !label) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">
        {error ?? "Label not found"}
      </div>
    );
  }

  const isReviewed = label.reviewed === "true";
  const confirmedLabel = label.confirmed_label;
  const autoLabel = label.auto_label ?? "";
  const confidence = label.confidence ? parseFloat(label.confidence) : null;
  const boundingBoxes: number[][] = (() => {
    try { return JSON.parse(label.bounding_boxes ?? "[]"); } catch { return []; }
  })();
  const hasBoxes = boundingBoxes.length > 0;
  const filename = keyframeKey.split("/").pop() ?? keyframeKey;
  const isDogLabel = confirmedLabel === "my_dog" || confirmedLabel === "other_dog";

  return (
    <div>
      {/* Top bar */}
      <div className="flex items-center gap-3 mb-6">
        <button
          onClick={() => navigate(-1)}
          className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-900"
        >
          <ArrowLeft className="w-4 h-4" /> Back
        </button>
        <span className="text-gray-300">/</span>
        <Link to="/labels" className="text-sm text-gray-500 hover:text-gray-900">Labels</Link>
        <span className="text-gray-300">/</span>
        <span className="text-sm text-gray-900 font-medium truncate max-w-xs">{filename}</span>
      </div>

      <div className="flex gap-6">
        {/* Left: image */}
        <div className="flex-1 min-w-0">
          <div className="bg-black rounded-xl overflow-hidden relative" style={{ aspectRatio: "16/9" }}>
            {label.imageUrl ? (
              <>
                <img
                  src={label.imageUrl}
                  alt=""
                  className="w-full h-full object-contain"
                  onLoad={(e) => {
                    const img = e.target as HTMLImageElement;
                    setImageDims({ w: img.naturalWidth, h: img.naturalHeight });
                  }}
                />
                {imageDims && (
                  <svg
                    ref={svgRef}
                    className="absolute inset-0 w-full h-full"
                    viewBox={`0 0 ${imageDims.w} ${imageDims.h}`}
                    preserveAspectRatio="xMidYMid meet"
                    style={{ cursor: drawMode ? "crosshair" : "default", pointerEvents: drawMode ? "all" : "none" }}
                    onMouseDown={handleSvgMouseDown}
                    onMouseMove={handleSvgMouseMove}
                    onMouseUp={handleSvgMouseUp}
                    onMouseLeave={() => { setDragStart(null); setDragCurrent(null); }}
                  >
                    {/* Saved boxes from DB */}
                    {boundingBoxes.map((box, i) => (
                      <rect
                        key={`saved-${i}`}
                        x={box[0]} y={box[1]}
                        width={box[2]} height={box[3]}
                        fill="none"
                        stroke="#7c3aed"
                        strokeWidth={Math.max(imageDims.w, imageDims.h) / 200}
                      />
                    ))}
                    {/* User-drawn boxes */}
                    {drawnBoxes.map((box, i) => (
                      <rect
                        key={`drawn-${i}`}
                        x={box[0]} y={box[1]}
                        width={box[2]} height={box[3]}
                        fill="rgba(124, 58, 237, 0.1)"
                        stroke="#7c3aed"
                        strokeWidth={Math.max(imageDims.w, imageDims.h) / 200}
                      />
                    ))}
                    {/* Current drag preview */}
                    {dragStart && dragCurrent && (
                      <rect
                        x={Math.min(dragStart.x, dragCurrent.x)}
                        y={Math.min(dragStart.y, dragCurrent.y)}
                        width={Math.abs(dragCurrent.x - dragStart.x)}
                        height={Math.abs(dragCurrent.y - dragStart.y)}
                        fill="rgba(124, 58, 237, 0.15)"
                        stroke="#7c3aed"
                        strokeWidth={Math.max(imageDims.w, imageDims.h) / 200}
                        strokeDasharray="8"
                      />
                    )}
                  </svg>
                )}
              </>
            ) : (
              <div className="w-full h-full flex items-center justify-center text-gray-600">
                No image available
              </div>
            )}
          </div>

          {/* Bounding box controls */}
          {!drawMode ? (
            <div className="mt-3">
              <div className="flex items-center justify-between">
                <span className="text-sm text-gray-500">
                  {hasBoxes
                    ? <span className="text-violet-600 font-medium">{boundingBoxes.length} bounding box{boundingBoxes.length !== 1 ? "es" : ""} detected</span>
                    : <span className="text-gray-400">No bounding boxes detected</span>
                  }
                </span>
                <div className="flex items-center gap-2">
                  {(isDogLabel || autoLabel === "dog") && (
                    <button
                      onClick={() => { setDrawMode(true); setDrawnBoxes([]); }}
                      className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-lg"
                    >
                      <PenLine className="w-4 h-4" /> Draw Boxes
                    </button>
                  )}
                  <button
                    onClick={handleRebox}
                    disabled={reboxing}
                    className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-white bg-violet-600 hover:bg-violet-700 rounded-lg disabled:opacity-50"
                  >
                    {reboxing ? <Loader2 className="w-4 h-4 animate-spin" /> : <Crosshair className="w-4 h-4" />}
                    {reboxing ? "Queuing..." : "Re-box"}
                  </button>
                </div>
              </div>
              {reboxResult && (
                <p className="mt-2 text-sm text-violet-700 bg-violet-50 px-3 py-2 rounded-lg">{reboxResult}</p>
              )}
            </div>
          ) : (
            <div className="mt-3 p-3 bg-green-50 border border-green-200 rounded-lg">
              <p className="text-sm text-green-800 font-medium mb-2">
                <PenLine className="w-4 h-4 inline mr-1" />
                Click and drag on the image to draw bounding boxes
              </p>
              <div className="flex items-center gap-2 flex-wrap">
                {drawnBoxes.length > 0 && (
                  <span className="text-xs font-medium text-violet-700 bg-violet-100 px-2 py-0.5 rounded-full">
                    {drawnBoxes.length} box{drawnBoxes.length !== 1 ? "es" : ""} drawn
                  </span>
                )}
                <button
                  onClick={() => setDrawnBoxes((prev) => prev.slice(0, -1))}
                  disabled={drawnBoxes.length === 0}
                  className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded disabled:opacity-40"
                >
                  <RotateCcw className="w-3 h-3" /> Undo
                </button>
                <button
                  onClick={() => setDrawnBoxes([])}
                  disabled={drawnBoxes.length === 0}
                  className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded disabled:opacity-40"
                >
                  <Trash2 className="w-3 h-3" /> Clear
                </button>
                <button
                  onClick={() => { setDrawMode(false); setDrawnBoxes([]); setDragStart(null); setDragCurrent(null); }}
                  className="px-2 py-1 text-xs font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded"
                >
                  Cancel
                </button>
                <button
                  onClick={handleSubmitBoxes}
                  disabled={drawnBoxes.length === 0 || submittingBoxes}
                  className="inline-flex items-center gap-1 px-3 py-1 text-xs font-medium text-white bg-green-600 hover:bg-green-700 rounded disabled:opacity-50 ml-auto"
                >
                  {submittingBoxes ? <Loader2 className="w-3 h-3 animate-spin" /> : null}
                  {submittingBoxes ? "Saving..." : "Submit Boxes"}
                </button>
              </div>
            </div>
          )}
        </div>

        {/* Right: details panel */}
        <div className="w-72 shrink-0 space-y-4">
          {/* Label status */}
          <div className="bg-white rounded-xl border border-gray-200 p-4">
            <h2 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-3">Label</h2>
            <div className="flex flex-wrap gap-2 mb-3">
              <div>
                <p className="text-xs text-gray-400 mb-1">Auto</p>
                <LabelBadge label={autoLabel} type="auto" />
              </div>
              <div>
                <p className="text-xs text-gray-400 mb-1">Confirmed</p>
                {confirmedLabel
                  ? <LabelBadge label={confirmedLabel} type="confirmed" />
                  : <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-500">Unreviewed</span>
                }
              </div>
            </div>
            {confidence !== null && (
              <p className="text-xs text-gray-500">Confidence: <span className="font-medium text-gray-900">{(confidence * 100).toFixed(1)}%</span></p>
            )}
            {confirmedLabel && label.breed && isDogLabel && (
              <p className="text-xs text-gray-500 mt-1">Breed: <span className="font-medium text-gray-900">{label.breed}</span></p>
            )}
            {isReviewed && (
              <div className="flex items-center gap-1 mt-2 text-xs text-green-600">
                <CheckCircle className="w-3 h-3" /> Reviewed
              </div>
            )}
          </div>

          {/* Actions */}
          <div className="bg-white rounded-xl border border-gray-200 p-4">
            <h2 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-3">Actions</h2>

            {(!isReviewed || editing) && !pendingLabel && (
              <div className="space-y-2">
                <p className="text-xs text-gray-500">Confirm label:</p>
                <div className="flex flex-col gap-1.5">
                  <button
                    onClick={() => { setPendingLabel("my_dog"); setPendingBreed("Labrador Retriever"); }}
                    className="w-full inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-green-700 bg-green-50 hover:bg-green-100 rounded-lg"
                  >
                    <Dog className="w-4 h-4" /> My Dog
                  </button>
                  <button
                    onClick={() => { setPendingLabel("other_dog"); setPendingBreed("Unknown"); }}
                    className="w-full inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-orange-700 bg-orange-50 hover:bg-orange-100 rounded-lg"
                  >
                    <Dog className="w-4 h-4" /> Other Dog
                  </button>
                  <button
                    onClick={() => { setPendingLabel("no_dog"); handleConfirm(); }}
                    className="w-full inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-gray-600 bg-gray-50 hover:bg-gray-100 rounded-lg"
                  >
                    <Ban className="w-4 h-4" /> No Dog
                  </button>
                </div>
                {editing && (
                  <button onClick={() => setEditing(false)} className="text-xs text-gray-400 hover:text-gray-600">
                    Cancel
                  </button>
                )}
              </div>
            )}

            {pendingLabel && pendingLabel !== "no_dog" && (
              <div className="space-y-2">
                <p className="text-xs text-gray-500">Select breed for {pendingLabel === "my_dog" ? "My Dog" : "Other Dog"}:</p>
                <select
                  value={pendingBreed}
                  onChange={(e) => setPendingBreed(e.target.value)}
                  className="w-full text-sm border border-gray-300 rounded px-2 py-1.5"
                >
                  {DOG_BREEDS.map((b) => <option key={b} value={b}>{b}</option>)}
                </select>
                <div className="flex gap-2">
                  <button
                    onClick={() => setPendingLabel(null)}
                    className="flex-1 px-3 py-1.5 text-sm font-medium text-gray-600 bg-gray-100 hover:bg-gray-200 rounded-lg"
                  >
                    Back
                  </button>
                  <button
                    onClick={handleConfirm}
                    disabled={confirming}
                    className="flex-1 px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
                  >
                    {confirming ? <Loader2 className="w-4 h-4 animate-spin mx-auto" /> : "Confirm"}
                  </button>
                </div>
              </div>
            )}

            {isReviewed && !editing && (
              <button
                onClick={() => setEditing(true)}
                className="text-xs text-blue-600 hover:text-blue-700"
              >
                Re-review this label
              </button>
            )}
          </div>

          {/* Metadata */}
          <div className="bg-white rounded-xl border border-gray-200 p-4">
            <h2 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-3">Metadata</h2>
            <div>
              {label.clip_id && (
                <MetaRow label="Clip">
                  <Link
                    to={`/clips/${label.clip_id}`}
                    className="text-blue-600 hover:underline inline-flex items-center gap-1"
                  >
                    {label.clip_id.slice(0, 12)}… <ExternalLink className="w-3 h-3" />
                  </Link>
                </MetaRow>
              )}
              {label.device && (
                <MetaRow label="Device">{label.device}</MetaRow>
              )}
              {label.labelled_at && (
                <MetaRow label="Labelled">
                  {formatDistanceToNow(new Date(label.labelled_at), { addSuffix: true })}
                </MetaRow>
              )}
              {label.reviewed_at && (
                <MetaRow label="Reviewed">
                  {formatDistanceToNow(new Date(label.reviewed_at), { addSuffix: true })}
                </MetaRow>
              )}
              <MetaRow label="S3 Key">
                <span className="font-mono text-gray-500 break-all">{keyframeKey}</span>
              </MetaRow>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
