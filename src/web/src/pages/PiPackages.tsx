import { useEffect, useState } from "react";
import { Package, Trash2, CheckCircle, Loader2 } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";
import type { PiRelease } from "../types";

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export default function PiPackages() {
  const [releases, setReleases] = useState<PiRelease[]>([]);
  const [latestVersion, setLatestVersion] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  const loadReleases = () => {
    api.listPiReleases()
      .then((data) => {
        setReleases(data.releases);
        setLatestVersion(data.latestVersion);
      })
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    loadReleases();
  }, []);

  const handleDelete = async (version: string) => {
    setConfirmDelete(null);
    setDeleting(version);
    setMessage(null);
    try {
      await api.deletePiRelease(version);
      setMessage({ text: `${version} deleted`, error: false });
      loadReleases();
    } catch (e) {
      setMessage({ text: `Delete failed: ${(e as Error).message}`, error: true });
    } finally {
      setDeleting(null);
    }
  };

  if (error) {
    return <div className="text-red-600 bg-red-50 p-4 rounded-lg">Failed to load releases: {error}</div>;
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Pi Packages</h1>
          {latestVersion && (
            <p className="text-sm text-gray-500 mt-1">Latest: v{latestVersion}</p>
          )}
        </div>
      </div>

      {message && (
        <div className={`mb-4 p-3 rounded-lg text-sm ${message.error ? "bg-red-50 text-red-700" : "bg-green-50 text-green-700"}`}>
          {message.text}
        </div>
      )}

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        {loading ? (
          <div className="p-5 animate-pulse space-y-3">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="h-10 bg-gray-100 rounded" />
            ))}
          </div>
        ) : releases.length === 0 ? (
          <div className="p-8 text-center">
            <Package className="w-8 h-8 text-gray-300 mx-auto mb-2" />
            <p className="text-sm text-gray-400">No Pi releases found</p>
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-gray-500 border-b border-gray-100">
                <th className="px-5 py-3 font-medium">Version</th>
                <th className="px-5 py-3 font-medium">Size</th>
                <th className="px-5 py-3 font-medium">Published</th>
                <th className="px-5 py-3 font-medium">Status</th>
                <th className="px-5 py-3 font-medium w-20"></th>
              </tr>
            </thead>
            <tbody>
              {releases.map((release) => (
                <tr key={release.version} className={`border-b border-gray-50 ${release.isLatest ? "bg-green-50/50" : "hover:bg-gray-50"}`}>
                  <td className="px-5 py-3 font-medium text-gray-900">{release.version}</td>
                  <td className="px-5 py-3 text-gray-500">{formatBytes(release.sizeBytes)}</td>
                  <td className="px-5 py-3 text-gray-500">
                    {formatDistanceToNow(new Date(release.lastModified), { addSuffix: true })}
                  </td>
                  <td className="px-5 py-3">
                    {release.isLatest ? (
                      <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-700">
                        <CheckCircle className="w-3 h-3" /> Latest
                      </span>
                    ) : (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-500">
                        Available
                      </span>
                    )}
                  </td>
                  <td className="px-5 py-3">
                    {!release.isLatest && (
                      <button
                        onClick={() => setConfirmDelete(release.version)}
                        disabled={deleting === release.version}
                        className="p-1.5 text-gray-400 hover:text-red-600 rounded-lg hover:bg-red-50 disabled:opacity-50"
                        title={`Delete ${release.version}`}
                      >
                        {deleting === release.version ? (
                          <Loader2 className="w-4 h-4 animate-spin" />
                        ) : (
                          <Trash2 className="w-4 h-4" />
                        )}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Delete confirmation */}
      {confirmDelete && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
          <div className="bg-white rounded-xl border border-gray-200 p-6 max-w-sm w-full mx-4">
            <h3 className="text-lg font-semibold text-gray-900 mb-2">Delete release?</h3>
            <p className="text-sm text-gray-500 mb-4">
              Are you sure you want to delete <strong>{confirmDelete}</strong>? This cannot be undone.
              Devices running this version will not be affected, but you won't be able to roll back to it.
            </p>
            <div className="flex items-center justify-end gap-2">
              <button
                onClick={() => setConfirmDelete(null)}
                className="px-4 py-2 text-sm font-medium text-gray-600 bg-white border border-gray-300 hover:bg-gray-50 rounded-lg"
              >
                Cancel
              </button>
              <button
                onClick={() => handleDelete(confirmDelete)}
                className="px-4 py-2 text-sm font-medium text-white bg-red-600 hover:bg-red-700 rounded-lg"
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
