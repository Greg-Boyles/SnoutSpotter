import { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft, Loader2, CheckCircle, XCircle, Play, Square, Zap } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";

type JobDetail = {
  jobId: string;
  status: string;
  agentThingName: string | null;
  exportId: string | null;
  config: string | null;
  progress: string | null;
  result: string | null;
  error: string | null;
  createdAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
};

function parseJson(s: string | null): Record<string, unknown> | null {
  if (!s) return null;
  try { return JSON.parse(s); } catch { return null; }
}

export default function TrainingJobDetail() {
  const { jobId } = useParams<{ jobId: string }>();
  const [job, setJob] = useState<JobDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [activating, setActivating] = useState(false);

  const loadJob = () => {
    if (!jobId) return;
    api.getTrainingJob(jobId)
      .then((data) => setJob(data as unknown as JobDetail))
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadJob();
    const interval = setInterval(loadJob, 5_000);
    return () => clearInterval(interval);
  }, [jobId]);

  const handleCancel = async () => {
    if (!jobId) return;
    setCancelling(true);
    try {
      await api.cancelTrainingJob(jobId);
      loadJob();
    } catch (e) {
      setError((e as Error).message);
    }
    setCancelling(false);
  };

  const handleActivate = async () => {
    const result = parseJson(job?.result ?? null);
    const modelKey = result?.model_s3_key as string;
    if (!modelKey) return;
    const version = modelKey.match(/versions\/(v[\d.]+)\//)?.[1];
    if (!version) return;
    setActivating(true);
    try {
      await api.activateModel(version);
      loadJob();
    } catch (e) {
      setError((e as Error).message);
    }
    setActivating(false);
  };

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-gray-400">
        <Loader2 className="w-4 h-4 animate-spin" /> Loading...
      </div>
    );
  }

  if (error && !job) {
    return (
      <div>
        <Link to="/training" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
          <ArrowLeft className="w-4 h-4" /> Training Jobs
        </Link>
        <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
      </div>
    );
  }

  if (!job) return null;

  const config = parseJson(job.config);
  const progress = parseJson(job.progress);
  const result = parseJson(job.result);
  const isRunning = ["pending", "downloading", "training", "uploading", "cancelling"].includes(job.status);
  const isComplete = job.status === "complete";
  const epoch = (progress?.epoch as number) ?? 0;
  const totalEpochs = (progress?.total_epochs as number) ?? (config?.epochs as number) ?? 0;
  const percent = totalEpochs > 0 ? Math.round((epoch / totalEpochs) * 100) : 0;

  return (
    <div className="max-w-2xl">
      <Link to="/training" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> Training Jobs
      </Link>

      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">{job.jobId}</h1>
          <p className="text-sm text-gray-500 mt-1">
            {job.agentThingName?.replace("snoutspotter-trainer-", "") || "Unassigned"}
            {job.createdAt && ` · Created ${formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}`}
          </p>
        </div>
        <span className={`inline-flex items-center gap-1.5 px-3 py-1.5 rounded-full text-sm font-medium ${
          isComplete ? "bg-green-100 text-green-700" :
          job.status === "failed" ? "bg-red-100 text-red-700" :
          isRunning ? "bg-amber-100 text-amber-700" :
          "bg-gray-100 text-gray-600"
        }`}>
          {isComplete && <CheckCircle className="w-4 h-4" />}
          {job.status === "failed" && <XCircle className="w-4 h-4" />}
          {job.status === "training" && <Play className="w-4 h-4" />}
          {job.status}
        </span>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-lg text-sm">{error}</div>}

      <div className="space-y-4">
        {/* Progress bar */}
        {(isRunning || isComplete) && totalEpochs > 0 && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium text-gray-700">
                Epoch {epoch} / {totalEpochs}
              </span>
              <span className="text-sm text-gray-500">{percent}%</span>
            </div>
            <div className="w-full bg-gray-200 rounded-full h-3">
              <div
                className={`h-3 rounded-full transition-all duration-500 ${isComplete ? "bg-green-500" : "bg-amber-500"}`}
                style={{ width: `${percent}%` }}
              />
            </div>
            {progress?.eta_seconds != null && isRunning && (
              <p className="text-xs text-gray-400 mt-2">
                ETA: {Math.round((progress.eta_seconds as number) / 60)} min remaining
              </p>
            )}
          </div>
        )}

        {/* Metrics */}
        {progress && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Metrics</h3>
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
              {progress.mAP50 != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(progress.mAP50 as number).toFixed(3)}</p>
                  <p className="text-xs text-gray-500">mAP50</p>
                </div>
              )}
              {progress.best_mAP50 != null && (
                <div>
                  <p className="text-lg font-bold text-green-600">{(progress.best_mAP50 as number).toFixed(3)}</p>
                  <p className="text-xs text-gray-500">Best mAP50</p>
                </div>
              )}
              {progress.train_loss != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(progress.train_loss as number).toFixed(4)}</p>
                  <p className="text-xs text-gray-500">Train Loss</p>
                </div>
              )}
              {progress.val_loss != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(progress.val_loss as number).toFixed(4)}</p>
                  <p className="text-xs text-gray-500">Val Loss</p>
                </div>
              )}
              {progress.gpu_temp_c != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{progress.gpu_temp_c as number}°C</p>
                  <p className="text-xs text-gray-500">GPU Temp</p>
                </div>
              )}
              {progress.gpu_util_percent != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{progress.gpu_util_percent as number}%</p>
                  <p className="text-xs text-gray-500">GPU Util</p>
                </div>
              )}
            </div>
          </div>
        )}

        {/* Final result */}
        {result && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Results</h3>
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 mb-4">
              {result.final_mAP50 != null && (
                <div>
                  <p className="text-lg font-bold text-green-600">{(result.final_mAP50 as number).toFixed(3)}</p>
                  <p className="text-xs text-gray-500">Final mAP50</p>
                </div>
              )}
              {result.precision != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(result.precision as number).toFixed(3)}</p>
                  <p className="text-xs text-gray-500">Precision</p>
                </div>
              )}
              {result.recall != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(result.recall as number).toFixed(3)}</p>
                  <p className="text-xs text-gray-500">Recall</p>
                </div>
              )}
              {result.best_epoch != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{result.best_epoch as number}</p>
                  <p className="text-xs text-gray-500">Best Epoch</p>
                </div>
              )}
            </div>
            {result.model_s3_key && (
              <button
                onClick={handleActivate}
                disabled={activating}
                className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-lg disabled:opacity-50"
              >
                {activating ? <Loader2 className="w-4 h-4 animate-spin" /> : <Zap className="w-4 h-4" />}
                {activating ? "Activating..." : "Activate Model"}
              </button>
            )}
          </div>
        )}

        {/* Config */}
        {config && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Configuration</h3>
            <div className="grid grid-cols-2 gap-2 text-sm">
              {Object.entries(config).filter(([, v]) => v != null).map(([k, v]) => (
                <div key={k} className="flex justify-between py-1 border-b border-gray-50">
                  <span className="text-gray-500">{k.replace(/_/g, " ")}</span>
                  <span className="text-gray-700">{String(v)}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Error */}
        {job.error && (
          <div className="bg-red-50 border border-red-200 rounded-xl p-5">
            <h3 className="text-sm font-semibold text-red-800 mb-1">Error</h3>
            <p className="text-sm text-red-700">{job.error}</p>
          </div>
        )}

        {/* Actions */}
        {isRunning && (
          <button
            onClick={handleCancel}
            disabled={cancelling}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-gray-600 border border-gray-200 hover:text-red-600 hover:border-red-200 rounded-lg disabled:opacity-50"
          >
            {cancelling ? <Loader2 className="w-4 h-4 animate-spin" /> : <Square className="w-4 h-4" />}
            {cancelling ? "Cancelling..." : "Cancel Job"}
          </button>
        )}
      </div>
    </div>
  );
}
