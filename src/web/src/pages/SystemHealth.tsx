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
  const [updating, setUpdating] = useState<Record<string, boolean>>({});
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

  const handleUpdate = async (thingName: string) => {
    setUpdating((prev) => ({ ...prev, [thingName]: true }));
    setUpdateMessage(null);
    try {
      const result = await api.triggerPiUpdate(thingName);
      setUpdateMessage(`Update to v${result.version} triggered for ${thingName}`);
      // Poll for status changes
      setTimeout(refreshHealth, 5000);
      setTimeout(refreshHealth, 15000);
      setTimeout(refreshHealth, 30000);
    } catch (e) {
      setUpdateMessage(`Update failed: ${(e as Error).message}`);
    } finally {
      setUpdating((prev) => ({ ...prev, [thingName]: false }));
    }
  };

  const handleUpdateAll = async () => {
    setUpdating((prev) => ({ ...prev, all: true }));
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
      setUpdating((prev) => ({ ...prev, all: false }));
    }
  };

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
    );
  }

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
          {anyUpdateAvailable && (
            <button
              onClick={handleUpdateAll}
              disabled={updating.all}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <RefreshCw className={`w-3 h-3 ${updating.all ? "animate-spin" : ""}`} />
              {updating.all ? "Updating..." : "Update All"}
            </button>
          )}
        </div>
      </div>

      {updateMessage && (
        <div className="mb-4 p-3 bg-blue-50 text-blue-700 rounded-lg text-sm">
          {updateMessage}
        </div>
      )}

      {/* API Health */}
      <div className="bg-white rounded-xl border border-gray-200 p-5 mb-4">
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

      {/* Pi Devices */}
      <h2 className="text-lg font-semibold text-gray-900 mb-3">Raspberry Pi Devices</h2>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {health.devices.map((device) => (
          <div
            key={device.thingName}
            className="bg-white rounded-xl border border-gray-200 p-5"
          >
            <div className="flex items-center justify-between mb-3">
              <div className="flex items-center gap-2">
                {device.online ? (
                  <Wifi className="w-5 h-5 text-green-500" />
                ) : (
                  <WifiOff className="w-5 h-5 text-red-500" />
                )}
                <span className="font-medium text-gray-900">
                  {device.hostname || device.thingName}
                </span>
              </div>
              <span
                className={`w-2 h-2 rounded-full ${device.online ? "bg-green-500" : "bg-red-500"}`}
              />
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-sm text-gray-500">Status</span>
                <StatusBadge ok={device.online} label={device.online ? "Online" : "Offline"} />
              </div>

              <div className="flex items-center justify-between">
                <span className="text-sm text-gray-500">Version</span>
                <span className="text-sm font-medium text-gray-900">
                  {device.version ? `v${device.version}` : "Unknown"}
                </span>
              </div>

              {device.lastHeartbeat && (
                <div className="flex items-center justify-between">
                  <span className="text-sm text-gray-500">Last seen</span>
                  <span className="text-xs text-gray-400">
                    {formatDistanceToNow(new Date(device.lastHeartbeat), { addSuffix: true })}
                  </span>
                </div>
              )}

              {device.updateStatus && device.updateStatus !== "idle" && (
                <div className="flex items-center justify-between">
                  <span className="text-sm text-gray-500">Update Status</span>
                  <span
                    className={`text-xs font-medium ${
                      device.updateStatus === "success"
                        ? "text-green-600"
                        : device.updateStatus === "failed"
                        ? "text-red-600"
                        : "text-blue-600"
                    }`}
                  >
                    {device.updateStatus}
                  </span>
                </div>
              )}
            </div>

            {device.updateAvailable && (
              <div className="mt-4 pt-4 border-t border-gray-100">
                <div className="flex items-center justify-between mb-2">
                  <span className="text-xs text-amber-600">
                    v{health.latestVersion} available
                  </span>
                  <button
                    onClick={() => handleUpdate(device.thingName)}
                    disabled={updating[device.thingName] || device.updateStatus === "updating"}
                    className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <RefreshCw
                      className={`w-3 h-3 ${
                        updating[device.thingName] ? "animate-spin" : ""
                      }`}
                    />
                    {updating[device.thingName] ? "Updating..." : "Update"}
                  </button>
                </div>
              </div>
            )}

            {!device.updateAvailable && device.version && (
              <p className="text-xs text-green-600 mt-3 pt-3 border-t border-gray-100">
                Up to date
              </p>
            )}
          </div>
        ))}

        {health.devices.length === 0 && (
          <div className="col-span-2 bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
            <WifiOff className="w-12 h-12 text-gray-300 mx-auto mb-3" />
            <p className="text-gray-500">No Pi devices registered</p>
          </div>
        )}
      </div>
    </div>
  );
}
