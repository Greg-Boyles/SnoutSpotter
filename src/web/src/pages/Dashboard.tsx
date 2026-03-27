import { useEffect, useState } from "react";
import { Video, Search, Dog, HardDrive } from "lucide-react";
import { api } from "../api";
import type { StatsOverview } from "../types";

function StatCard({
  icon: Icon,
  label,
  value,
  sub,
}: {
  icon: React.ElementType;
  label: string;
  value: string | number;
  sub?: string;
}) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5">
      <div className="flex items-center gap-3 mb-3">
        <div className="p-2 rounded-lg bg-amber-50">
          <Icon className="w-5 h-5 text-amber-600" />
        </div>
        <span className="text-sm text-gray-500">{label}</span>
      </div>
      <p className="text-2xl font-bold text-gray-900">{value}</p>
      {sub && <p className="text-xs text-gray-400 mt-1">{sub}</p>}
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(1)} MB`;
  return `${(bytes / 1073741824).toFixed(2)} GB`;
}

export default function Dashboard() {
  const [stats, setStats] = useState<StatsOverview | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getStats()
      .then(setStats)
      .catch((e: Error) => setError(e.message));
  }, []);

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">
        Failed to load stats: {error}
      </div>
    );
  }

  if (!stats) {
    return <div className="text-gray-400">Loading...</div>;
  }

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">Dashboard</h1>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        <StatCard
          icon={Video}
          label="Total Clips"
          value={stats.totalClips}
          sub={`${stats.clipsToday} today`}
        />
        <StatCard
          icon={Search}
          label="Detections"
          value={stats.totalDetections}
        />
        <StatCard
          icon={Dog}
          label="My Dog"
          value={stats.myDogDetections}
        />
        <StatCard
          icon={HardDrive}
          label="Pi Status"
          value={stats.piOnline ? "Online" : "Offline"}
          sub={stats.lastUploadTime ? `Last: ${new Date(stats.lastUploadTime).toLocaleString()}` : undefined}
        />
      </div>
    </div>
  );
}
