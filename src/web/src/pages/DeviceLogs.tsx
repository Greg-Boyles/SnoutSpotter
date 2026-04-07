import { useEffect, useState, useRef } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft, RefreshCw } from "lucide-react";
import { api } from "../api";
import type { LogEntry } from "../types";

const TIME_RANGES = [
  { label: "15m", value: 15 },
  { label: "1h", value: 60 },
  { label: "6h", value: 360 },
  { label: "24h", value: 1440 },
];

const LEVELS = ["All", "INFO", "WARNING", "ERROR"];
const SERVICES = ["All", "motion", "uploader", "agent", "watchdog"];

function levelColor(level: string): string {
  switch (level.toUpperCase()) {
    case "ERROR":
    case "CRITICAL":
      return "text-red-600";
    case "WARNING":
      return "text-amber-600";
    case "DEBUG":
      return "text-gray-400";
    default:
      return "text-green-600";
  }
}

function formatLogTimestamp(ts: string): string {
  const d = new Date(ts);
  if (isNaN(d.getTime()) || d.getFullYear() < 2020) return "Invalid";
  const isToday = d.toDateString() === new Date().toDateString();
  return isToday ? d.toLocaleTimeString() : d.toLocaleString();
}

function levelBg(level: string): string {
  switch (level.toUpperCase()) {
    case "ERROR":
    case "CRITICAL":
      return "bg-red-50";
    case "WARNING":
      return "bg-amber-50";
    default:
      return "";
  }
}

export default function DeviceLogs() {
  const { thingName } = useParams<{ thingName: string }>();
  const [logs, setLogs] = useState<LogEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [minutes, setMinutes] = useState(60);
  const [level, setLevel] = useState("All");
  const [service, setService] = useState("All");
  const [autoRefresh, setAutoRefresh] = useState(false);
  const logEndRef = useRef<HTMLDivElement>(null);

  const fetchLogs = async () => {
    if (!thingName) return;
    try {
      setLoading(true);
      const result = await api.getDeviceLogs(
        thingName,
        minutes,
        level === "All" ? undefined : level,
        service === "All" ? undefined : service,
      );
      setLogs(result.logs);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchLogs();
  }, [thingName, minutes, level, service]);

  useEffect(() => {
    if (!autoRefresh) return;
    const interval = setInterval(fetchLogs, 30_000);
    return () => clearInterval(interval);
  }, [autoRefresh, thingName, minutes, level, service]);

  return (
    <div>
      <div className="flex items-center gap-3 mb-4">
        <Link
          to={`/device/${thingName}`}
          className="p-1.5 rounded-lg hover:bg-gray-100 text-gray-500"
        >
          <ArrowLeft className="w-5 h-5" />
        </Link>
        <h1 className="text-2xl font-bold text-gray-900">
          Device Logs — {thingName}
        </h1>
      </div>

      {/* Filters */}
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <div className="flex items-center gap-1 bg-white border border-gray-200 rounded-lg p-0.5">
          {TIME_RANGES.map((r) => (
            <button
              key={r.value}
              onClick={() => setMinutes(r.value)}
              className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
                minutes === r.value
                  ? "bg-blue-600 text-white"
                  : "text-gray-600 hover:bg-gray-100"
              }`}
            >
              {r.label}
            </button>
          ))}
        </div>

        <select
          value={level}
          onChange={(e) => setLevel(e.target.value)}
          className="px-3 py-1.5 text-xs font-medium border border-gray-200 rounded-lg bg-white"
        >
          {LEVELS.map((l) => (
            <option key={l} value={l}>
              {l === "All" ? "All Levels" : l}
            </option>
          ))}
        </select>

        <select
          value={service}
          onChange={(e) => setService(e.target.value)}
          className="px-3 py-1.5 text-xs font-medium border border-gray-200 rounded-lg bg-white"
        >
          {SERVICES.map((s) => (
            <option key={s} value={s}>
              {s === "All" ? "All Services" : s}
            </option>
          ))}
        </select>

        <label className="flex items-center gap-1.5 text-xs text-gray-600 cursor-pointer">
          <input
            type="checkbox"
            checked={autoRefresh}
            onChange={(e) => setAutoRefresh(e.target.checked)}
            className="rounded"
          />
          Auto-refresh
        </label>

        <button
          onClick={fetchLogs}
          disabled={loading}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-gray-600 hover:text-blue-600 border border-gray-200 rounded-lg hover:bg-gray-50 disabled:opacity-50"
        >
          <RefreshCw className={`w-3 h-3 ${loading ? "animate-spin" : ""}`} />
          Refresh
        </button>

        <span className="text-xs text-gray-400 ml-auto">
          {logs.length} entries
        </span>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-lg text-sm">
          {error}
        </div>
      )}

      {/* Log output */}
      <div className="bg-gray-900 rounded-xl border border-gray-700 overflow-hidden">
        <div className="overflow-auto max-h-[calc(100vh-260px)]">
          {logs.length === 0 && !loading ? (
            <div className="p-8 text-center text-gray-500">
              No logs found for the selected time range and filters.
            </div>
          ) : (
            <table className="w-full">
              <tbody className="font-mono text-xs leading-relaxed">
                {logs.map((entry, i) => (
                  <tr
                    key={i}
                    className={`border-b border-gray-800 hover:bg-gray-800/50 ${levelBg(entry.level)}`}
                  >
                    <td className="px-3 py-1 text-gray-500 whitespace-nowrap align-top" title={entry.timestamp}>
                      {formatLogTimestamp(entry.timestamp)}
                    </td>
                    <td className={`px-2 py-1 whitespace-nowrap align-top font-medium ${levelColor(entry.level)}`}>
                      {entry.level.padEnd(7)}
                    </td>
                    <td className="px-2 py-1 text-blue-400 whitespace-nowrap align-top">
                      {entry.service}
                    </td>
                    <td className="px-3 py-1 text-gray-300 break-all">
                      {entry.message}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <div ref={logEndRef} />
        </div>
      </div>
    </div>
  );
}
