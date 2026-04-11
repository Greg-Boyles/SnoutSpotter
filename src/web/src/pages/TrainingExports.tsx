import { useEffect, useState } from "react";
import { formatDistanceToNow } from "date-fns";
import { Download, Trash2, Loader2, Package, CheckCircle, XCircle, Clock, Dog, Ban } from "lucide-react";

function Skeleton({ className }: { className: string }) {
  return <div className={`animate-pulse bg-gray-200 rounded ${className}`} />;
}
import { api } from "../api";

interface ExportItem {
  export_id: string;
  status: string;
  created_at: string;
  completed_at?: string;
  total_images?: string;
  my_dog_count?: string;
  not_my_dog_count?: string;
  no_dog_count?: string;
  skipped_no_boxes_count?: string;
  train_count?: string;
  val_count?: string;
  size_mb?: string;
  error?: string;
}

function Stat({ label, value, color }: { label: string; value?: string; color: string }) {
  return (
    <div className="text-center">
      <p className={`text-lg font-bold ${color}`}>{value ?? "—"}</p>
      <p className="text-xs text-gray-500">{label}</p>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    complete: "bg-green-50 text-green-700",
    running: "bg-blue-50 text-blue-700",
    failed: "bg-red-50 text-red-700",
  };
  const icons: Record<string, React.ReactNode> = {
    complete: <CheckCircle className="w-3 h-3" />,
    running: <Loader2 className="w-3 h-3 animate-spin" />,
    failed: <XCircle className="w-3 h-3" />,
  };
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] || "bg-gray-50 text-gray-700"}`}>
      {icons[status] || <Clock className="w-3 h-3" />}
      {status}
    </span>
  );
}

export default function TrainingExports() {
  const [exports, setExports] = useState<ExportItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [exporting, setExporting] = useState(false);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [labelStats, setLabelStats] = useState<{ myDog: number; otherDog: number; confirmedNoDog: number; breeds: Record<string, number> } | null>(null);

  const loadExports = () => {
    api.listExports()
      .then((data) => setExports(data.exports as unknown as ExportItem[]))
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadExports();
    api.getLabelStats().then(setLabelStats).catch(console.error);
    const interval = setInterval(loadExports, 10000);
    return () => clearInterval(interval);
  }, []);

  const handleDownload = async (exportId: string) => {
    try {
      const { downloadUrl } = await api.getExportDownload(exportId);
      window.open(downloadUrl, "_blank");
    } catch (e) {
      console.error("Download failed:", e);
    }
  };

  const handleDelete = async (exportId: string) => {
    setDeleting(exportId);
    try {
      await api.deleteExport(exportId);
      setExports((prev) => prev.filter((e) => e.export_id !== exportId));
    } catch (e) {
      console.error("Delete failed:", e);
    }
    setDeleting(null);
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Training Exports</h1>
        <button
          onClick={async () => {
            setExporting(true);
            try { await api.triggerExport(); loadExports(); } catch (e) { console.error(e); }
            setExporting(false);
          }}
          disabled={exporting}
          className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50"
        >
          {exporting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Package className="w-4 h-4" />}
          {exporting ? "Exporting..." : "New Export"}
        </button>
      </div>

      {/* Label Summary */}
      {!labelStats ? (
        <div className="bg-white rounded-xl border border-gray-200 p-5 mb-6">
          <Skeleton className="h-4 w-40 mb-4" />
          <div className="grid grid-cols-3 gap-4 mb-4">
            {Array.from({ length: 3 }).map((_, i) => (
              <div key={i} className="text-center space-y-1.5">
                <Skeleton className="h-3 w-16 mx-auto" />
                <Skeleton className="h-8 w-12 mx-auto" />
              </div>
            ))}
          </div>
          <div className="pt-3 border-t border-gray-100">
            <Skeleton className="h-3 w-12 mb-2" />
            <div className="flex gap-1.5">
              {Array.from({ length: 4 }).map((_, i) => (
                <Skeleton key={i} className="h-5 w-24 rounded-full" />
              ))}
            </div>
          </div>
        </div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 p-5 mb-6">
          <h2 className="text-sm font-semibold text-gray-900 mb-4">Training Data Summary</h2>
          <div className="grid grid-cols-3 gap-4 mb-4">
            <div className="text-center">
              <div className="flex items-center justify-center gap-1.5 mb-1">
                <Dog className="w-4 h-4 text-green-500" />
                <span className="text-xs text-gray-500">My Dog</span>
              </div>
              <p className="text-2xl font-bold text-green-600">{labelStats.myDog}</p>
            </div>
            <div className="text-center">
              <div className="flex items-center justify-center gap-1.5 mb-1">
                <Dog className="w-4 h-4 text-orange-500" />
                <span className="text-xs text-gray-500">Other Dog</span>
              </div>
              <p className="text-2xl font-bold text-orange-600">{labelStats.otherDog}</p>
            </div>
            <div className="text-center">
              <div className="flex items-center justify-center gap-1.5 mb-1">
                <Ban className="w-4 h-4 text-gray-400" />
                <span className="text-xs text-gray-500">No Dog</span>
              </div>
              <p className="text-2xl font-bold text-gray-500">{labelStats.confirmedNoDog}</p>
            </div>
          </div>
          {Object.keys(labelStats.breeds).length > 0 && (
            <div className="pt-3 border-t border-gray-100">
              <p className="text-xs font-medium text-gray-500 mb-2">Breeds</p>
              <div className="flex flex-wrap gap-1.5">
                {Object.entries(labelStats.breeds)
                  .sort(([, a], [, b]) => b - a)
                  .map(([breed, count]) => (
                    <span key={breed} className="inline-flex items-center gap-1 px-2 py-0.5 bg-gray-50 border border-gray-100 text-xs text-gray-600 rounded-full">
                      {breed} <span className="font-semibold text-gray-900">{count}</span>
                    </span>
                  ))}
              </div>
            </div>
          )}
        </div>
      )}

      {loading ? (
        <div className="space-y-3">
          {Array.from({ length: 3 }).map((_, i) => (
            <div key={i} className="bg-white rounded-xl border border-gray-200 p-5">
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-3">
                  <Skeleton className="w-5 h-5 rounded" />
                  <div className="space-y-1.5">
                    <Skeleton className="h-4 w-32" />
                    <Skeleton className="h-3 w-24" />
                  </div>
                </div>
                <Skeleton className="h-5 w-20 rounded-full" />
              </div>
              <div className="grid grid-cols-4 gap-3 mb-3">
                {Array.from({ length: 4 }).map((_, j) => (
                  <div key={j} className="text-center space-y-1">
                    <Skeleton className="h-6 w-10 mx-auto" />
                    <Skeleton className="h-3 w-12 mx-auto" />
                  </div>
                ))}
              </div>
              <div className="flex gap-2">
                <Skeleton className="h-7 w-24 rounded-lg" />
                <Skeleton className="h-7 w-20 rounded-lg" />
              </div>
            </div>
          ))}
        </div>
      ) : exports.length === 0 ? (
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <Package className="w-12 h-12 text-gray-300 mx-auto mb-3" />
          <p className="text-gray-500">No exports yet</p>
          <p className="text-xs text-gray-400 mt-1">Click "New Export" to package your training data</p>
        </div>
      ) : (
        <div className="space-y-3">
          {exports.map((exp) => (
            <div key={exp.export_id} className="bg-white rounded-xl border border-gray-200 p-5">
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-3">
                  <Package className="w-5 h-5 text-gray-400" />
                  <div>
                    <p className="text-sm font-medium text-gray-900">
                      {exp.created_at ? formatDistanceToNow(new Date(exp.created_at), { addSuffix: true }) : "Unknown"}
                    </p>
                    <p className="text-xs text-gray-400 font-mono">{exp.export_id.substring(0, 12)}...</p>
                  </div>
                </div>
                <StatusBadge status={exp.status} />
              </div>

              {exp.status === "complete" && (
                <>
                  <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-1">Included</p>
                  <div className="grid grid-cols-4 gap-3 mb-2">
                    <Stat label="My Dog"    value={exp.my_dog_count}    color="text-green-600" />
                    <Stat label="Other Dog" value={exp.not_my_dog_count} color="text-gray-700" />
                    <Stat label="No Dog"    value={exp.no_dog_count}     color="text-gray-500" />
                    <Stat label="Total"     value={exp.total_images}     color="text-gray-900" />
                  </div>
                  {Number(exp.skipped_no_boxes_count ?? 0) > 0 && (
                    <>
                      <p className="text-xs font-medium text-amber-600 uppercase tracking-wide mb-1">Skipped</p>
                      <div className="grid grid-cols-4 gap-3 mb-2">
                        <Stat label="No bounding box" value={exp.skipped_no_boxes_count} color="text-amber-600" />
                      </div>
                    </>
                  )}
                  <p className="text-xs text-gray-400 mb-3">
                    {exp.size_mb || 0} MB · Train: {exp.train_count} · Val: {exp.val_count}
                  </p>
                </>
              )}

              {exp.error && (
                <p className="text-xs text-red-600 mb-3">{exp.error}</p>
              )}

              <div className="flex items-center gap-2">
                {exp.status === "complete" && (
                  <button
                    onClick={() => handleDownload(exp.export_id)}
                    className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-blue-700 bg-blue-50 hover:bg-blue-100 rounded-lg"
                  >
                    <Download className="w-3.5 h-3.5" /> Download
                  </button>
                )}
                <button
                  onClick={() => handleDelete(exp.export_id)}
                  disabled={deleting === exp.export_id}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-red-700 bg-red-50 hover:bg-red-100 rounded-lg disabled:opacity-50"
                >
                  <Trash2 className="w-3.5 h-3.5" /> Delete
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
