import { useEffect, useState } from "react";
import { Loader2, Pencil, Check } from "lucide-react";
import { api } from "../../api";
import type { SnoutSpotterDeviceDto } from "../../types";

// Lives on the Pi detail page. Reads the registry row for this thing name so
// the user can give the camera a display name and add notes — both optional.
// If no row exists yet (brand-new Pi that no one has viewed via /devices), we
// stay quiet until the first list triggers lazy-create.
export default function DeviceRegistryCard({ thingName }: { thingName: string }) {
  const [row, setRow] = useState<SnoutSpotterDeviceDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [displayName, setDisplayName] = useState("");
  const [notes, setNotes] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const { snoutSpotter } = await api.devices.list();
      const mine = snoutSpotter.find((d) => d.thingName === thingName) ?? null;
      setRow(mine);
      if (mine) {
        setDisplayName(mine.displayName);
        setNotes(mine.notes ?? "");
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load registry entry");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, [thingName]);

  const handleSave = async () => {
    if (!displayName.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await api.devices.updateSnoutSpotter(thingName, {
        displayName: displayName.trim(),
        notes: notes.trim() ? notes.trim() : null,
      });
      setRow(updated);
      setEditing(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Save failed");
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="bg-white rounded-xl border border-gray-200 p-5">
        <h3 className="text-sm font-semibold text-gray-900 mb-3">Registry</h3>
        <Loader2 className="w-4 h-4 animate-spin text-gray-400" />
      </div>
    );
  }

  if (!row) {
    // No row yet — fine, just don't render. First /devices view will create it.
    return null;
  }

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-semibold text-gray-900">Registry</h3>
        {!editing && (
          <button
            onClick={() => setEditing(true)}
            className="text-xs text-amber-700 hover:text-amber-800 flex items-center gap-1"
          >
            <Pencil className="w-3 h-3" /> Edit
          </button>
        )}
      </div>

      {error && (
        <div className="mb-3 p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700">{error}</div>
      )}

      {editing ? (
        <div className="space-y-2">
          <div>
            <label className="block text-xs text-gray-500 mb-1">Display name</label>
            <input
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
            />
          </div>
          <div>
            <label className="block text-xs text-gray-500 mb-1">Notes</label>
            <textarea
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              rows={2}
              className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
              placeholder="Optional"
            />
          </div>
          <div className="flex items-center gap-2">
            <button
              onClick={handleSave}
              disabled={saving || !displayName.trim()}
              className="flex items-center gap-1 px-3 py-1 bg-amber-600 text-white rounded-lg text-xs font-medium disabled:opacity-50"
            >
              {saving ? <Loader2 className="w-3 h-3 animate-spin" /> : <Check className="w-3 h-3" />}
              Save
            </button>
            <button
              onClick={() => {
                setDisplayName(row.displayName);
                setNotes(row.notes ?? "");
                setEditing(false);
                setError(null);
              }}
              className="px-3 py-1 bg-gray-100 text-gray-700 rounded-lg text-xs font-medium"
            >
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <dl className="space-y-2 text-sm">
          <div className="flex items-center justify-between gap-3">
            <dt className="text-gray-500">Display name</dt>
            <dd className="text-gray-900 font-medium truncate">{row.displayName}</dd>
          </div>
          {row.notes && (
            <div>
              <dt className="text-gray-500 mb-0.5">Notes</dt>
              <dd className="text-gray-700 whitespace-pre-wrap">{row.notes}</dd>
            </div>
          )}
        </dl>
      )}
    </div>
  );
}
