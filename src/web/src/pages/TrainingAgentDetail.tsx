import { useEffect, useState } from "react";
import { useParams, Link, useNavigate } from "react-router-dom";
import {
  ArrowLeft, Cpu, Thermometer, Zap,
  CheckCircle, XCircle, Clock, Play, AlertTriangle, Trash2
} from "lucide-react";
import { formatDistanceToNow, format } from "date-fns";
import { api } from "../api";

type GpuStatus = {
  name: string;
  vramMb: number;
  cudaVersion: string;
  driverVersion: string;
  temperatureC: number;
  utilizationPercent: number;
};

type TrainingProgress = {
  epoch: number;
  total_epochs: number;
  train_loss?: number;
  val_loss?: number;
  mAP50?: number;
  best_mAP50?: number;
  elapsed_seconds?: number;
  eta_seconds?: number;
  gpu_util_percent?: number;
  gpu_temp_c?: number;
};

type AgentReported = {
  agentVersion: string;
  hostname: string;
  lastHeartbeat: string;
  status: string;
  updateStatus: string;
  gpu?: GpuStatus;
  currentJobId?: string;
  currentJobProgress?: TrainingProgress;
  deferredVersion?: string;
  deferReason?: string;
};

type AgentStatus = {
  thingName: string;
  online: boolean;
  reported: AgentReported | null;
};

type Job = {
  jobId: string;
  status: string;
  agentThingName: string | null;
  exportId: string | null;
  epochs: number | null;
  createdAt: string | null;
  completedAt: string | null;
};

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    idle: "bg-gray-100 text-gray-600",
    training: "bg-amber-100 text-amber-700",
    downloading: "bg-blue-100 text-blue-700",
    uploading: "bg-blue-100 text-blue-700",
  };
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? "bg-gray-100 text-gray-600"}`}>
      {status === "training" && <Play className="w-3 h-3" />}
      {status}
    </span>
  );
}

function JobStatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    pending: "bg-gray-100 text-gray-600",
    downloading: "bg-blue-100 text-blue-700",
    training: "bg-amber-100 text-amber-700",
    uploading: "bg-blue-100 text-blue-700",
    complete: "bg-green-100 text-green-700",
    failed: "bg-red-100 text-red-700",
    cancelled: "bg-gray-100 text-gray-600",
  };
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? "bg-gray-100 text-gray-600"}`}>
      {status === "complete" && <CheckCircle className="w-3 h-3" />}
      {status === "failed" && <XCircle className="w-3 h-3" />}
      {status === "pending" && <Clock className="w-3 h-3" />}
      {status === "training" && <Play className="w-3 h-3" />}
      {status}
    </span>
  );
}

function formatDuration(seconds: number) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

export default function TrainingAgentDetail() {
  const { thingName } = useParams<{ thingName: string }>();
  const navigate = useNavigate();
  const [agent, setAgent] = useState<AgentStatus | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deregistering, setDeregistering] = useState(false);

  const load = () => {
    if (!thingName) return;
    return Promise.all([
      api.getTrainingAgentStatus(thingName),
      api.listTrainingJobs(),
    ]).then(([a, j]) => {
      setAgent(a as AgentStatus);
      setJobs((j.jobs as Job[]).filter(job => job.agentThingName === thingName));
    });
  };

  useEffect(() => {
    load()
      ?.catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));

    const interval = setInterval(() => load()?.catch(console.error), 10_000);
    return () => clearInterval(interval);
  }, [thingName]);

  const handleDeregister = async () => {
    if (!thingName || !confirm(`Deregister ${thingName}? This will delete its IoT thing and certificates.`)) return;
    setDeregistering(true);
    try {
      await fetch(`/api/trainers/${thingName}`, { method: "DELETE" });
      navigate("/training");
    } catch (e) {
      alert("Deregister failed");
      setDeregistering(false);
    }
  };

  if (loading) {
    return (
      <div className="animate-pulse space-y-4">
        <div className="h-8 bg-gray-100 rounded w-64" />
        <div className="h-40 bg-gray-100 rounded-xl" />
        <div className="h-40 bg-gray-100 rounded-xl" />
      </div>
    );
  }

  if (error || !agent) {
    return <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error ?? "Agent not found"}</div>;
  }

  const r = agent.reported;
  const displayName = r?.hostname ?? thingName!;
  const progress = r?.currentJobProgress;

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <Link to="/training" className="text-gray-400 hover:text-gray-600">
            <ArrowLeft className="w-5 h-5" />
          </Link>
          <div className="flex items-center gap-2">
            <span className={`w-2.5 h-2.5 rounded-full ${agent.online ? "bg-green-500 animate-pulse" : "bg-gray-300"}`} />
            <h1 className="text-2xl font-bold text-gray-900">{displayName}</h1>
            {r?.status && <StatusBadge status={r.status} />}
          </div>
        </div>
        <button
          onClick={handleDeregister}
          disabled={deregistering}
          className="inline-flex items-center gap-2 px-3 py-1.5 text-sm text-red-600 border border-red-200 rounded-lg hover:bg-red-50 disabled:opacity-50"
        >
          <Trash2 className="w-4 h-4" />
          Deregister
        </button>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Agent Info */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <h2 className="text-sm font-semibold text-gray-900 mb-4">Agent Info</h2>
          <dl className="space-y-3 text-sm">
            <div className="flex justify-between">
              <dt className="text-gray-500">Thing name</dt>
              <dd className="text-gray-900 font-mono text-xs">{thingName}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500">Hostname</dt>
              <dd className="text-gray-900">{r?.hostname ?? "—"}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500">Agent version</dt>
              <dd className="text-gray-900">{r?.agentVersion ? `v${r.agentVersion}` : "—"}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500">Status</dt>
              <dd>{r?.status ? <StatusBadge status={r.status} /> : "—"}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500">Last heartbeat</dt>
              <dd className="text-gray-900">
                {r?.lastHeartbeat
                  ? formatDistanceToNow(new Date(r.lastHeartbeat), { addSuffix: true })
                  : "Never"}
              </dd>
            </div>
            {r?.updateStatus && r.updateStatus !== "idle" && (
              <div className="flex justify-between">
                <dt className="text-gray-500">Update status</dt>
                <dd className="text-gray-900">{r.updateStatus}</dd>
              </div>
            )}
            {r?.deferredVersion && (
              <div className="flex items-start gap-2 mt-2 p-3 bg-amber-50 rounded-lg text-xs text-amber-700">
                <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                Update to v{r.deferredVersion} deferred — {r.deferReason}
              </div>
            )}
          </dl>
        </div>

        {/* GPU */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <h2 className="text-sm font-semibold text-gray-900 mb-4 flex items-center gap-2">
            <Cpu className="w-4 h-4 text-gray-400" /> GPU
          </h2>
          {r?.gpu ? (
            <div className="space-y-4">
              <div>
                <p className="text-base font-medium text-gray-900">{r.gpu.name}</p>
                <p className="text-sm text-gray-500">{(r.gpu.vramMb / 1024).toFixed(0)} GB VRAM</p>
              </div>
              <dl className="grid grid-cols-2 gap-3 text-sm">
                <div>
                  <dt className="text-gray-500 text-xs">CUDA</dt>
                  <dd className="text-gray-900 font-medium">{r.gpu.cudaVersion}</dd>
                </div>
                <div>
                  <dt className="text-gray-500 text-xs">Driver</dt>
                  <dd className="text-gray-900 font-medium">{r.gpu.driverVersion}</dd>
                </div>
              </dl>
              <div className="grid grid-cols-2 gap-3">
                <div className="bg-gray-50 rounded-lg p-3 text-center">
                  <Thermometer className="w-4 h-4 mx-auto mb-1 text-orange-400" />
                  <p className="text-lg font-bold text-gray-900">{r.gpu.temperatureC}°C</p>
                  <p className="text-xs text-gray-500">Temperature</p>
                </div>
                <div className="bg-gray-50 rounded-lg p-3 text-center">
                  <Zap className="w-4 h-4 mx-auto mb-1 text-blue-400" />
                  <p className="text-lg font-bold text-gray-900">{r.gpu.utilizationPercent}%</p>
                  <p className="text-xs text-gray-500">Utilisation</p>
                </div>
              </div>
            </div>
          ) : (
            <p className="text-sm text-gray-400">No GPU data available</p>
          )}
        </div>
      </div>

      {/* Current Job */}
      {r?.currentJobId && (
        <div className="bg-amber-50 border border-amber-200 rounded-xl p-5">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-sm font-semibold text-amber-900 flex items-center gap-2">
              <Play className="w-4 h-4" /> Training in progress
            </h2>
            <Link
              to={`/training/${r.currentJobId}`}
              className="text-xs text-amber-700 hover:text-amber-900 font-medium"
            >
              View job →
            </Link>
          </div>
          <p className="text-xs font-mono text-amber-700 mb-4">{r.currentJobId}</p>
          {progress && (
            <div className="space-y-3">
              <div>
                <div className="flex justify-between text-xs text-amber-700 mb-1">
                  <span>Epoch {progress.epoch} / {progress.total_epochs}</span>
                  <span>{Math.round((progress.epoch / progress.total_epochs) * 100)}%</span>
                </div>
                <div className="w-full bg-amber-100 rounded-full h-2">
                  <div
                    className="bg-amber-500 h-2 rounded-full transition-all duration-500"
                    style={{ width: `${(progress.epoch / progress.total_epochs) * 100}%` }}
                  />
                </div>
              </div>
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-xs">
                {progress.mAP50 != null && (
                  <div className="bg-white rounded-lg p-2 text-center border border-amber-100">
                    <p className="font-bold text-gray-900">{progress.mAP50.toFixed(3)}</p>
                    <p className="text-gray-500">mAP50</p>
                  </div>
                )}
                {progress.train_loss != null && (
                  <div className="bg-white rounded-lg p-2 text-center border border-amber-100">
                    <p className="font-bold text-gray-900">{progress.train_loss.toFixed(4)}</p>
                    <p className="text-gray-500">Train loss</p>
                  </div>
                )}
                {progress.elapsed_seconds != null && (
                  <div className="bg-white rounded-lg p-2 text-center border border-amber-100">
                    <p className="font-bold text-gray-900">{formatDuration(progress.elapsed_seconds)}</p>
                    <p className="text-gray-500">Elapsed</p>
                  </div>
                )}
                {progress.eta_seconds != null && (
                  <div className="bg-white rounded-lg p-2 text-center border border-amber-100">
                    <p className="font-bold text-gray-900">{formatDuration(progress.eta_seconds)}</p>
                    <p className="text-gray-500">ETA</p>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}

      {/* Job History */}
      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="px-5 py-4 border-b border-gray-100">
          <h2 className="text-sm font-semibold text-gray-900">Job History</h2>
        </div>
        {jobs.length === 0 ? (
          <p className="p-5 text-sm text-gray-400">No jobs run on this agent yet</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-gray-500 border-b border-gray-100">
                <th className="px-5 py-2 font-medium">Job ID</th>
                <th className="px-5 py-2 font-medium">Status</th>
                <th className="px-5 py-2 font-medium">Dataset</th>
                <th className="px-5 py-2 font-medium">Epochs</th>
                <th className="px-5 py-2 font-medium">Created</th>
                <th className="px-5 py-2 font-medium">Completed</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.jobId} className="border-b border-gray-50 hover:bg-gray-50">
                  <td className="px-5 py-3">
                    <Link to={`/training/${job.jobId}`} className="text-blue-600 hover:text-blue-700 font-mono text-xs">
                      {job.jobId.slice(0, 8)}…
                    </Link>
                  </td>
                  <td className="px-5 py-3"><JobStatusBadge status={job.status} /></td>
                  <td className="px-5 py-3 text-gray-500 font-mono text-xs">{job.exportId?.slice(0, 8) ?? "—"}</td>
                  <td className="px-5 py-3 text-gray-500">{job.epochs ?? "—"}</td>
                  <td className="px-5 py-3 text-gray-400 text-xs">
                    {job.createdAt ? format(new Date(job.createdAt), "dd MMM HH:mm") : "—"}
                  </td>
                  <td className="px-5 py-3 text-gray-400 text-xs">
                    {job.completedAt ? formatDistanceToNow(new Date(job.completedAt), { addSuffix: true }) : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
