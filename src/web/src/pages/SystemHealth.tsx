import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { formatDistanceToNow } from "date-fns";
import {
  Wifi,
  WifiOff,
  Server,
  RefreshCw,
  Plus,
  Trash2,
  X,
  Copy,
  Check,
  Camera,
  CameraOff,
  Upload,
  HardDrive,
  Cpu,
  Thermometer,
  Settings,
  FileText,
  RotateCw,
  Power,
  FolderX,
} from "lucide-react";
import { api } from "../api";
import type { SystemHealth } from "../types";

function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const mins = Math.floor((seconds % 3600) / 60);
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${mins}m`;
  return `${mins}m`;
}

function UsageBar({ percent, color }: { percent: number; color: string }) {
  return (
    <div className="w-full h-1.5 bg-gray-100 rounded-full overflow-hidden">
      <div className={`h-full rounded-full ${color}`} style={{ width: `${Math.min(percent, 100)}%` }} />
    </div>
  );
}

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
  const [showAddDialog, setShowAddDialog] = useState(false);
  const [newDeviceName, setNewDeviceName] = useState("");
  const [registrationResult, setRegistrationResult] = useState<any>(null);
  const [registering, setRegistering] = useState(false);
  const [removing, setRemoving] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const [commandState, setCommandState] = useState<Record<string, { status: string; message?: string }>>({});
  const [confirmAction, setConfirmAction] = useState<{ thingName: string; action: string } | null>(null);

  const DESTRUCTIVE_ACTIONS = new Set(["reboot", "clear-clips", "clear-backups"]);

  const requestCommand = (thingName: string, action: string) => {
    if (DESTRUCTIVE_ACTIONS.has(action)) {
      setConfirmAction({ thingName, action });
    } else {
      sendCommand(thingName, action);
    }
  };

  const sendCommand = async (thingName: string, action: string) => {
    setConfirmAction(null);
    const key = `${thingName}:${action}`;
    setCommandState((prev) => ({ ...prev, [key]: { status: "running" } }));
    try {
      const { commandId } = await api.sendCommand(thingName, action);
      for (let i = 0; i < 10; i++) {
        await new Promise((r) => setTimeout(r, i < 3 ? 2000 : 5000));
        const result = await api.getCommandResult(thingName, commandId);
        if (result.status !== "pending") {
          const isSuccess = result.status === "success";
          setCommandState((prev) => ({
            ...prev,
            [key]: { status: isSuccess ? "success" : "failed", message: result.message || result.error }
          }));
          refreshHealth();
          setTimeout(() => setCommandState((prev) => { const n = { ...prev }; delete n[key]; return n; }), isSuccess ? 3000 : 5000);
          return;
        }
      }
      setCommandState((prev) => ({ ...prev, [key]: { status: "failed", message: "Timed out" } }));
      setTimeout(() => setCommandState((prev) => { const n = { ...prev }; delete n[key]; return n; }), 5000);
    } catch (e) {
      setCommandState((prev) => ({ ...prev, [key]: { status: "failed", message: (e as Error).message } }));
      setTimeout(() => setCommandState((prev) => { const n = { ...prev }; delete n[key]; return n; }), 5000);
    }
  };

  const refreshHealth = () =>
    Promise.all([api.getHealth(), api.getDevices()])
      .then(([health, devices]) => {
        const deviceMap = new Map(
          devices.devices.map((d) => [d.thingName, d])
        );
        setHealth({
          ...health,
          devices: health.devices.map((d) => ({
            ...d,
            ...deviceMap.get(d.thingName),
          })),
        });
      })
      .catch(console.error);

  useEffect(() => {
    Promise.all([api.getHealth(), api.getDevices()])
      .then(([health, devices]) => {
        const deviceMap = new Map(
          devices.devices.map((d) => [d.thingName, d])
        );
        setHealth({
          ...health,
          devices: health.devices.map((d) => ({
            ...d,
            ...deviceMap.get(d.thingName),
          })),
        });
      })
      .catch((e: Error) => setError(e.message));

    const interval = setInterval(refreshHealth, 10_000);
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

  const handleAddDevice = async () => {
    if (!newDeviceName.trim()) return;
    setRegistering(true);
    try {
      const result = await api.registerDevice(newDeviceName.trim());
      setRegistrationResult(result);
      setTimeout(refreshHealth, 2000);
    } catch (e) {
      setUpdateMessage(`Registration failed: ${(e as Error).message}`);
      setShowAddDialog(false);
    } finally {
      setRegistering(false);
    }
  };

  const handleRemoveDevice = async (thingName: string) => {
    if (!confirm(`Are you sure you want to remove device "${thingName}"? This cannot be undone.`)) {
      return;
    }
    setRemoving(thingName);
    try {
      await api.deregisterDevice(thingName);
      setUpdateMessage(`Device "${thingName}" removed successfully`);
      setTimeout(refreshHealth, 1000);
    } catch (e) {
      setUpdateMessage(`Failed to remove device: ${(e as Error).message}`);
    } finally {
      setRemoving(null);
    }
  };

  const copyToClipboard = (text: string, key: string) => {
    navigator.clipboard.writeText(text);
    setCopied(key);
    setTimeout(() => setCopied(null), 2000);
  };

  const closeAddDialog = () => {
    setShowAddDialog(false);
    setNewDeviceName("");
    setRegistrationResult(null);
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
          <button
            onClick={() => setShowAddDialog(true)}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg"
          >
            <Plus className="w-3 h-3" />
            Add Pi
          </button>
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

      {/* Add Pi Dialog */}
      {showAddDialog && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-white rounded-lg p-6 max-w-2xl w-full mx-4 max-h-[90vh] overflow-y-auto">
            {!registrationResult ? (
              <>
                <div className="flex items-center justify-between mb-4">
                  <h3 className="text-lg font-semibold text-gray-900">Add Raspberry Pi Device</h3>
                  <button onClick={closeAddDialog} className="text-gray-400 hover:text-gray-600">
                    <X className="w-5 h-5" />
                  </button>
                </div>
                <p className="text-sm text-gray-600 mb-4">
                  Enter a name for your new Pi device (e.g., "garage", "front-door").
                </p>
                <input
                  type="text"
                  value={newDeviceName}
                  onChange={(e) => setNewDeviceName(e.target.value)}
                  placeholder="Device name"
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg mb-4 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  disabled={registering}
                />
                <div className="flex items-center gap-3">
                  <button
                    onClick={handleAddDevice}
                    disabled={registering || !newDeviceName.trim()}
                    className="flex-1 px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {registering ? "Registering..." : "Register Device"}
                  </button>
                  <button
                    onClick={closeAddDialog}
                    disabled={registering}
                    className="px-4 py-2 border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50"
                  >
                    Cancel
                  </button>
                </div>
              </>
            ) : (
              <>
                <div className="flex items-center justify-between mb-4">
                  <h3 className="text-lg font-semibold text-green-600">Device Registered!</h3>
                  <button onClick={closeAddDialog} className="text-gray-400 hover:text-gray-600">
                    <X className="w-5 h-5" />
                  </button>
                </div>
                <p className="text-sm text-gray-600 mb-4">
                  Save these credentials securely. You'll need them to set up the Pi.
                </p>
                <div className="space-y-3">
                  <div>
                    <label className="text-xs font-medium text-gray-500">Thing Name:</label>
                    <div className="flex items-center gap-2 mt-1">
                      <code className="flex-1 p-2 bg-gray-100 rounded text-xs break-all">{registrationResult.thingName}</code>
                      <button onClick={() => copyToClipboard(registrationResult.thingName, "thingName")} className="p-2 hover:bg-gray-100 rounded">
                        {copied === "thingName" ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                      </button>
                    </div>
                  </div>
                  <div>
                    <label className="text-xs font-medium text-gray-500">IoT Endpoint:</label>
                    <div className="flex items-center gap-2 mt-1">
                      <code className="flex-1 p-2 bg-gray-100 rounded text-xs break-all">{registrationResult.ioTEndpoint}</code>
                      <button onClick={() => copyToClipboard(registrationResult.ioTEndpoint, "endpoint")} className="p-2 hover:bg-gray-100 rounded">
                        {copied === "endpoint" ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                      </button>
                    </div>
                  </div>
                  <div>
                    <label className="text-xs font-medium text-gray-500">Certificate:</label>
                    <div className="flex items-center gap-2 mt-1">
                      <textarea readOnly value={registrationResult.certificatePem} className="flex-1 p-2 bg-gray-100 rounded text-xs font-mono h-20 resize-none" />
                      <button onClick={() => copyToClipboard(registrationResult.certificatePem, "cert")} className="p-2 hover:bg-gray-100 rounded">
                        {copied === "cert" ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                      </button>
                    </div>
                  </div>
                  <div>
                    <label className="text-xs font-medium text-gray-500">Private Key:</label>
                    <div className="flex items-center gap-2 mt-1">
                      <textarea readOnly value={registrationResult.privateKey} className="flex-1 p-2 bg-gray-100 rounded text-xs font-mono h-20 resize-none" />
                      <button onClick={() => copyToClipboard(registrationResult.privateKey, "key")} className="p-2 hover:bg-gray-100 rounded">
                        {copied === "key" ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                      </button>
                    </div>
                  </div>
                </div>
                <button
                  onClick={closeAddDialog}
                  className="mt-4 w-full px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700"
                >
                  Done
                </button>
              </>
            )}
          </div>
        </div>
      )}

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
              <div className="flex items-center gap-2">
                <button
                  onClick={() => handleRemoveDevice(device.thingName)}
                  disabled={removing === device.thingName}
                  className="p-1 text-gray-400 hover:text-red-600 disabled:opacity-50"
                  title="Remove device"
                >
                  <Trash2 className="w-4 h-4" />
                </button>
                <span
                  className={`w-2 h-2 rounded-full ${device.online ? "bg-green-500" : "bg-red-500"}`}
                />
              </div>
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

            {/* Services */}
            {device.services && (
              <div className="mt-3 pt-3 border-t border-gray-100">
                <div className="flex items-center flex-wrap gap-1.5">
                  {Object.entries(device.services).map(([name, status]) => {
                    const cmdKey = `${device.thingName}:restart-${name}`;
                    const cmdState = commandState[cmdKey];
                    return (
                      <span
                        key={name}
                        className={`inline-flex items-center gap-1 px-2 py-0.5 rounded text-xs ${
                          status === "active"
                            ? "bg-green-50 text-green-700"
                            : "bg-red-50 text-red-700"
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
                            onClick={() => requestCommand(device.thingName, `restart-${name}`)}
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

            {/* Camera Status */}
            {device.camera && (
              <div className="mt-3 pt-3 border-t border-gray-100">
                <div className="flex items-center gap-2 mb-2">
                  {device.camera.healthy ? (
                    <Camera className="w-4 h-4 text-green-500" />
                  ) : (
                    <CameraOff className="w-4 h-4 text-red-500" />
                  )}
                  <span className="text-xs font-medium text-gray-700">Camera</span>
                  <StatusBadge
                    ok={device.camera.healthy}
                    label={device.camera.connected ? (device.camera.healthy ? "Healthy" : "Error") : "Disconnected"}
                  />
                </div>
                <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-gray-500">
                  {device.camera.sensor && <span>Sensor: {device.camera.sensor}</span>}
                  {device.camera.resolution && <span>Native: {device.camera.resolution}</span>}
                  {device.camera.recordResolution && <span>Record: {device.camera.recordResolution}</span>}
                </div>
              </div>
            )}

            {/* Motion & Upload Times */}
            {(device.lastMotionAt || device.lastUploadAt) && (
              <div className="mt-3 pt-3 border-t border-gray-100 space-y-1">
                {device.lastMotionAt && (
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-gray-500">Last motion</span>
                    <span className="text-xs text-gray-700">
                      {formatDistanceToNow(new Date(device.lastMotionAt), { addSuffix: true })}
                    </span>
                  </div>
                )}
                {device.lastUploadAt && (
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-gray-500">Last upload</span>
                    <span className="text-xs text-gray-700">
                      {formatDistanceToNow(new Date(device.lastUploadAt), { addSuffix: true })}
                    </span>
                  </div>
                )}
              </div>
            )}

            {/* Upload Stats */}
            {device.uploadStats && (
              <div className="mt-3 pt-3 border-t border-gray-100">
                <div className="flex items-center gap-2 mb-1">
                  <Upload className="w-3.5 h-3.5 text-gray-400" />
                  <span className="text-xs font-medium text-gray-700">Uploads</span>
                </div>
                <div className="flex items-center gap-3 text-xs text-gray-500">
                  <span>{device.uploadStats.uploadsToday} today</span>
                  {device.uploadStats.failedToday > 0 && (
                    <span className="text-red-600">{device.uploadStats.failedToday} failed</span>
                  )}
                  <span>{device.uploadStats.totalUploaded} total</span>
                </div>
                {device.clipsPending != null && device.clipsPending > 0 && (
                  <span className="text-xs text-amber-600 mt-1 block">
                    {device.clipsPending} clip{device.clipsPending !== 1 ? "s" : ""} pending upload
                  </span>
                )}
              </div>
            )}

            {/* System Health */}
            {device.system && (
              <div className="mt-3 pt-3 border-t border-gray-100">
                <div className="flex items-center gap-2 mb-2">
                  <Cpu className="w-3.5 h-3.5 text-gray-400" />
                  <span className="text-xs font-medium text-gray-700">System</span>
                </div>
                <div className="space-y-2">
                  {device.system.cpuTempC != null && (
                    <div className="flex items-center justify-between text-xs">
                      <span className="text-gray-500 flex items-center gap-1">
                        <Thermometer className="w-3 h-3" /> CPU Temp
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
                      <div className="flex items-center justify-between text-xs mb-0.5">
                        <span className="text-gray-500">Memory</span>
                        <span className="text-gray-700">{device.system.memUsedPercent.toFixed(0)}%</span>
                      </div>
                      <UsageBar percent={device.system.memUsedPercent} color={device.system.memUsedPercent > 85 ? "bg-red-500" : "bg-blue-500"} />
                    </div>
                  )}
                  {device.system.diskUsedPercent != null && (
                    <div>
                      <div className="flex items-center justify-between text-xs mb-0.5">
                        <span className="text-gray-500 flex items-center gap-1">
                          <HardDrive className="w-3 h-3" /> Disk
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
                    // Sparkline: oldest (15m) → 5m → newest (1m), left to right
                    const vals = [l15, l5, l1];
                    const W = 40, H = 16;
                    const maxVal = Math.max(...vals, 0.5);
                    const x = (i: number) => (i / 2) * W;
                    const y = (v: number) => H - Math.min(v / maxVal, 1) * H;
                    const points = vals.map((v, i) => `${x(i)},${y(v)}`).join(" ");
                    return (
                      <div className="text-xs space-y-1">
                        <div className="flex items-center justify-between">
                          <span className="text-gray-500">Load avg</span>
                          <svg width={W} height={H} className="overflow-visible">
                            <polyline points={points} fill="none" stroke={strokeColor} strokeWidth="1.5" strokeLinejoin="round" />
                            {vals.map((v, i) => (
                              <circle key={i} cx={x(i)} cy={y(v)} r="2" fill={strokeColor} />
                            ))}
                          </svg>
                        </div>
                        <div className="flex justify-between text-gray-400">
                          <span>15m <span className="text-gray-600">{l15.toFixed(2)}</span></span>
                          <span>5m <span className="text-gray-600">{l5.toFixed(2)}</span></span>
                          <span>1m <span className={color}>{l1.toFixed(2)}</span></span>
                        </div>
                      </div>
                    );
                  })()}
                  <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-gray-500 mt-1">
                    {device.system.uptimeSeconds != null && (
                      <span>Uptime: {formatUptime(device.system.uptimeSeconds)}</span>
                    )}
                    {device.system.wifiSignalDbm != null && (
                      <span>WiFi: {device.system.wifiSignalDbm} dBm</span>
                    )}
                    {device.system.ipAddress && <span>IP: {device.system.ipAddress}</span>}
                    {device.system.wifiSsid && <span>SSID: {device.system.wifiSsid}</span>}
                    {device.system.piModel && <span className="col-span-2">{device.system.piModel}</span>}
                  </div>
                </div>
              </div>
            )}

            {/* Log Shipping Status */}
            {device.logShipping != null && (
              <div className="mt-3 pt-3 border-t border-gray-100">
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    <FileText className="w-3.5 h-3.5 text-gray-400" />
                    <span className="text-xs font-medium text-gray-700">Log Shipping</span>
                  </div>
                  <StatusBadge ok={device.logShipping} label={device.logShipping ? "Active" : "Disabled"} />
                </div>
                {device.logShippingError && (
                  <p className="text-xs text-red-600 mt-1">{device.logShippingError}</p>
                )}
              </div>
            )}

            {/* Device Actions */}
            <div className="mt-3 pt-3 border-t border-gray-100">
              <div className="flex items-center flex-wrap gap-2">
                <Link
                  to={`/device/${device.thingName}/config`}
                  className="flex items-center gap-1.5 text-xs font-medium text-gray-600 hover:text-blue-600 transition-colors"
                >
                  <Settings className="w-3.5 h-3.5" />
                  Settings
                </Link>
                <Link
                  to={`/device/${device.thingName}/logs`}
                  className="flex items-center gap-1.5 text-xs font-medium text-gray-600 hover:text-blue-600 transition-colors"
                >
                  <FileText className="w-3.5 h-3.5" />
                  Logs
                </Link>
                {[
                  { action: "reboot", icon: Power, label: "Reboot", color: "hover:text-red-600" },
                  { action: "clear-clips", icon: FolderX, label: "Clear Clips", color: "hover:text-amber-600" },
                  { action: "clear-backups", icon: FolderX, label: "Clear Backups", color: "hover:text-amber-600" },
                ].map(({ action, icon: Icon, label, color }) => {
                  const cmdKey = `${device.thingName}:${action}`;
                  const cmdState = commandState[cmdKey];
                  return (
                    <button
                      key={action}
                      onClick={() => requestCommand(device.thingName, action)}
                      disabled={cmdState?.status === "running"}
                      className={`flex items-center gap-1.5 text-xs font-medium text-gray-600 ${color} transition-colors disabled:opacity-50`}
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

              {/* Inline confirmation */}
              {confirmAction && confirmAction.thingName === device.thingName && (
                <div className="mt-2 p-2 bg-amber-50 border border-amber-200 rounded-lg flex items-center justify-between">
                  <span className="text-xs text-amber-800">
                    {confirmAction.action === "reboot" ? "Reboot this device?" :
                     confirmAction.action === "clear-clips" ? "Delete all pending clips?" :
                     "Delete all OTA backups?"}
                  </span>
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => sendCommand(confirmAction.thingName, confirmAction.action)}
                      className="px-2 py-1 text-xs font-medium text-white bg-amber-600 hover:bg-amber-700 rounded"
                    >
                      Yes
                    </button>
                    <button
                      onClick={() => setConfirmAction(null)}
                      className="px-2 py-1 text-xs font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded"
                    >
                      Cancel
                    </button>
                  </div>
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
