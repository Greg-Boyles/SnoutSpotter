import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Cpu, Plus, Loader2, CheckCircle, XCircle, Clock, Play, Thermometer, Zap } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";

type TrainingJob = {
  jobId: string;
  status: string;
  agentThingName: string | null;
  exportId: string | null;
  epochs: number | null;
  createdAt: string | null;
  completedAt: string | null;
};

type TrainerAgent = {
  thingName: string;
  online: boolean;
  version: string | null;
  hostname: string | null;
  lastHeartbeat: string | null;
  currentJobId: string | null;
  status?: string | null;
  gpu?: { name: string; vramMb: number; temperatureC: number; utilizationPercent: number } | null;
  currentJobProgress?: { epoch: number; total_epochs: number; mAP50?: number } | null;
};

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    pending: "bg-gray-100 text-gray-600",
    downloading: "bg-blue-100 text-blue-700",
    training: "bg-amber-100 text-amber-700",
    uploading: "bg-blue-100 text-blue-700",
    complete: "bg-green-100 text-green-700",
    failed: "bg-red-100 text-red-700",
    cancelled: "bg-gray-100 text-gray-600",
    cancelling: "bg-amber-100 text-amber-700",
    interrupted: "bg-amber-100 text-amber-700",
  };
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? "bg-gray-100 text-gray-600"}`}>
      {status === "training" && <Play className="w-3 h-3" />}
      {status === "complete" && <CheckCircle className="w-3 h-3" />}
      {status === "failed" && <XCircle className="w-3 h-3" />}
      {status === "pending" && <Clock className="w-3 h-3" />}
      {status}
    </span>
  );
}

export default function TrainingJobs() {
  const [jobs, setJobs] = useState<TrainingJob[]>([]);
  const [agents, setAgents] = useState<TrainerAgent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api.listTrainingJobs(), api.listTrainingAgents()])
      .then(([j, a]) => {
        setJobs(j.jobs);
        setAgents(a.agents);
      })
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));

    const interval = setInterval(() => {
      api.listTrainingJobs().then((j) => setJobs(j.jobs)).catch(console.error);
      api.listTrainingAgents().then((a) => setAgents(a.agents)).catch(console.error);
    }, 10_000);
    return () => clearInterval(interval);
  }, []);

  if (error) return <div className="text-red-600 bg-red-50 p-4 rounded-lg">Failed to load: {error}</div>;

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Training</h1>
        <Link
          to="/training/new"
          className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg"
        >
          <Plus className="w-4 h-4" /> New Job
        </Link>
      </div>

      {/* Agents */}
      <div className="bg-white rounded-xl border border-gray-200 p-5 mb-6">
        <h2 className="text-sm font-semibold text-gray-900 mb-3">Training Agents</h2>
        {loading ? (
          <div className="flex items-center gap-2 text-gray-400 text-sm">
            <Loader2 className="w-4 h-4 animate-spin" /> Loading...
          </div>
        ) : agents.length === 0 ? (
          <p className="text-sm text-gray-400">No training agents registered</p>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
            {agents.map((agent) => {
              const progress = agent.currentJobProgress;
              return (
                <Link
                  key={agent.thingName}
                  to={`/training/agents/${agent.thingName}`}
                  className="block border border-gray-200 rounded-xl p-4 hover:border-blue-300 hover:bg-blue-50/30 transition-colors"
                >
                  {/* Header row */}
                  <div className="flex items-center justify-between mb-3">
                    <div className="flex items-center gap-2">
                      <span className={`w-2 h-2 rounded-full shrink-0 ${agent.online ? "bg-green-500 animate-pulse" : "bg-gray-300"}`} />
                      <span className="text-sm font-medium text-gray-900 truncate">
                        {agent.hostname || agent.thingName.replace("snoutspotter-trainer-", "")}
                      </span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      {agent.version && (
                        <span className="text-xs text-gray-400">v{agent.version}</span>
                      )}
                      {agent.status === "training" ? (
                        <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full text-xs font-medium bg-amber-100 text-amber-700">
                          <Play className="w-3 h-3" /> training
                        </span>
                      ) : (
                        <span className={`text-xs ${agent.online ? "text-green-600" : "text-gray-400"}`}>
                          {agent.online ? "idle" : "offline"}
                        </span>
                      )}
                    </div>
                  </div>

                  {/* GPU row */}
                  {agent.gpu && (
                    <div className="flex items-center justify-between text-xs text-gray-500 mb-3">
                      <div className="flex items-center gap-1">
                        <Cpu className="w-3 h-3" />
                        <span className="truncate">{agent.gpu.name}</span>
                        <span className="text-gray-400 shrink-0">{(agent.gpu.vramMb / 1024).toFixed(0)}GB</span>
                      </div>
                      <div className="flex items-center gap-2 shrink-0">
                        <span className="flex items-center gap-0.5">
                          <Thermometer className="w-3 h-3 text-orange-400" />
                          {agent.gpu.temperatureC}°
                        </span>
                        <span className="flex items-center gap-0.5">
                          <Zap className="w-3 h-3 text-blue-400" />
                          {agent.gpu.utilizationPercent}%
                        </span>
                      </div>
                    </div>
                  )}

                  {/* Progress bar (if training) */}
                  {agent.currentJobId && progress ? (
                    <div>
                      <div className="flex justify-between text-xs text-gray-500 mb-1">
                        <span>Epoch {progress.epoch}/{progress.total_epochs}</span>
                        {progress.mAP50 != null && <span>mAP50 {progress.mAP50.toFixed(3)}</span>}
                      </div>
                      <div className="w-full bg-gray-100 rounded-full h-1.5">
                        <div
                          className="bg-amber-500 h-1.5 rounded-full transition-all duration-500"
                          style={{ width: `${(progress.epoch / progress.total_epochs) * 100}%` }}
                        />
                      </div>
                    </div>
                  ) : (
                    <p className="text-xs text-gray-400">
                      {agent.lastHeartbeat
                        ? `Last seen ${formatDistanceToNow(new Date(agent.lastHeartbeat), { addSuffix: true })}`
                        : "Never connected"}
                    </p>
                  )}
                </Link>
              );
            })}
          </div>
        )}
      </div>

      {/* Jobs */}
      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="px-5 py-4 border-b border-gray-100">
          <h2 className="text-sm font-semibold text-gray-900">Training Jobs</h2>
        </div>
        {loading ? (
          <div className="p-5 animate-pulse space-y-3">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="h-10 bg-gray-100 rounded" />
            ))}
          </div>
        ) : jobs.length === 0 ? (
          <div className="p-8 text-center">
            <Cpu className="w-8 h-8 text-gray-300 mx-auto mb-2" />
            <p className="text-sm text-gray-400">No training jobs yet</p>
            <Link to="/training/new" className="text-sm text-blue-600 hover:text-blue-700 font-medium">
              Submit your first job
            </Link>
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-gray-500 border-b border-gray-100">
                <th className="px-5 py-2 font-medium">Job ID</th>
                <th className="px-5 py-2 font-medium">Status</th>
                <th className="px-5 py-2 font-medium">Dataset</th>
                <th className="px-5 py-2 font-medium">Epochs</th>
                <th className="px-5 py-2 font-medium">Agent</th>
                <th className="px-5 py-2 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.jobId} className="border-b border-gray-50 hover:bg-gray-50">
                  <td className="px-5 py-3">
                    <Link to={`/training/${job.jobId}`} className="text-blue-600 hover:text-blue-700 font-medium">
                      {job.jobId}
                    </Link>
                  </td>
                  <td className="px-5 py-3"><StatusBadge status={job.status} /></td>
                  <td className="px-5 py-3 text-gray-500">{job.exportId || "—"}</td>
                  <td className="px-5 py-3 text-gray-500">{job.epochs ?? "—"}</td>
                  <td className="px-5 py-3 text-gray-500">{job.agentThingName?.replace("snoutspotter-trainer-", "") || "—"}</td>
                  <td className="px-5 py-3 text-gray-400">
                    {job.createdAt ? formatDistanceToNow(new Date(job.createdAt), { addSuffix: true }) : "—"}
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
