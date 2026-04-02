import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Video, Search, Dog, HardDrive, Clock, ChevronRight, Tag, Package, Play, Loader2 } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer } from "recharts";
import { api } from "../api";
import type { Clip, StatsOverview } from "../types";

function StatCard({
  icon: Icon,
  label,
  value,
  sub,
  color = "bg-amber-50",
  iconColor = "text-amber-600",
}: {
  icon: React.ElementType;
  label: string;
  value: string | number;
  sub?: string;
  color?: string;
  iconColor?: string;
}) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5">
      <div className="flex items-center gap-3 mb-3">
        <div className={`p-2 rounded-lg ${color}`}>
          <Icon className={`w-5 h-5 ${iconColor}`} />
        </div>
        <span className="text-sm text-gray-500">{label}</span>
      </div>
      <p className="text-2xl font-bold text-gray-900">{value}</p>
      {sub && <p className="text-xs text-gray-400 mt-1">{sub}</p>}
    </div>
  );
}

function buildActivityData(clips: Clip[]): { date: string; clips: number }[] {
  const counts: Record<string, number> = {};
  // Last 14 days
  for (let i = 13; i >= 0; i--) {
    const d = new Date();
    d.setDate(d.getDate() - i);
    const key = d.toISOString().slice(0, 10);
    counts[key] = 0;
  }
  for (const clip of clips) {
    const key = new Date(clip.timestamp * 1000).toISOString().slice(0, 10);
    if (key in counts) counts[key]++;
  }
  return Object.entries(counts).map(([date, clips]) => ({
    date: date.slice(5), // MM-DD
    clips,
  }));
}

export default function Dashboard() {
  const [stats, setStats] = useState<StatsOverview | null>(null);
  const [recentClips, setRecentClips] = useState<Clip[]>([]);
  const [allClips, setAllClips] = useState<Clip[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [labelStats, setLabelStats] = useState<{ total: number; dogs: number; noDogs: number; reviewed: number; unreviewed: number; myDog: number; otherDog: number; confirmedNoDog: number; breeds: Record<string, number> } | null>(null);
  const [latestExport, setLatestExport] = useState<Record<string, string> | null>(null);
  const [runningAutoLabel, setRunningAutoLabel] = useState(false);

  const loadData = () => {
    api.getStats().then(setStats).catch((e: Error) => setError(e.message));
    api.getClips(8).then((data) => setRecentClips(data.clips)).catch(console.error);
    api.getClips(500).then((data) => setAllClips(data.clips)).catch(console.error);
    api.getLabelStats().then(setLabelStats).catch(console.error);
    api.listExports().then((data) => setLatestExport(data.exports[0] || null)).catch(console.error);
  };

  useEffect(() => {
    loadData();
    const interval = setInterval(loadData, 30_000);
    return () => clearInterval(interval);
  }, []);

  if (error) {
    return (
      <div className="text-red-600 bg-red-50 p-4 rounded-lg">
        Failed to load stats: {error}
      </div>
    );
  }

  if (!stats) {
    return (
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {[...Array(4)].map((_, i) => (
          <div key={i} className="bg-white rounded-xl border border-gray-200 p-5 animate-pulse">
            <div className="h-4 bg-gray-200 rounded w-24 mb-3" />
            <div className="h-8 bg-gray-200 rounded w-16" />
          </div>
        ))}
      </div>
    );
  }

  const activityData = buildActivityData(allClips);

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">Dashboard</h1>

      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
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
          color="bg-blue-50"
          iconColor="text-blue-600"
        />
        <StatCard
          icon={Dog}
          label="My Dog"
          value={stats.myDogDetections}
          color="bg-green-50"
          iconColor="text-green-600"
        />
        <StatCard
          icon={HardDrive}
          label="Pi Status"
          value={stats.piOnline ? "Online" : "Offline"}
          sub={stats.lastUploadTime ? `Last upload ${formatDistanceToNow(new Date(stats.lastUploadTime), { addSuffix: true })}` : undefined}
          color={stats.piOnline ? "bg-green-50" : "bg-red-50"}
          iconColor={stats.piOnline ? "text-green-600" : "text-red-600"}
        />
      </div>

      {/* Activity chart */}
      {activityData.length > 0 && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 mb-6">
          <h2 className="text-sm font-semibold text-gray-900 mb-4">Clips — Last 14 Days</h2>
          <ResponsiveContainer width="100%" height={200}>
            <BarChart data={activityData}>
              <XAxis dataKey="date" tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
              <YAxis allowDecimals={false} tick={{ fontSize: 11 }} tickLine={false} axisLine={false} width={30} />
              <Tooltip
                contentStyle={{ fontSize: 12, borderRadius: 8, border: "1px solid #e5e7eb" }}
                labelFormatter={(label) => `Date: ${label}`}
              />
              <Bar dataKey="clips" fill="#f59e0b" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* ML Pipeline */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-6">
        {/* Auto-Label */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-2">
              <Tag className="w-5 h-5 text-blue-500" />
              <h2 className="text-sm font-semibold text-gray-900">Auto-Label</h2>
            </div>
            <button
              onClick={async () => {
                setRunningAutoLabel(true);
                try { await api.triggerAutoLabel(); } catch (e) { console.error(e); }
                setRunningAutoLabel(false);
              }}
              disabled={runningAutoLabel}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
            >
              {runningAutoLabel ? <Loader2 className="w-3 h-3 animate-spin" /> : <Play className="w-3 h-3" />}
              {runningAutoLabel ? "Running..." : "Run"}
            </button>
          </div>
          {labelStats ? (
            <div className="space-y-3">
              <div className="grid grid-cols-4 gap-2 text-center">
                <div>
                  <p className="text-lg font-bold text-gray-900">{labelStats.total}</p>
                  <p className="text-xs text-gray-500">Total</p>
                </div>
                <div>
                  <p className="text-lg font-bold text-green-600">{labelStats.myDog}</p>
                  <p className="text-xs text-gray-500">My Dog</p>
                </div>
                <div>
                  <p className="text-lg font-bold text-orange-600">{labelStats.otherDog}</p>
                  <p className="text-xs text-gray-500">Other Dog</p>
                </div>
                <div>
                  <p className="text-lg font-bold text-gray-500">{labelStats.confirmedNoDog}</p>
                  <p className="text-xs text-gray-500">No Dog</p>
                </div>
              </div>
              {labelStats.unreviewed > 0 && (
                <p className="text-xs text-amber-600">{labelStats.unreviewed} unreviewed</p>
              )}
              {Object.keys(labelStats.breeds).length > 0 && (
                <div className="pt-2 border-t border-gray-100">
                  <p className="text-xs font-medium text-gray-500 mb-1.5">Breeds</p>
                  <div className="flex flex-wrap gap-1.5">
                    {Object.entries(labelStats.breeds)
                      .sort(([, a], [, b]) => b - a)
                      .map(([breed, count]) => (
                        <span key={breed} className="inline-flex items-center gap-1 px-2 py-0.5 bg-gray-50 text-xs text-gray-600 rounded-full">
                          {breed} <span className="font-medium text-gray-900">{count}</span>
                        </span>
                      ))}
                  </div>
                </div>
              )}
              <div className="flex items-center justify-end text-xs pt-2 border-t border-gray-100">
                <Link to="/labels" className="text-blue-600 hover:text-blue-700 font-medium">
                  View Labels →
                </Link>
              </div>
            </div>
          ) : (
            <p className="text-xs text-gray-400">Loading...</p>
          )}
        </div>

        {/* Latest Export */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-2">
              <Package className="w-5 h-5 text-amber-500" />
              <h2 className="text-sm font-semibold text-gray-900">Training Export</h2>
            </div>
            <Link
              to="/exports"
              className="text-xs font-medium text-blue-600 hover:text-blue-700"
            >
              View All →
            </Link>
          </div>
          {latestExport ? (
            <div className="space-y-2">
              <div className="grid grid-cols-3 gap-2 text-center">
                <div>
                  <p className="text-lg font-bold text-gray-900">{latestExport.total_images || "—"}</p>
                  <p className="text-xs text-gray-500">Images</p>
                </div>
                <div>
                  <p className="text-lg font-bold text-green-600">{latestExport.my_dog_count || "—"}</p>
                  <p className="text-xs text-gray-500">My Dog</p>
                </div>
                <div>
                  <p className="text-lg font-bold text-gray-600">{latestExport.size_mb ? `${latestExport.size_mb} MB` : "—"}</p>
                  <p className="text-xs text-gray-500">Size</p>
                </div>
              </div>
              <div className="flex items-center justify-between text-xs pt-2 border-t border-gray-100">
                <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full font-medium ${
                  latestExport.status === "complete" ? "bg-green-50 text-green-700" :
                  latestExport.status === "running" ? "bg-blue-50 text-blue-700" :
                  "bg-red-50 text-red-700"
                }`}>
                  {latestExport.status}
                </span>
                <span className="text-gray-400">
                  {latestExport.created_at
                    ? formatDistanceToNow(new Date(latestExport.created_at), { addSuffix: true })
                    : ""}
                </span>
              </div>
            </div>
          ) : (
            <div className="text-center py-4">
              <p className="text-xs text-gray-400">No exports yet</p>
              <Link to="/labels" className="text-xs text-blue-600 hover:text-blue-700 font-medium">
                Go to Labels →
              </Link>
            </div>
          )}
        </div>
      </div>

      {/* Recent clips */}
      {recentClips.length > 0 && (
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-sm font-semibold text-gray-900">Recent Clips</h2>
            <Link
              to="/clips"
              className="inline-flex items-center gap-1 text-xs font-medium text-blue-600 hover:text-blue-700"
            >
              View all <ChevronRight className="w-3 h-3" />
            </Link>
          </div>
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-3">
            {recentClips.map((clip) => (
              <Link
                key={clip.clipId}
                to={`/clips/${clip.clipId}`}
                className="group rounded-lg border border-gray-100 overflow-hidden hover:border-amber-300 transition-colors"
              >
                <div className="aspect-video bg-gray-100 relative">
                  {clip.thumbnailUrl ? (
                    <img src={clip.thumbnailUrl} alt="" className="w-full h-full object-cover" loading="lazy" />
                  ) : (
                    <div className="w-full h-full flex items-center justify-center">
                      <Video className="w-6 h-6 text-gray-300" />
                    </div>
                  )}
                  {clip.detectionType && clip.detectionType !== "pending" && (
                    <span className={`absolute top-1 left-1 px-1.5 py-0.5 rounded text-xs font-medium ${
                      clip.detectionType === "my_dog"
                        ? "bg-green-600 text-white"
                        : clip.detectionType === "other_dog"
                        ? "bg-amber-500 text-white"
                        : "bg-gray-500 text-white"
                    }`}>
                      {clip.detectionType === "my_dog" ? "My Dog" : clip.detectionType === "no_dog" ? "No Dog" : clip.detectionType}
                    </span>
                  )}
                  <span className="absolute bottom-1 right-1 px-1.5 py-0.5 bg-black bg-opacity-60 text-white text-xs rounded">
                    {clip.durationSeconds}s
                  </span>
                </div>
                <div className="p-2">
                  <p className="text-xs text-gray-500 flex items-center gap-1">
                    <Clock className="w-3 h-3" />
                    {formatDistanceToNow(new Date(clip.timestamp * 1000), { addSuffix: true })}
                  </p>
                </div>
              </Link>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
