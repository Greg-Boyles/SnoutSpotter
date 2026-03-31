import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { ArrowLeft, Download, Trash2, Loader2, Package, CheckCircle, XCircle, Clock } from "lucide-react";
import { api } from "../api";

interface ExportItem {
  export_id: string;
  status: string;
  created_at: string;
  completed_at?: string;
  total_images?: string;
  my_dog_count?: string;
  not_my_dog_count?: string;
  train_count?: string;
  val_count?: string;
  size_mb?: string;
  error?: string;
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
  const [deleting, setDeleting] = useState<string | null>(null);

  const loadExports = () => {
    api.listExports()
      .then((data) => setExports(data.exports as unknown as ExportItem[]))
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadExports();
    // Poll for running exports
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

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-gray-400 justify-center py-12">
        <Loader2 className="w-4 h-4 animate-spin" /> Loading exports...
      </div>
    );
  }

  return (
    <div className="max-w-3xl">
      <Link to="/labels" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> Labels
      </Link>

      <h1 className="text-2xl font-bold text-gray-900 mb-6">Training Exports</h1>

      {exports.length === 0 ? (
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <Package className="w-12 h-12 text-gray-300 mx-auto mb-3" />
          <p className="text-gray-500">No exports yet</p>
          <p className="text-xs text-gray-400 mt-1">Go to Labels and click "Export Dataset"</p>
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
                <div className="grid grid-cols-4 gap-3 mb-3">
                  <div className="text-center">
                    <p className="text-lg font-bold text-gray-900">{exp.total_images || 0}</p>
                    <p className="text-xs text-gray-500">Total</p>
                  </div>
                  <div className="text-center">
                    <p className="text-lg font-bold text-green-600">{exp.my_dog_count || 0}</p>
                    <p className="text-xs text-gray-500">My Dog</p>
                  </div>
                  <div className="text-center">
                    <p className="text-lg font-bold text-gray-600">{exp.not_my_dog_count || 0}</p>
                    <p className="text-xs text-gray-500">Not My Dog</p>
                  </div>
                  <div className="text-center">
                    <p className="text-lg font-bold text-gray-900">{exp.size_mb || 0} MB</p>
                    <p className="text-xs text-gray-500">Size</p>
                  </div>
                </div>
              )}

              {exp.status === "complete" && exp.train_count && (
                <p className="text-xs text-gray-400 mb-3">
                  Train: {exp.train_count} | Val: {exp.val_count}
                </p>
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
