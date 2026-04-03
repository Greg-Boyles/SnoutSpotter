import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { ArrowLeft, Upload, CheckCircle, AlertCircle, Cpu, Loader2 } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";

interface ModelInfo {
  modelType: string;
  s3Key: string;
  lastModified: string | null;
  sizeBytes: number;
  deployed: boolean;
}

const MODEL_LABELS: Record<string, { name: string; description: string }> = {
  "dog-detector": { name: "Dog Detector", description: "YOLOv8 — detects dogs in video keyframes" },
  "dog-classifier": { name: "Dog Classifier", description: "MobileNetV3 — classifies my_dog vs not_my_dog" },
};

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`;
}

function ModelCard({
  model,
  onUploaded,
}: {
  model: ModelInfo;
  onUploaded: () => void;
}) {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);

  const label = MODEL_LABELS[model.modelType] ?? { name: model.modelType, description: "" };

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    if (!file.name.endsWith(".onnx")) {
      setError("Only .onnx files are supported");
      return;
    }

    setError(null);
    setSuccess(false);
    setUploading(true);
    setProgress(0);

    try {
      const { uploadUrl } = await api.getModelUploadUrl(model.modelType);

      await new Promise<void>((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open("PUT", uploadUrl);
        xhr.setRequestHeader("Content-Type", "application/octet-stream");

        xhr.upload.onprogress = (evt) => {
          if (evt.lengthComputable) {
            setProgress(Math.round((evt.loaded / evt.total) * 100));
          }
        };

        xhr.onload = () => {
          if (xhr.status >= 200 && xhr.status < 300) resolve();
          else reject(new Error(`Upload failed: ${xhr.status}`));
        };

        xhr.onerror = () => reject(new Error("Upload failed"));
        xhr.send(file);
      });

      setSuccess(true);
      onUploaded();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Upload failed");
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = "";
    }
  };

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5">
      <div className="flex items-start justify-between mb-3">
        <div className="flex items-center gap-3">
          <div className="p-2 rounded-lg bg-amber-50">
            <Cpu className="w-5 h-5 text-amber-600" />
          </div>
          <div>
            <h3 className="font-semibold text-gray-900">{label.name}</h3>
            <p className="text-xs text-gray-500">{label.description}</p>
          </div>
        </div>
        {model.deployed ? (
          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-green-50 text-green-700">
            <CheckCircle className="w-3 h-3" /> Deployed
          </span>
        ) : (
          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-500">
            Not deployed
          </span>
        )}
      </div>

      {model.deployed && model.lastModified && (
        <div className="grid grid-cols-2 gap-3 mb-4 text-sm">
          <div>
            <span className="text-gray-500">Size</span>
            <p className="font-medium text-gray-900">{formatBytes(model.sizeBytes)}</p>
          </div>
          <div>
            <span className="text-gray-500">Last updated</span>
            <p className="font-medium text-gray-900">
              {formatDistanceToNow(new Date(model.lastModified), { addSuffix: true })}
            </p>
          </div>
        </div>
      )}

      <div className="text-xs text-gray-400 mb-4 font-mono">{model.s3Key}</div>

      {success && (
        <div className="mb-3 p-2 bg-green-50 text-green-700 rounded-lg text-sm flex items-center gap-2">
          <CheckCircle className="w-4 h-4" /> Model uploaded successfully
        </div>
      )}

      {error && (
        <div className="mb-3 p-2 bg-red-50 text-red-600 rounded-lg text-sm flex items-center gap-2">
          <AlertCircle className="w-4 h-4" /> {error}
        </div>
      )}

      {uploading && (
        <div className="mb-3">
          <div className="flex justify-between text-xs text-gray-500 mb-1">
            <span>Uploading...</span>
            <span>{progress}%</span>
          </div>
          <div className="w-full h-1.5 bg-gray-100 rounded-full overflow-hidden">
            <div
              className="h-full bg-amber-500 rounded-full transition-all duration-300"
              style={{ width: `${progress}%` }}
            />
          </div>
        </div>
      )}

      <input
        ref={fileInputRef}
        type="file"
        accept=".onnx"
        onChange={handleUpload}
        className="hidden"
      />

      <button
        onClick={() => fileInputRef.current?.click()}
        disabled={uploading}
        className="w-full flex items-center justify-center gap-2 px-4 py-2 text-sm font-medium rounded-lg bg-amber-50 text-amber-700 hover:bg-amber-100 disabled:opacity-50 disabled:cursor-not-allowed"
      >
        {uploading ? (
          <Loader2 className="w-4 h-4 animate-spin" />
        ) : (
          <Upload className="w-4 h-4" />
        )}
        {uploading ? "Uploading..." : "Upload new model"}
      </button>
    </div>
  );
}

export default function Models() {
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadModels = async () => {
    try {
      const data = await api.listModels();
      setModels(data.models);
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

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <Link
            to="/exports"
            className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-2"
          >
            <ArrowLeft className="w-4 h-4" /> Training Exports
          </Link>
          <h1 className="text-2xl font-bold text-gray-900">Deployed Models</h1>
          <p className="text-sm text-gray-500 mt-1">
            ONNX models used by the inference pipeline. Upload a new model to replace the current one.
          </p>
        </div>
      </div>

      {loading ? (
        <div className="flex items-center gap-2 text-gray-400 justify-center py-12">
          <Loader2 className="w-4 h-4 animate-spin" />
          Loading models...
        </div>
      ) : error ? (
        <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {models.map((model) => (
            <ModelCard key={model.modelType} model={model} onUploaded={loadModels} />
          ))}
        </div>
      )}
    </div>
  );
}
