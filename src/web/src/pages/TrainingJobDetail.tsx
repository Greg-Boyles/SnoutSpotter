import { useEffect, useState } from "react";
import { useParams, Link, useNavigate } from "react-router-dom";
import { ArrowLeft, Loader2, CheckCircle, XCircle, Play, Square, Zap, Copy, Trash2 } from "lucide-react";
import { formatDistanceToNow, format } from "date-fns";
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
  failedStage: string | null;
  createdAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
};

function parseJson(s: string | null): Record<string, unknown> | null {
  if (!s) return null;
  try { return JSON.parse(s); } catch { return null; }
}

function formatDuration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const m = Math.floor(seconds / 60);
  const h = Math.floor(m / 60);
  if (h > 0) return `${h}h ${m % 60}m`;
  return `${m}m ${seconds % 60}s`;
}

function MAP50Value({ value, label }: { value: number; label: string }) {
  const color = value >= 0.8 ? "text-green-600" : value >= 0.6 ? "text-amber-600" : "text-red-600";
  const bg = value >= 0.8 ? "bg-green-50" : value >= 0.6 ? "bg-amber-50" : "bg-red-50";
  const hint = value >= 0.8 ? "Good" : value >= 0.6 ? "Moderate" : "Low";
  return (
    <div className={`rounded-xl p-4 ${bg} text-center`}>
      <p className={`text-2xl font-bold ${color}`}>{value.toFixed(3)}</p>
      <p className="text-xs text-gray-500 mt-0.5">{label}</p>
      <p className={`text-xs font-medium mt-1 ${color}`}>{hint}</p>
    </div>
  );
}

const STAGES = [
  { key: "pending", label: "Pending" },
  { key: "downloading", label: "Downloading" },
  { key: "training", label: "Training" },
  { key: "uploading", label: "Uploading" },
  { key: "complete", label: "Complete" },
] as const;

function stageIndex(status: string): number {
  const map: Record<string, number> = {
    pending: 0,
    downloading: 1,
    training: 2,
    uploading: 3,
    complete: 4,
    cancelling: 2,
    cancelled: -1,
    failed: -1,
  };
  return map[status] ?? 0;
}

// Maps agent-reported failed_stage values to the UI STAGES index
function failedStageIndex(failedStage: string): number {
  const map: Record<string, number> = {
    preparing: 1,
    downloading: 1,
    extracting: 1,
    training: 2,
    uploading: 3,
  };
  return map[failedStage] ?? 1;
}

function StageTimeline({ status, failedStage }: { status: string; failedStage: string | null }) {
  const isFailed = status === "failed";
  const isCancelled = status === "cancelled" || status === "cancelling";
  const current = isFailed ? -1 : stageIndex(status);
  const failedIdx = isFailed && failedStage ? failedStageIndex(failedStage) : -1;

  return (
    <div className="flex items-center gap-0">
      {STAGES.map((stage, idx) => {
        const done = isFailed ? idx < failedIdx : current > idx;
        const active = !isFailed && !isCancelled && current === idx;
        const isFail = isFailed && idx === failedIdx;
        const isLast = idx === STAGES.length - 1;

        const dotColor = isFail
          ? "bg-red-500 ring-2 ring-red-200"
          : done
            ? "bg-green-500"
            : active
              ? "bg-amber-500 animate-pulse"
              : "bg-gray-200";

        const lineColor = done ? "bg-green-200" : "bg-gray-100";

        return (
          <div key={stage.key} className="flex items-center flex-1 min-w-0">
            <div className="flex flex-col items-center">
              <div className={`w-3 h-3 rounded-full shrink-0 ${dotColor}`} />
              <span className={`text-xs mt-1 whitespace-nowrap ${
                isFail ? "text-red-600 font-medium" :
                done ? "text-green-600 font-medium" :
                active ? "text-amber-600 font-medium" :
                "text-gray-400"
              }`}>{stage.label}</span>
            </div>
            {!isLast && (
              <div className={`flex-1 h-0.5 mx-1 ${lineColor}`} />
            )}
          </div>
        );
      })}
    </div>
  );
}

export default function TrainingJobDetail() {
  const { jobId } = useParams<{ jobId: string }>();
  const navigate = useNavigate();
  const [job, setJob] = useState<JobDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [activating, setActivating] = useState(false);
  const [deleting, setDeleting] = useState(false);

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

  const handleDelete = async () => {
    if (!jobId || !confirm("Delete this training job? This cannot be undone.")) return;
    setDeleting(true);
    try {
      await api.deleteTrainingJob(jobId);
      navigate("/training");
    } catch (e) {
      setError((e as Error).message);
      setDeleting(false);
    }
  };

  const handleClone = () => {
    const config = parseJson(job?.config ?? null);
    navigate("/training/new", {
      state: {
        exportId: job?.exportId,
        config,
      },
    });
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

  const agentDisplayName = job.agentThingName?.replace("snoutspotter-trainer-", "");
  const createdLabel = job.createdAt
    ? format(new Date(job.createdAt), "d MMM yyyy, HH:mm")
    : null;

  return (
    <div className="max-w-2xl">
      <Link to="/training" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> Training Jobs
      </Link>

      <div className="flex items-start justify-between mb-6">
        <div>
          <h1 className="text-xl font-bold text-gray-900">
            {job.exportId
              ? <>Export <span className="font-mono">{job.exportId.slice(0, 8)}</span></>
              : "Training Job"}
          </h1>
          <p className="text-xs font-mono text-gray-400 mt-0.5">{job.jobId}</p>
          <p className="text-sm text-gray-500 mt-1">
            {createdLabel}
            {agentDisplayName && ` · ${agentDisplayName}`}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={handleClone}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm text-gray-600 border border-gray-200 rounded-lg hover:bg-gray-50"
          >
            <Copy className="w-3.5 h-3.5" /> Clone
          </button>
          <span className={`inline-flex items-center gap-1.5 px-3 py-1.5 rounded-full text-sm font-medium ${
            isComplete ? "bg-green-100 text-green-700" :
            job.status === "failed" ? "bg-red-100 text-red-700" :
            job.status === "cancelled" ? "bg-gray-100 text-gray-600" :
            isRunning ? "bg-amber-100 text-amber-700" :
            "bg-gray-100 text-gray-600"
          }`}>
            {isComplete && <CheckCircle className="w-4 h-4" />}
            {job.status === "failed" && <XCircle className="w-4 h-4" />}
            {job.status === "training" && <Play className="w-4 h-4" />}
            {job.status}
          </span>
        </div>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-lg text-sm">{error}</div>}

      <div className="space-y-4">
        {/* Stage timeline */}
        {job.status !== "pending" || true ? (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <StageTimeline status={job.status} failedStage={job.failedStage} />
            {(job.status === "failed" || job.status === "cancelled") && (
              <p className="text-xs text-gray-400 mt-3 text-center">
                Job {job.status}
                {job.completedAt && ` ${formatDistanceToNow(new Date(job.completedAt), { addSuffix: true })}`}
              </p>
            )}
          </div>
        ) : null}

        {/* Download progress */}
        {job.status === "downloading" && progress?.download_total_bytes != null && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium text-gray-700">Downloading dataset</span>
              <span className="text-sm text-gray-500">
                {((progress.download_bytes as number ?? 0) / (1024 * 1024)).toFixed(0)} /
                {" "}{((progress.download_total_bytes as number) / (1024 * 1024)).toFixed(0)} MB
              </span>
            </div>
            <div className="w-full bg-gray-200 rounded-full h-3 mb-2">
              <div
                className="bg-blue-500 h-3 rounded-full transition-all duration-500"
                style={{ width: `${Math.min(100, ((progress.download_bytes as number ?? 0) / (progress.download_total_bytes as number)) * 100)}%` }}
              />
            </div>
            <div className="flex justify-between text-xs text-gray-400">
              <span>{Math.round(((progress.download_bytes as number ?? 0) / (progress.download_total_bytes as number)) * 100)}%</span>
              {progress.download_speed_mbps != null && (
                <span>{(progress.download_speed_mbps as number).toFixed(1)} MB/s</span>
              )}
            </div>
          </div>
        )}

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

        {/* Live metrics */}
        {progress && (isRunning || (isComplete && !result)) && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Live Metrics</h3>
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
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-sm font-semibold text-gray-900">Results</h3>
              {result.training_time_seconds != null && (
                <span className="text-xs text-gray-400">
                  Training took {formatDuration(result.training_time_seconds as number)}
                </span>
              )}
            </div>

            {/* mAP50 hero metric */}
            {result.final_mAP50 != null && (
              <div className="mb-4">
                <MAP50Value value={result.final_mAP50 as number} label="Final mAP50" />
              </div>
            )}

            <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 mb-4">
              {result.final_mAP50_95 != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(result.final_mAP50_95 as number).toFixed(3)}</p>
                  <p className="text-xs text-gray-500">mAP50-95</p>
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
              {result.dataset_images != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{result.dataset_images as number}</p>
                  <p className="text-xs text-gray-500">Dataset Images</p>
                </div>
              )}
              {result.model_size_mb != null && (
                <div>
                  <p className="text-lg font-bold text-gray-900">{(result.model_size_mb as number).toFixed(1)} MB</p>
                  <p className="text-xs text-gray-500">Model Size</p>
                </div>
              )}
            </div>
            {!!result.model_s3_key && (
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
            {job.failedStage && (
              <p className="text-xs text-red-500 mb-2">
                Failed during: {job.failedStage.charAt(0).toUpperCase() + job.failedStage.slice(1)}
              </p>
            )}
            <p className="text-sm text-red-700 font-mono">{job.error}</p>
          </div>
        )}

        {/* Actions */}
        <div className="flex gap-3">
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
          {!isRunning && (
            <button
              onClick={handleDelete}
              disabled={deleting}
              className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-red-600 border border-red-200 hover:bg-red-50 rounded-lg disabled:opacity-50"
            >
              {deleting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
              {deleting ? "Deleting..." : "Delete Job"}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
