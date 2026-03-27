import { useEffect, useState } from "react";
import { formatDistanceToNow } from "date-fns";
import {
  Wifi,
  WifiOff,
  Server,
  Package,
  RefreshCw,
} from "lucide-react";
import { api } from "../api";
import type { SystemHealth } from "../types";

function StatusBadge({ ok, label }: { ok: boolean; label: string }) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium ${
        ok ? "bg-green-50 text-green-700" : "bg-red-50 text-red-700"
      }`}
    >
      <span
        className={`w-1.5 h-1.5 rounded-full ${ok ? "bg-green-500" : "bg-red-500"}`}
      />
      {label}
    </span>
  );
}

export default function SystemHealthPage() {
  const [health, setHealth] = useState<SystemHealth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [updating, setUpdating] = useState(false);
  const [updateMessage, setUpdateMessage] = useState<string | null>(null);

  const refreshHealth = () =>
    api.getHealth().then(setHealth).catch(console.error);

  useEffect(() => {
    api
      .getHealth()
      .then(setHealth)
      .catch((e: Error) => setError(e.message));

    const interval = setInterval(refreshHealth, 30_000);
    return () => clearInterval(interval);
  }, []);

  const handleUpdate = async () => {
    setUpdating(true);
    setUpdateMessage(null);
    try {
      const result = await api.triggerPiUpdate();
      setUpdateMessage(`Update to v${result.version} triggered`);
      // Poll for status changes
      setTimeout(refreshHealth, 5000);
      setTimeout(refreshHealth, 15000);
      setTimeout(refreshHealth, 30000);
    } catch (e) {
      setUpdateMessage(`Update failed: ${(e as Error).message}`);
    } finally {
      setUpdating(false);
    }
  };

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
    );
  }

  if (!health) return <div className="text-gray-400">Loading...</div>;

  const metrics = [
    {
      icon: health.piOnline ? Wifi : WifiOff,
      label: "Pi Status",
      value: health.piOnline ? "Online" : "Offline",
      sub: `Checked ${formatDistanceToNow(new Date(health.checkedAt), { addSuffix: true })}`,
      ok: health.piOnline,
    },
    {
      icon: Server,
      label: "API",
      value: "Healthy",
      sub: "Responding normally",
      ok: true,
    },
  ];

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">System Health</h1>
        <StatusBadge
          ok={health.piOnline}
          label={health.piOnline ? "All Systems Go" : "Pi Offline"}
        />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        {metrics.map((m) => (
          <div
            key={m.label}
            className="bg-white rounded-xl border border-gray-200 p-5"
          >
            <div className="flex items-center justify-between mb-3">
              <div className="flex items-center gap-2">
                <m.icon className="w-5 h-5 text-gray-400" />
                <span className="text-sm text-gray-500">{m.label}</span>
              </div>
              <span
                className={`w-2 h-2 rounded-full ${m.ok ? "bg-green-500" : "bg-red-500"}`}
              />
            </div>
            <p className="text-2xl font-bold text-gray-900">{m.value}</p>
            {m.sub && <p className="text-xs text-gray-400 mt-1">{m.sub}</p>}
          </div>
        ))}

        {/* Pi Software Version */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-2">
              <Package className="w-5 h-5 text-gray-400" />
              <span className="text-sm text-gray-500">Pi Software</span>
            </div>
            {health.updateAvailable && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-amber-50 text-amber-700">
                Update Available
              </span>
            )}
          </div>
          <p className="text-2xl font-bold text-gray-900">
            {health.piVersion ? `v${health.piVersion}` : "Unknown"}
          </p>
          {health.latestVersion && health.piVersion !== health.latestVersion && (
            <p className="text-xs text-amber-600 mt-1">
              v{health.latestVersion} available
            </p>
          )}
          {health.piVersion && !health.updateAvailable && (
            <p className="text-xs text-green-600 mt-1">Up to date</p>
          )}
          {health.updateStatus && health.updateStatus !== "idle" && (
            <p className={`text-xs mt-1 ${
              health.updateStatus === "success" ? "text-green-600" :
              health.updateStatus === "failed" ? "text-red-600" :
              "text-blue-600"
            }`}>
              Status: {health.updateStatus}
            </p>
          )}

          {health.updateAvailable && (
            <button
              onClick={handleUpdate}
              disabled={updating || health.updateStatus === "updating"}
              className="mt-3 inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <RefreshCw className={`w-3 h-3 ${updating ? "animate-spin" : ""}`} />
              {updating ? "Triggering..." : "Update Now"}
            </button>
          )}

          {updateMessage && (
            <p className="text-xs text-gray-500 mt-2">{updateMessage}</p>
          )}
        </div>
      </div>
    </div>
  );
}
