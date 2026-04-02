import { useEffect, useState } from "react";
import { Link, useParams, useNavigate } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import {
  Wifi, WifiOff, ArrowLeft, Trash2, RefreshCw, Camera, CameraOff, Upload,
  HardDrive, Cpu, Thermometer, Settings, FileText, RotateCw, Power,
  FolderX, Terminal, Check, X,
} from "lucide-react";
import { api } from "../api";
import type { SystemHealth, PiDevice } from "../types";
import StatusBadge from "../components/health/StatusBadge";
import UsageBar from "../components/health/UsageBar";
import { formatUptime } from "../components/health/formatUptime";

export default function DeviceDetail() {
  const { thingName } = useParams<{ thingName: string }>();
  const navigate = useNavigate();
  const [health, setHealth] = useState<SystemHealth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [updating, setUpdating] = useState(false);
  const [updateMessage, setUpdateMessage] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const [commandState, setCommandState] = useState<Record<string, { status: string; message?: string }>>({});
  const [confirmAction, setConfirmAction] = useState<string | null>(null);

  const DESTRUCTIVE_ACTIONS = new Set(["reboot", "clear-clips", "clear-backups"]);

  const refreshHealth = () =>
    Promise.all([api.getHealth(), api.getDevices()])
      .then(([h, d]) => {
        const deviceMap = new Map(d.devices.map((dev) => [dev.thingName, dev]));
        setHealth({
          ...h,
          devices: h.devices.map((dev) => ({ ...dev, ...deviceMap.get(dev.thingName) })),
        });
      })
      .catch(console.error);

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

    const interval = setInterval(refreshHealth, 10_000);
    return () => clearInterval(interval);
  }, []);

  const requestCommand = (action: string) => {
    if (DESTRUCTIVE_ACTIONS.has(action)) {
      setConfirmAction(action);
    } else {
      sendCommand(action);
    }
  };

  const sendCommand = async (action: string) => {
    if (!thingName) return;
    setConfirmAction(null);
    setCommandState((prev) => ({ ...prev, [action]: { status: "running" } }));
    try {
      const { commandId } = await api.sendCommand(thingName, action);
      for (let i = 0; i < 10; i++) {
        await new Promise((r) => setTimeout(r, i < 3 ? 2000 : 5000));
        const result = await api.getCommandResult(thingName, commandId);
        if (result.status !== "pending") {
          const isSuccess = result.status === "success";
          setCommandState((prev) => ({
            ...prev,
            [action]: { status: isSuccess ? "success" : "failed", message: result.message || result.error },
          }));
          refreshHealth();
          setTimeout(() => setCommandState((prev) => { const n = { ...prev }; delete n[action]; return n; }), isSuccess ? 3000 : 5000);
          return;
        }
      }
      setCommandState((prev) => ({ ...prev, [action]: { status: "failed", message: "Timed out" } }));
      setTimeout(() => setCommandState((prev) => { const n = { ...prev }; delete n[action]; return n; }), 5000);
    } catch (e) {
      setCommandState((prev) => ({ ...prev, [action]: { status: "failed", message: (e as Error).message } }));
      setTimeout(() => setCommandState((prev) => { const n = { ...prev }; delete n[action]; return n; }), 5000);
    }
  };

  const handleUpdate = async () => {
    if (!thingName) return;
    setUpdating(true);
    setUpdateMessage(null);
    try {
      const result = await api.triggerPiUpdate(thingName);
      setUpdateMessage(`Update to v${result.version} triggered`);
      setTimeout(refreshHealth, 5000);
      setTimeout(refreshHealth, 15000);
      setTimeout(refreshHealth, 30000);
    } catch (e) {
      setUpdateMessage(`Update failed: ${(e as Error).message}`);
    } finally {
      setUpdating(false);
    }
  };

  const handleRemove = async () => {
    if (!thingName || !confirm(`Are you sure you want to remove "${thingName}"? This cannot be undone.`)) return;
    setRemoving(true);
    try {
      await api.deregisterDevice(thingName);
      navigate("/health");
    } catch (e) {
      setUpdateMessage(`Failed to remove device: ${(e as Error).message}`);
      setRemoving(false);
    }
  };

  if (error) return <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>;
  if (!health) return <div className="text-gray-400">Loading...</div>;

  const device: PiDevice | undefined = health.devices.find((d) => d.thingName === thingName);

  if (!device) {
    return (
      <div>
        <Link to="/health" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
          <ArrowLeft className="w-4 h-4" /> System Health
        </Link>
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <p className="text-gray-500">Device "{thingName}" not found</p>
        </div>
      </div>
    );
  }

  return (
    <div className="max-w-3xl">
      {/* Back nav */}
      <Link to="/health" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> System Health
      </Link>

      {/* Device header */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-3">
          {device.online ? <Wifi className="w-6 h-6 text-green-500" /> : <WifiOff className="w-6 h-6 text-red-500" />}
          <h1 className="text-2xl font-bold text-gray-900">{device.hostname || device.thingName}</h1>
          <StatusBadge ok={device.online} label={device.online ? "Online" : "Offline"} />
          {device.version && (
            <span className="text-sm font-medium text-gray-500 bg-gray-100 px-2 py-0.5 rounded">v{device.version}</span>
          )}
        </div>
        <button
          onClick={handleRemove}
          disabled={removing}
          className="p-2 text-gray-400 hover:text-red-600 disabled:opacity-50"
          title="Remove device"
        >
          <Trash2 className="w-5 h-5" />
        </button>
      </div>

      {updateMessage && (
        <div className="mb-4 p-3 bg-blue-50 text-blue-700 rounded-lg text-sm">{updateMessage}</div>
      )}

      {/* Quick links */}
      <div className="flex items-center gap-3 mb-6">
        {[
          { to: `/device/${thingName}/config`, icon: Settings, label: "Settings" },
          { to: `/device/${thingName}/logs`, icon: FileText, label: "Logs" },
          { to: `/device/${thingName}/commands`, icon: Terminal, label: "Commands" },
        ].map(({ to, icon: Icon, label }) => (
          <Link
            key={to}
            to={to}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-gray-600 bg-white border border-gray-200 hover:bg-gray-50 rounded-lg"
          >
            <Icon className="w-3.5 h-3.5" /> {label}
          </Link>
        ))}
      </div>

      <div className="space-y-4">
        {/* Status & Update */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <h3 className="text-sm font-semibold text-gray-900 mb-3">Status</h3>
          <div className="space-y-2">
            {device.lastHeartbeat && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-gray-500">Last seen</span>
                <span className="text-sm text-gray-700">
                  {formatDistanceToNow(new Date(device.lastHeartbeat), { addSuffix: true })}
                </span>
              </div>
            )}
            {device.updateStatus && device.updateStatus !== "idle" && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-gray-500">Update Status</span>
                <span className={`text-sm font-medium ${
                  device.updateStatus === "success" ? "text-green-600" :
                  device.updateStatus === "failed" ? "text-red-600" : "text-blue-600"
                }`}>
                  {device.updateStatus}
                </span>
              </div>
            )}
          </div>
          {device.updateAvailable && (
            <div className="flex items-center justify-between mt-3 pt-3 border-t border-gray-100">
              <span className="text-sm text-amber-600">v{health.latestVersion} available</span>
              <button
                onClick={handleUpdate}
                disabled={updating || device.updateStatus === "updating"}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50"
              >
                <RefreshCw className={`w-3 h-3 ${updating ? "animate-spin" : ""}`} />
                {updating ? "Updating..." : "Update"}
              </button>
            </div>
          )}
          {!device.updateAvailable && device.version && (
            <p className="text-xs text-green-600 mt-3 pt-3 border-t border-gray-100">Up to date</p>
          )}
        </div>

        {/* Services */}
        {device.services && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Services</h3>
            <div className="flex items-center flex-wrap gap-2">
              {Object.entries(device.services).map(([name, status]) => {
                const cmdState = commandState[`restart-${name}`];
                return (
                  <span
                    key={name}
                    className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-medium ${
                      status === "active" ? "bg-green-50 text-green-700" : "bg-red-50 text-red-700"
                    }`}
                  >
                    <span className={`w-1.5 h-1.5 rounded-full ${status === "active" ? "bg-green-500" : "bg-red-500"}`} />
                    {name}
                    {cmdState?.status === "success" ? (
                      <Check className="w-3 h-3 text-green-600" />
                    ) : cmdState?.status === "failed" ? (
                      <X className="w-3 h-3 text-red-600" />
                    ) : (
                      <button
                        onClick={() => requestCommand(`restart-${name}`)}
                        disabled={cmdState?.status === "running"}
                        className="p-0.5 rounded hover:bg-black hover:bg-opacity-10 disabled:opacity-50"
                        title={`Restart ${name}`}
                      >
                        <RotateCw className={`w-3 h-3 ${cmdState?.status === "running" ? "animate-spin" : ""}`} />
                      </button>
                    )}
                  </span>
                );
              })}
            </div>
          </div>
        )}

        {/* Camera */}
        {device.camera && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <div className="flex items-center gap-2 mb-3">
              {device.camera.healthy ? <Camera className="w-4 h-4 text-green-500" /> : <CameraOff className="w-4 h-4 text-red-500" />}
              <h3 className="text-sm font-semibold text-gray-900">Camera</h3>
              <StatusBadge
                ok={device.camera.healthy}
                label={device.camera.connected ? (device.camera.healthy ? "Healthy" : "Error") : "Disconnected"}
              />
            </div>
            <div className="grid grid-cols-3 gap-4 text-xs text-gray-500">
              {device.camera.sensor && <span>Sensor: <span className="text-gray-700">{device.camera.sensor}</span></span>}
              {device.camera.resolution && <span>Native: <span className="text-gray-700">{device.camera.resolution}</span></span>}
              {device.camera.recordResolution && <span>Record: <span className="text-gray-700">{device.camera.recordResolution}</span></span>}
            </div>
          </div>
        )}

        {/* Activity */}
        {(device.lastMotionAt || device.lastUploadAt || device.uploadStats) && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <div className="flex items-center gap-2 mb-3">
              <Upload className="w-4 h-4 text-gray-400" />
              <h3 className="text-sm font-semibold text-gray-900">Activity</h3>
            </div>
            <div className="space-y-2">
              {device.lastMotionAt && (
                <div className="flex items-center justify-between text-sm">
                  <span className="text-gray-500">Last motion</span>
                  <span className="text-gray-700">{formatDistanceToNow(new Date(device.lastMotionAt), { addSuffix: true })}</span>
                </div>
              )}
              {device.lastUploadAt && (
                <div className="flex items-center justify-between text-sm">
                  <span className="text-gray-500">Last upload</span>
                  <span className="text-gray-700">{formatDistanceToNow(new Date(device.lastUploadAt), { addSuffix: true })}</span>
                </div>
              )}
              {device.uploadStats && (
                <div className="flex items-center gap-4 text-sm text-gray-500 pt-2 border-t border-gray-100">
                  <span>{device.uploadStats.uploadsToday} today</span>
                  {device.uploadStats.failedToday > 0 && (
                    <span className="text-red-600">{device.uploadStats.failedToday} failed</span>
                  )}
                  <span>{device.uploadStats.totalUploaded} total</span>
                </div>
              )}
              {device.clipsPending != null && device.clipsPending > 0 && (
                <span className="text-sm text-amber-600 block">
                  {device.clipsPending} clip{device.clipsPending !== 1 ? "s" : ""} pending upload
                </span>
              )}
            </div>
          </div>
        )}

        {/* System Health */}
        {device.system && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <div className="flex items-center gap-2 mb-3">
              <Cpu className="w-4 h-4 text-gray-400" />
              <h3 className="text-sm font-semibold text-gray-900">System</h3>
            </div>
            <div className="space-y-3">
              {device.system.cpuTempC != null && (
                <div className="flex items-center justify-between text-sm">
                  <span className="text-gray-500 flex items-center gap-1">
                    <Thermometer className="w-3.5 h-3.5" /> CPU Temp
                  </span>
                  <span className={
                    device.system.cpuTempC > 75 ? "text-red-600 font-medium" :
                    device.system.cpuTempC > 60 ? "text-amber-600" : "text-gray-700"
                  }>
                    {device.system.cpuTempC.toFixed(1)}°C
                  </span>
                </div>
              )}
              {device.system.memUsedPercent != null && (
                <div>
                  <div className="flex items-center justify-between text-sm mb-1">
                    <span className="text-gray-500">Memory</span>
                    <span className="text-gray-700">{device.system.memUsedPercent.toFixed(0)}%</span>
                  </div>
                  <UsageBar percent={device.system.memUsedPercent} color={device.system.memUsedPercent > 85 ? "bg-red-500" : "bg-blue-500"} />
                </div>
              )}
              {device.system.diskUsedPercent != null && (
                <div>
                  <div className="flex items-center justify-between text-sm mb-1">
                    <span className="text-gray-500 flex items-center gap-1">
                      <HardDrive className="w-3.5 h-3.5" /> Disk
                    </span>
                    <span className="text-gray-700">
                      {device.system.diskUsedPercent.toFixed(0)}%
                      {device.system.diskFreeGb != null && ` (${device.system.diskFreeGb.toFixed(1)} GB free)`}
                    </span>
                  </div>
                  <UsageBar percent={device.system.diskUsedPercent} color={device.system.diskUsedPercent > 85 ? "bg-red-500" : "bg-blue-500"} />
                </div>
              )}
              {device.system.loadAvg != null && device.system.loadAvg.length === 3 && (() => {
                const [l1, l5, l15] = device.system.loadAvg;
                const color = l1 > 3.5 ? "text-red-600 font-medium" : l1 > 2.0 ? "text-amber-600" : "text-gray-700";
                const strokeColor = l1 > 3.5 ? "#dc2626" : l1 > 2.0 ? "#d97706" : "#60a5fa";
                const vals = [l15, l5, l1];
                const W = 48, H = 20;
                const maxVal = Math.max(...vals, 0.5);
                const x = (i: number) => (i / 2) * W;
                const y = (v: number) => H - Math.min(v / maxVal, 1) * H;
                const points = vals.map((v, i) => `${x(i)},${y(v)}`).join(" ");
                return (
                  <div className="text-sm space-y-1">
                    <div className="flex items-center justify-between">
                      <span className="text-gray-500">Load avg</span>
                      <svg width={W} height={H} className="overflow-visible">
                        <polyline points={points} fill="none" stroke={strokeColor} strokeWidth="1.5" strokeLinejoin="round" />
                        {vals.map((v, i) => (
                          <circle key={i} cx={x(i)} cy={y(v)} r="2" fill={strokeColor} />
                        ))}
                      </svg>
                    </div>
                    <div className="flex justify-between text-xs text-gray-400">
                      <span>15m <span className="text-gray-600">{l15.toFixed(2)}</span></span>
                      <span>5m <span className="text-gray-600">{l5.toFixed(2)}</span></span>
                      <span>1m <span className={color}>{l1.toFixed(2)}</span></span>
                    </div>
                  </div>
                );
              })()}
              <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm text-gray-500 pt-2 border-t border-gray-100">
                {device.system.uptimeSeconds != null && (
                  <span>Uptime: <span className="text-gray-700">{formatUptime(device.system.uptimeSeconds)}</span></span>
                )}
                {device.system.wifiSignalDbm != null && (
                  <span>WiFi: <span className="text-gray-700">{device.system.wifiSignalDbm} dBm</span></span>
                )}
                {device.system.ipAddress && <span>IP: <span className="text-gray-700">{device.system.ipAddress}</span></span>}
                {device.system.wifiSsid && <span>SSID: <span className="text-gray-700">{device.system.wifiSsid}</span></span>}
                {device.system.piModel && <span className="col-span-2 text-gray-700">{device.system.piModel}</span>}
              </div>
            </div>
          </div>
        )}

        {/* Log Shipping */}
        {device.logShipping != null && (
          <div className="bg-white rounded-xl border border-gray-200 p-5">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <FileText className="w-4 h-4 text-gray-400" />
                <h3 className="text-sm font-semibold text-gray-900">Log Shipping</h3>
              </div>
              <StatusBadge ok={device.logShipping} label={device.logShipping ? "Active" : "Disabled"} />
            </div>
            {device.logShippingError && (
              <p className="text-sm text-red-600 mt-2">{device.logShippingError}</p>
            )}
          </div>
        )}

        {/* Device Actions */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <h3 className="text-sm font-semibold text-gray-900 mb-3">Actions</h3>
          <div className="flex items-center flex-wrap gap-3">
            {[
              { action: "reboot", icon: Power, label: "Reboot", color: "hover:text-red-600 hover:border-red-200" },
              { action: "clear-clips", icon: FolderX, label: "Clear Clips", color: "hover:text-amber-600 hover:border-amber-200" },
              { action: "clear-backups", icon: FolderX, label: "Clear Backups", color: "hover:text-amber-600 hover:border-amber-200" },
            ].map(({ action, icon: Icon, label, color }) => {
              const cmdState = commandState[action];
              return (
                <button
                  key={action}
                  onClick={() => requestCommand(action)}
                  disabled={cmdState?.status === "running"}
                  className={`inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-gray-600 border border-gray-200 rounded-lg ${color} transition-colors disabled:opacity-50`}
                >
                  {cmdState?.status === "running" ? (
                    <RotateCw className="w-3.5 h-3.5 animate-spin" />
                  ) : cmdState?.status === "success" ? (
                    <Check className="w-3.5 h-3.5 text-green-600" />
                  ) : cmdState?.status === "failed" ? (
                    <X className="w-3.5 h-3.5 text-red-600" />
                  ) : (
                    <Icon className="w-3.5 h-3.5" />
                  )}
                  {cmdState?.message || label}
                </button>
              );
            })}
          </div>

          {confirmAction && (
            <div className="mt-3 p-3 bg-amber-50 border border-amber-200 rounded-lg flex items-center justify-between">
              <span className="text-sm text-amber-800">
                {confirmAction === "reboot" ? "Reboot this device?" :
                 confirmAction === "clear-clips" ? "Delete all pending clips?" :
                 "Delete all OTA backups?"}
              </span>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => sendCommand(confirmAction)}
                  className="px-3 py-1.5 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded"
                >
                  Yes
                </button>
                <button
                  onClick={() => setConfirmAction(null)}
                  className="px-3 py-1.5 text-xs font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded"
                >
                  Cancel
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
