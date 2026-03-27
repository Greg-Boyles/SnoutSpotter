import { useEffect, useState } from "react";
import { formatDistanceToNow } from "date-fns";
import {
  Wifi,
  WifiOff,
  Server,
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

  useEffect(() => {
    api
      .getHealth()
      .then(setHealth)
      .catch((e: Error) => setError(e.message));

    const interval = setInterval(() => {
      api.getHealth().then(setHealth).catch(console.error);
    }, 30_000);
    return () => clearInterval(interval);
  }, []);

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
      </div>
    </div>
  );
}
