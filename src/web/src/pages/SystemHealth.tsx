import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import { Server, WifiOff, Plus, RefreshCw, ChevronRight, Wifi, AlertTriangle, Inbox } from "lucide-react";
import { api } from "../api";
import type { SystemHealth } from "../types";
import StatusBadge from "../components/health/StatusBadge";
import AddDeviceDialog from "../components/health/AddDeviceDialog";

export default function SystemHealthPage() {
  const [health, setHealth] = useState<SystemHealth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [updating, setUpdating] = useState(false);
  const [updateMessage, setUpdateMessage] = useState<string | null>(null);
  const [showAddDialog, setShowAddDialog] = useState(false);
  const [queueStats, setQueueStats] = useState<{ name: string; pending: number; inFlight: number; dlqPending: number }[]>([]);

  const refreshHealth = () => {
    Promise.all([api.getHealth(), api.getDevices()])
      .then(([h, d]) => {
        const deviceMap = new Map(d.devices.map((dev) => [dev.thingName, dev]));
        setHealth({
          ...h,
          devices: h.devices.map((dev) => ({ ...dev, ...deviceMap.get(dev.thingName) })),
        });
      })
      .catch(console.error);
    api.getQueueStats().then((d) => setQueueStats(d.queues)).catch(console.error);
  };

  useEffect(() => {
    Promise.all([api.getHealth(), api.getDevices()])
      .then(([h, d]) => {
        const deviceMap = new Map(d.devices.map((dev) => [dev.thingName, dev]));
        setHealth({
          ...h,
          devices: h.devices.map((dev) => ({ ...dev, ...deviceMap.get(dev.thingName) })),
        });
      })
      .catch((e: Error) => setError(e.message));
    api.getQueueStats().then((d) => setQueueStats(d.queues)).catch(console.error);

    const interval = setInterval(refreshHealth, 10_000);
    return () => clearInterval(interval);
  }, []);

  const handleUpdateAll = async () => {
    setUpdating(true);
    setUpdateMessage(null);
    try {
      const result = await api.triggerPiUpdateAll();
      setUpdateMessage(`Update to v${result.version} triggered for ${result.deviceCount} device(s)`);
      setTimeout(refreshHealth, 5000);
      setTimeout(refreshHealth, 15000);
      setTimeout(refreshHealth, 30000);
    } catch (e) {
      setUpdateMessage(`Update failed: ${(e as Error).message}`);
    } finally {
      setUpdating(false);
    }
  };

  if (error) return <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>;
  if (!health) return <div className="text-gray-400">Loading...</div>;

  const allOnline = health.devices.every((d) => d.online);
  const anyUpdateAvailable = health.devices.some((d) => d.updateAvailable);

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">System Health</h1>
        <div className="flex items-center gap-3">
          <StatusBadge
            ok={allOnline}
            label={allOnline ? "All Systems Go" : `${health.devices.filter((d) => !d.online).length} Offline`}
          />
          <button
            onClick={() => setShowAddDialog(true)}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg"
          >
            <Plus className="w-3 h-3" /> Add Pi
          </button>
          {anyUpdateAvailable && (
            <button
              onClick={handleUpdateAll}
              disabled={updating}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50"
            >
              <RefreshCw className={`w-3 h-3 ${updating ? "animate-spin" : ""}`} />
              {updating ? "Updating..." : "Update All"}
            </button>
          )}
        </div>
      </div>

      {updateMessage && (
        <div className="mb-4 p-3 bg-blue-50 text-blue-700 rounded-lg text-sm">{updateMessage}</div>
      )}

      {/* API Health */}
      <div className="bg-white rounded-xl border border-gray-200 p-5 mb-6">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Server className="w-5 h-5 text-gray-400" />
            <span className="text-sm text-gray-500">API</span>
          </div>
          <span className="w-2 h-2 rounded-full bg-green-500" />
        </div>
        <p className="text-2xl font-bold text-gray-900 mt-2">Healthy</p>
        <p className="text-xs text-gray-400 mt-1">
          Checked {formatDistanceToNow(new Date(health.checkedAt), { addSuffix: true })}
        </p>
      </div>

      {/* Queue Stats */}
      {queueStats.length > 0 && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 mb-6">
          <div className="flex items-center gap-2 mb-3">
            <Inbox className="w-5 h-5 text-gray-400" />
            <span className="text-sm font-semibold text-gray-900">Processing Queues</span>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            {queueStats.map((q) => (
              <div key={q.name} className="flex items-center justify-between px-3 py-2 border border-gray-100 rounded-lg">
                <span className="text-sm text-gray-700">{q.name}</span>
                <div className="flex items-center gap-2">
                  <span className={`text-xs px-1.5 py-0.5 rounded-full font-medium ${
                    q.pending === 0 ? "bg-green-100 text-green-700" : "bg-amber-100 text-amber-700"
                  }`}>
                    {q.pending} pending
                  </span>
                  {q.inFlight > 0 && (
                    <span className="text-xs px-1.5 py-0.5 rounded-full font-medium bg-blue-100 text-blue-700">
                      {q.inFlight} in-flight
                    </span>
                  )}
                  {q.dlqPending > 0 && (
                    <span className="text-xs px-1.5 py-0.5 rounded-full font-medium bg-red-100 text-red-700">
                      {q.dlqPending} failed
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Device Table */}
      <h2 className="text-lg font-semibold text-gray-900 mb-3">Raspberry Pi Devices</h2>

      {health.devices.length === 0 ? (
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <WifiOff className="w-12 h-12 text-gray-300 mx-auto mb-3" />
          <p className="text-gray-500">No Pi devices registered</p>
        </div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 divide-y divide-gray-100">
          {health.devices.map((device) => (
            <Link
              key={device.thingName}
              to={`/device/${device.thingName}`}
              className="flex items-center gap-4 px-5 py-4 hover:bg-gray-50 transition-colors"
            >
              {device.online ? (
                <Wifi className="w-5 h-5 text-green-500 shrink-0" />
              ) : (
                <WifiOff className="w-5 h-5 text-red-400 shrink-0" />
              )}
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-gray-900 truncate">
                  {device.hostname || device.thingName}
                </p>
                <p className="text-xs text-gray-400">{device.thingName}</p>
              </div>
              <StatusBadge ok={device.online} label={device.online ? "Online" : "Offline"} />
              <span className={`text-sm font-medium w-16 text-right ${device.updateAvailable ? "text-amber-600" : "text-gray-600"}`}>
                {device.version ? `v${device.version}` : "—"}
                {device.updateAvailable && " *"}
              </span>
              <span className="text-xs text-gray-400 w-28 text-right">
                {device.lastHeartbeat
                  ? formatDistanceToNow(new Date(device.lastHeartbeat), { addSuffix: true })
                  : "Never"}
              </span>
              {device.deviceTime && Math.abs(Date.now() - new Date(device.deviceTime).getTime()) > 60000 ? (
                <span className="shrink-0" aria-label="Clock out of sync"><AlertTriangle className="w-4 h-4 text-red-500" /></span>
              ) : (
                <ChevronRight className="w-4 h-4 text-gray-300 shrink-0" />
              )}
            </Link>
          ))}
        </div>
      )}

      <AddDeviceDialog
        open={showAddDialog}
        onClose={() => setShowAddDialog(false)}
        onDeviceAdded={() => setTimeout(refreshHealth, 2000)}
        onError={(msg) => setUpdateMessage(msg)}
      />
    </div>
  );
}
