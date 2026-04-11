import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { ArrowLeft, Upload, CheckCircle, AlertCircle, Cpu, Loader2, Zap, RefreshCw } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";

interface ModelVersion {
  version: string;
  s3Key: string;
  sizeBytes: number;
  lastModified: string;
  active: boolean;
}

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`;
}

export default function Models() {
  const [versions, setVersions] = useState<ModelVersion[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Upload state
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploadVersion, setUploadVersion] = useState("");
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState(0);
  const [activating, setActivating] = useState<string | null>(null);

  // Re-run inference state
  const [rerunFrom, setRerunFrom] = useState("");
  const [rerunTo, setRerunTo] = useState("");
  const [rerunning, setRerunning] = useState(false);
  const [showRerunPrompt, setShowRerunPrompt] = useState(false);

  const loadModels = async () => {
    try {
      const data = await api.listModels();
      setVersions(data.versions);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load models");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadModels();
  }, []);

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    if (!file.name.endsWith(".onnx")) {
      setError("Only .onnx files are supported");
      return;
    }

    if (!uploadVersion.trim()) {
      setError("Please enter a version name");
      return;
    }

    setError(null);
    setSuccess(null);
    setUploading(true);
    setUploadProgress(0);

    try {
      const { uploadUrl } = await api.getModelUploadUrl(uploadVersion.trim());

      await new Promise<void>((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open("PUT", uploadUrl);
        xhr.setRequestHeader("Content-Type", "application/octet-stream");

        xhr.upload.onprogress = (evt) => {
          if (evt.lengthComputable) {
            setUploadProgress(Math.round((evt.loaded / evt.total) * 100));
          }
        };

        xhr.onload = () => {
          if (xhr.status >= 200 && xhr.status < 300) resolve();
          else reject(new Error(`Upload failed: ${xhr.status}`));
        };

        xhr.onerror = () => reject(new Error("Upload failed"));
        xhr.send(file);
      });

      setSuccess(`Version "${uploadVersion.trim()}" uploaded successfully`);
      setUploadVersion("");
      loadModels();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Upload failed");
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = "";
    }
  };

  const handleActivate = async (version: string) => {
    setActivating(version);
    setError(null);
    setSuccess(null);

    try {
      await api.activateModel(version);
      setSuccess(`Version "${version}" is now active`);
      setShowRerunPrompt(true);
      loadModels();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Activation failed");
    } finally {
      setActivating(null);
    }
  };

  return (
    <div className="space-y-6">
      <div>
        <Link
          to="/exports"
          className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-2"
        >
          <ArrowLeft className="w-4 h-4" /> Training Exports
        </Link>
        <h1 className="text-2xl font-bold text-gray-900">Detection Models</h1>
        <p className="text-sm text-gray-500 mt-1">
          YOLOv8 ONNX models for the inference pipeline. Upload new versions and choose which one is active.
        </p>
      </div>

      {success && (
        <div className="p-3 bg-green-50 text-green-700 rounded-lg text-sm flex items-center gap-2">
          <CheckCircle className="w-4 h-4" /> {success}
        </div>
      )}

      {error && (
        <div className="p-3 bg-red-50 text-red-600 rounded-lg text-sm flex items-center gap-2">
          <AlertCircle className="w-4 h-4" /> {error}
        </div>
      )}

      {/* Upload new version */}
      <div className="bg-white rounded-xl border border-gray-200 p-5">
        <h2 className="text-sm font-semibold text-gray-900 mb-3">Upload New Version</h2>
        <div className="flex items-end gap-3">
          <div className="flex-1">
            <label className="block text-xs text-gray-500 mb-1">Version name</label>
            <input
              type="text"
              value={uploadVersion}
              onChange={(e) => setUploadVersion(e.target.value)}
              placeholder="e.g. v2.0"
              disabled={uploading}
              className="w-full px-3 py-2 text-sm border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-transparent disabled:opacity-50"
            />
          </div>
          <input
            ref={fileInputRef}
            type="file"
            accept=".onnx"
            onChange={handleUpload}
            className="hidden"
          />
          <button
            onClick={() => {
              if (!uploadVersion.trim()) {
                setError("Please enter a version name");
                return;
              }
              fileInputRef.current?.click();
            }}
            disabled={uploading}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {uploading ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Upload className="w-4 h-4" />
            )}
            {uploading ? "Uploading..." : "Select .onnx file"}
          </button>
        </div>

        {uploading && (
          <div className="mt-3">
            <div className="flex justify-between text-xs text-gray-500 mb-1">
              <span>Uploading {uploadVersion}...</span>
              <span>{uploadProgress}%</span>
            </div>
            <div className="w-full h-1.5 bg-gray-100 rounded-full overflow-hidden">
              <div
                className="h-full bg-amber-500 rounded-full transition-all duration-300"
                style={{ width: `${uploadProgress}%` }}
              />
            </div>
          </div>
        )}
      </div>

      {/* Versions list */}
      {loading ? (
        <div className="flex items-center gap-2 text-gray-400 justify-center py-12">
          <Loader2 className="w-4 h-4 animate-spin" /> Loading models...
        </div>
      ) : versions.length === 0 ? (
        <div className="bg-white rounded-xl border border-gray-200 p-8 text-center text-gray-500">
          <Cpu className="w-8 h-8 mx-auto mb-2 text-gray-300" />
          <p className="text-sm">No model versions uploaded yet</p>
        </div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <table className="w-full">
            <thead>
              <tr className="border-b border-gray-100 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                <th className="px-5 py-3">Version</th>
                <th className="px-5 py-3">Size</th>
                <th className="px-5 py-3">Uploaded</th>
                <th className="px-5 py-3">Status</th>
                <th className="px-5 py-3"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-50">
              {versions.map((v) => (
                <tr key={v.version} className={v.active ? "bg-green-50/50" : ""}>
                  <td className="px-5 py-3">
                    <span className="text-sm font-medium text-gray-900">{v.version}</span>
                  </td>
                  <td className="px-5 py-3 text-sm text-gray-500">{formatBytes(v.sizeBytes)}</td>
                  <td className="px-5 py-3 text-sm text-gray-500">
                    {formatDistanceToNow(new Date(v.lastModified), { addSuffix: true })}
                  </td>
                  <td className="px-5 py-3">
                    {v.active ? (
                      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-700">
                        <CheckCircle className="w-3 h-3" /> Active
                      </span>
                    ) : (
                      <span className="text-xs text-gray-400">Inactive</span>
                    )}
                  </td>
                  <td className="px-5 py-3 text-right">
                    {!v.active && (
                      <button
                        onClick={() => handleActivate(v.version)}
                        disabled={activating !== null}
                        className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-lg bg-amber-50 text-amber-700 hover:bg-amber-100 disabled:opacity-50 disabled:cursor-not-allowed"
                      >
                        {activating === v.version ? (
                          <Loader2 className="w-3 h-3 animate-spin" />
                        ) : (
                          <Zap className="w-3 h-3" />
                        )}
                        Activate
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Post-activation prompt */}
      {showRerunPrompt && (
        <div className="bg-blue-50 border border-blue-200 rounded-xl p-4 flex items-center justify-between">
          <div>
            <p className="text-sm font-medium text-blue-800">Model activated</p>
            <p className="text-xs text-blue-600 mt-0.5">Re-run inference on existing clips to apply the new model?</p>
          </div>
          <div className="flex items-center gap-2">
            <button
              onClick={() => setShowRerunPrompt(false)}
              className="px-3 py-1.5 text-xs font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded-lg"
            >
              Dismiss
            </button>
            <button
              onClick={async () => {
                setShowRerunPrompt(false);
                setRerunning(true);
                setError(null);
                try {
                  const result = await api.rerunInference();
                  setSuccess(`Queued ${result.total} clips for inference re-run`);
                } catch (err) {
                  setError(err instanceof Error ? err.message : "Re-run failed");
                } finally {
                  setRerunning(false);
                }
              }}
              disabled={rerunning}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
            >
              {rerunning ? <Loader2 className="w-3 h-3 animate-spin" /> : <RefreshCw className="w-3 h-3" />}
              Re-run All
            </button>
          </div>
        </div>
      )}

      {/* Re-run Inference */}
      <div className="bg-white rounded-xl border border-gray-200 p-5">
        <h2 className="text-sm font-semibold text-gray-900 mb-1">Re-run Inference</h2>
        <p className="text-xs text-gray-500 mb-4">
          Re-run the active model on existing clips. Leave dates empty to process all clips.
        </p>
        <div className="flex items-end gap-3">
          <div>
            <label className="block text-xs text-gray-500 mb-1">From</label>
            <input
              type="date"
              value={rerunFrom}
              onChange={(e) => setRerunFrom(e.target.value)}
              disabled={rerunning}
              className="px-3 py-2 text-sm border border-gray-200 rounded-lg disabled:opacity-50"
            />
          </div>
          <div>
            <label className="block text-xs text-gray-500 mb-1">To</label>
            <input
              type="date"
              value={rerunTo}
              onChange={(e) => setRerunTo(e.target.value)}
              disabled={rerunning}
              className="px-3 py-2 text-sm border border-gray-200 rounded-lg disabled:opacity-50"
            />
          </div>
          <button
            onClick={async () => {
              setRerunning(true);
              setError(null);
              setSuccess(null);
              try {
                // Convert YYYY-MM-DD to YYYY/MM/DD for the API
                const dateFrom = rerunFrom ? rerunFrom.replace(/-/g, "/") : undefined;
                const dateTo = rerunTo ? rerunTo.replace(/-/g, "/") : undefined;
                const result = await api.rerunInference(dateFrom, dateTo);
                setSuccess(`Queued ${result.total} clips for inference re-run`);
              } catch (err) {
                setError(err instanceof Error ? err.message : "Re-run failed");
              } finally {
                setRerunning(false);
              }
            }}
            disabled={rerunning}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-50"
          >
            {rerunning ? <Loader2 className="w-4 h-4 animate-spin" /> : <RefreshCw className="w-4 h-4" />}
            {rerunning ? "Queuing..." : "Re-run Inference"}
          </button>
        </div>
      </div>
    </div>
  );
}
