import { useEffect, useMemo, useState } from "react";
import {
  Camera,
  Loader2,
  AlertCircle,
  RefreshCw,
  Link2,
  Link2Off,
  Utensils,
  Droplet,
  DoorOpen,
  Wifi,
  Box,
  X,
  Pencil,
  Check,
  Radio,
  Cpu,
  Activity,
} from "lucide-react";
import { api } from "../api";
import type {
  DeviceLinkDto,
  DeviceListResponse,
  SnoutSpotterDeviceDto,
  SpcDeviceRegistryDto,
} from "../types";

// SPC product_id -> friendly label + icon. Matches Sure Pet Care's DeviceType enum.
const SPC_PRODUCT_LABELS: Record<number, { label: string; icon: React.ElementType }> = {
  1: { label: "Hub", icon: Wifi },
  2: { label: "Repeater", icon: Radio },
  3: { label: "Pet Door Connect", icon: DoorOpen },
  4: { label: "Feeder Connect", icon: Utensils },
  5: { label: "Programmer", icon: Cpu },
  6: { label: "Dual Scan Connect", icon: DoorOpen },
  7: { label: "Feeder Lite", icon: Utensils },
  8: { label: "Felaqua (Poseidon)", icon: Droplet },
  9: { label: "Dual Scan Cat Flap 2", icon: DoorOpen },
  10: { label: "Dual Scan Pet Door", icon: DoorOpen },
  32: { label: "No-ID Dog Bowl Connect", icon: Utensils },
  255: { label: "Animo", icon: Activity },
};

function spcProductMeta(productId: number | null): { label: string; icon: React.ElementType } {
  if (productId == null) return { label: "SPC device", icon: Box };
  return SPC_PRODUCT_LABELS[productId] ?? { label: `Product ${productId}`, icon: Box };
}

export default function Devices() {
  const [data, setData] = useState<DeviceListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [linkTarget, setLinkTarget] = useState<string | null>(null); // snoutspotter thing_name being linked

  const load = async () => {
    setLoading(true);
    try {
      const fresh = await api.devices.list();
      setData(fresh);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load devices");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  const linksByThing = useMemo(() => {
    const map = new Map<string, DeviceLinkDto[]>();
    (data?.links ?? []).forEach((l) => {
      const list = map.get(l.thingName) ?? [];
      list.push(l);
      map.set(l.thingName, list);
    });
    return map;
  }, [data]);

  const spcById = useMemo(() => {
    const map = new Map<string, SpcDeviceRegistryDto>();
    (data?.spc ?? []).forEach((d) => map.set(d.spcDeviceId, d));
    return map;
  }, [data]);

  return (
    <div className="max-w-4xl mx-auto">
      <div className="flex items-center gap-3 mb-6">
        <Camera className="w-7 h-7 text-amber-600" />
        <h1 className="text-2xl font-bold text-gray-900">Device Registry</h1>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg flex items-center gap-2 text-red-700 text-sm">
          <AlertCircle className="w-4 h-4 flex-shrink-0" />
          {error}
        </div>
      )}
      {status && (
        <div className="mb-4 p-3 bg-green-50 border border-green-200 rounded-lg text-green-700 text-sm">
          {status}
        </div>
      )}

      {loading && !data ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-gray-400" />
        </div>
      ) : data == null ? null : (
        <div className="space-y-8">
          <section>
            <h2 className="text-lg font-semibold text-gray-900 mb-3">SnoutSpotter cameras</h2>
            {data.snoutSpotter.length === 0 ? (
              <p className="text-sm text-gray-500">No Pi cameras registered yet.</p>
            ) : (
              <div className="space-y-2">
                {data.snoutSpotter.map((d) => (
                  <SnoutSpotterRow
                    key={d.thingName}
                    device={d}
                    links={linksByThing.get(d.thingName) ?? []}
                    spcById={spcById}
                    onSave={async (displayName, notes) => {
                      await api.devices.updateSnoutSpotter(d.thingName, { displayName, notes });
                      setStatus(`Updated ${displayName}`);
                      await load();
                    }}
                    onManageLinks={() => setLinkTarget(d.thingName)}
                    onUnlink={async (spcDeviceId) => {
                      await api.devices.unlink(spcDeviceId, d.thingName);
                      setStatus("Link removed");
                      await load();
                    }}
                  />
                ))}
              </div>
            )}
          </section>

          <section>
            <h2 className="text-lg font-semibold text-gray-900 mb-3">Sure Pet Care devices</h2>
            {data.spc.length === 0 ? (
              <p className="text-sm text-gray-500">
                No Sure Pet Care devices yet. Link a camera to one of your SPC devices to add it
                here.
              </p>
            ) : (
              <div className="space-y-2">
                {data.spc.map((d) => (
                  <SpcRow
                    key={d.spcDeviceId}
                    device={d}
                    onSave={async (displayName, notes) => {
                      await api.devices.updateSpc(d.spcDeviceId, { displayName, notes });
                      setStatus(`Updated ${displayName}`);
                      await load();
                    }}
                    onRefresh={async () => {
                      await api.devices.refreshSpc(d.spcDeviceId);
                      setStatus("Refreshed from Sure Pet Care");
                      await load();
                    }}
                  />
                ))}
              </div>
            )}
          </section>
        </div>
      )}

      {linkTarget && data && (
        <LinkManagerDialog
          thingName={linkTarget}
          displayName={
            data.snoutSpotter.find((d) => d.thingName === linkTarget)?.displayName ?? linkTarget
          }
          currentLinks={linksByThing.get(linkTarget) ?? []}
          spcDevices={data.spc}
          onClose={() => setLinkTarget(null)}
          onChanged={async () => {
            await load();
          }}
        />
      )}
    </div>
  );
}

// Inline-editable row for a SnoutSpotter camera.
function SnoutSpotterRow({
  device,
  links,
  spcById,
  onSave,
  onManageLinks,
  onUnlink,
}: {
  device: SnoutSpotterDeviceDto;
  links: DeviceLinkDto[];
  spcById: Map<string, SpcDeviceRegistryDto>;
  onSave: (displayName: string, notes: string | null) => Promise<void>;
  onManageLinks: () => void;
  onUnlink: (spcDeviceId: string) => Promise<void>;
}) {
  return (
    <EditableRow
      icon={<Camera className="w-5 h-5 text-amber-600" />}
      idLabel={device.thingName}
      initialDisplayName={device.displayName}
      initialNotes={device.notes ?? ""}
      onSave={onSave}
      footer={
        <div className="mt-3 pt-3 border-t border-gray-100">
          <div className="flex items-center justify-between gap-3">
            <div className="text-xs text-gray-500">
              {links.length === 0
                ? "Not linked to any SPC devices."
                : `Linked to ${links.length} SPC device${links.length > 1 ? "s" : ""}.`}
            </div>
            <button
              onClick={onManageLinks}
              className="flex items-center gap-1 text-xs text-amber-700 hover:text-amber-800"
            >
              <Link2 className="w-3.5 h-3.5" />
              Manage links
            </button>
          </div>
          {links.length > 0 && (
            <ul className="mt-2 space-y-1">
              {links.map((l) => {
                const spc = spcById.get(l.spcDeviceId);
                return (
                  <li
                    key={l.spcDeviceId}
                    className="flex items-center gap-2 text-xs text-gray-600 bg-gray-50 rounded px-2 py-1"
                  >
                    <span className="flex-1 truncate">
                      {spc?.displayName ?? spc?.spcName ?? `SPC ${l.spcDeviceId}`}
                    </span>
                    <button
                      onClick={() => onUnlink(l.spcDeviceId)}
                      className="text-gray-400 hover:text-red-600"
                      title="Unlink"
                    >
                      <Link2Off className="w-3.5 h-3.5" />
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      }
    />
  );
}

// Inline-editable row for an SPC device.
function SpcRow({
  device,
  onSave,
  onRefresh,
}: {
  device: SpcDeviceRegistryDto;
  onSave: (displayName: string, notes: string | null) => Promise<void>;
  onRefresh: () => Promise<void>;
}) {
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const meta = spcProductMeta(device.spcProductId);
  const Icon = meta.icon;

  const handleRefresh = async () => {
    setRefreshing(true);
    setRefreshError(null);
    try {
      await onRefresh();
    } catch (e) {
      setRefreshError(e instanceof Error ? e.message : "Refresh failed");
    } finally {
      setRefreshing(false);
    }
  };

  return (
    <EditableRow
      icon={<Icon className="w-5 h-5 text-amber-600" />}
      idLabel={`${meta.label}${device.serialNumber ? ` · S/N ${device.serialNumber}` : ""}`}
      initialDisplayName={device.displayName}
      initialNotes={device.notes ?? ""}
      onSave={onSave}
      footer={
        <div className="mt-3 pt-3 border-t border-gray-100 flex items-center justify-between gap-3">
          <div className="text-xs text-gray-500">
            {device.lastRefreshedAt
              ? `Metadata refreshed ${new Date(device.lastRefreshedAt).toLocaleString()}`
              : "Metadata not yet refreshed from Sure Pet Care"}
          </div>
          <button
            onClick={handleRefresh}
            disabled={refreshing}
            className="flex items-center gap-1 text-xs text-amber-700 hover:text-amber-800 disabled:opacity-50"
          >
            {refreshing ? (
              <Loader2 className="w-3.5 h-3.5 animate-spin" />
            ) : (
              <RefreshCw className="w-3.5 h-3.5" />
            )}
            Refresh from SPC
          </button>
          {refreshError && (
            <div className="text-xs text-red-600 w-full mt-1">{refreshError}</div>
          )}
        </div>
      }
    />
  );
}

// Card with a display name (inline-edit), a notes textarea (inline-edit), and
// a custom footer. Save is only enabled when fields have changed.
function EditableRow({
  icon,
  idLabel,
  initialDisplayName,
  initialNotes,
  onSave,
  footer,
}: {
  icon: React.ReactNode;
  idLabel: string;
  initialDisplayName: string;
  initialNotes: string;
  onSave: (displayName: string, notes: string | null) => Promise<void>;
  footer?: React.ReactNode;
}) {
  const [editing, setEditing] = useState(false);
  const [displayName, setDisplayName] = useState(initialDisplayName);
  const [notes, setNotes] = useState(initialNotes);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    // Reset when underlying data changes (e.g. a refresh).
    setDisplayName(initialDisplayName);
    setNotes(initialNotes);
  }, [initialDisplayName, initialNotes]);

  const dirty = displayName !== initialDisplayName || notes !== initialNotes;

  const handleSave = async () => {
    if (!displayName.trim()) return;
    setSaving(true);
    setSaveError(null);
    try {
      await onSave(displayName.trim(), notes.trim() ? notes.trim() : null);
      setEditing(false);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : "Save failed");
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="bg-white border border-gray-200 rounded-lg p-4 shadow-sm">
      <div className="flex items-start gap-3">
        <div className="w-10 h-10 rounded-full bg-amber-100 flex items-center justify-center flex-shrink-0">
          {icon}
        </div>
        <div className="flex-1 min-w-0">
          {editing ? (
            <div className="space-y-2">
              <input
                type="text"
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
                placeholder="Display name"
                autoFocus
              />
              <textarea
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                rows={2}
                className="w-full px-3 py-1.5 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
                placeholder="Notes (optional)"
              />
              {saveError && <div className="text-xs text-red-600">{saveError}</div>}
              <div className="flex items-center gap-2">
                <button
                  onClick={handleSave}
                  disabled={saving || !dirty || !displayName.trim()}
                  className="flex items-center gap-1 px-3 py-1 bg-amber-600 text-white rounded-lg text-xs font-medium disabled:opacity-50"
                >
                  {saving ? <Loader2 className="w-3 h-3 animate-spin" /> : <Check className="w-3 h-3" />}
                  Save
                </button>
                <button
                  onClick={() => {
                    setDisplayName(initialDisplayName);
                    setNotes(initialNotes);
                    setEditing(false);
                    setSaveError(null);
                  }}
                  className="px-3 py-1 bg-gray-100 text-gray-700 rounded-lg text-xs font-medium"
                >
                  Cancel
                </button>
              </div>
            </div>
          ) : (
            <div>
              <div className="flex items-center gap-2">
                <h3 className="font-semibold text-gray-900 truncate">{displayName}</h3>
                <button
                  onClick={() => setEditing(true)}
                  className="text-gray-400 hover:text-gray-600"
                  title="Edit"
                >
                  <Pencil className="w-3.5 h-3.5" />
                </button>
              </div>
              <p className="text-xs text-gray-500 truncate font-mono">{idLabel}</p>
              {notes && (
                <p className="text-sm text-gray-600 mt-2 whitespace-pre-wrap">{notes}</p>
              )}
            </div>
          )}
          {footer}
        </div>
      </div>
    </div>
  );
}

// Modal drawer for toggling which SPC devices a given Pi watches.
//
// Fetches the live SPC device list directly from Sure Pet Care on open — we
// can't rely on the local registry table because it's populated lazily (rows
// only appear after a user has edited or linked something). For a fresh
// household the registry is empty but the user's SPC account already has
// bowls / pet doors / feeders, so we show those from the source.
//
// Registry rows for the same device are merged in so any custom display_name
// the user set locally wins over SPC's name.
function LinkManagerDialog({
  thingName,
  displayName,
  currentLinks,
  spcDevices,
  onClose,
  onChanged,
}: {
  thingName: string;
  displayName: string;
  currentLinks: DeviceLinkDto[];
  spcDevices: SpcDeviceRegistryDto[];
  onClose: () => void;
  onChanged: () => Promise<void>;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [spcUnavailable, setSpcUnavailable] = useState<string | null>(null);
  const [liveDevices, setLiveDevices] = useState<
    { spcDeviceId: string; displayName: string; spcProductId: number | null }[]
  >([]);
  const linked = useMemo(() => new Set(currentLinks.map((l) => l.spcDeviceId)), [currentLinks]);

  useEffect(() => {
    let cancelled = false;
    const registryByIdLocal = new Map(spcDevices.map((d) => [d.spcDeviceId, d] as const));

    (async () => {
      try {
        const resp = await api.spc.devices();
        if (cancelled) return;
        // Merge live + registry. Custom display_name (registry) wins so the
        // user's own labels persist even if SPC renames something.
        const merged = resp.devices.map((live) => {
          const registry = registryByIdLocal.get(live.id);
          return {
            spcDeviceId: live.id,
            displayName: registry?.displayName ?? live.name ?? `SPC ${live.id}`,
            spcProductId: registry?.spcProductId ?? live.productId,
          };
        });
        setLiveDevices(merged);
      } catch (e) {
        if (cancelled) return;
        // Fall back to whatever the registry has — better than nothing.
        setSpcUnavailable(
          e instanceof Error ? e.message : "Could not reach Sure Pet Care",
        );
        setLiveDevices(
          spcDevices.map((d) => ({
            spcDeviceId: d.spcDeviceId,
            displayName: d.displayName,
            spcProductId: d.spcProductId,
          })),
        );
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
    // spcDevices is a stable list from the parent; we only want to run this
    // on open, not on every registry reload.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const toggle = async (spcDeviceId: string) => {
    setBusy(spcDeviceId);
    setError(null);
    try {
      if (linked.has(spcDeviceId)) {
        await api.devices.unlink(spcDeviceId, thingName);
      } else {
        await api.devices.link(spcDeviceId, thingName);
      }
      await onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Link change failed");
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full">
        <div className="flex items-center justify-between p-4 border-b border-gray-100">
          <div>
            <h3 className="font-semibold text-gray-900">Manage links</h3>
            <p className="text-xs text-gray-500 font-mono">{displayName}</p>
          </div>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="p-4 max-h-96 overflow-y-auto">
          {error && (
            <div className="mb-3 p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700">
              {error}
            </div>
          )}
          {spcUnavailable && (
            <div className="mb-3 p-2 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800">
              Could not reach Sure Pet Care — {spcUnavailable}. Showing locally-known devices only.
            </div>
          )}
          {loading ? (
            <div className="flex items-center gap-2 text-sm text-gray-500 py-6">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading Sure Pet Care devices&hellip;
            </div>
          ) : liveDevices.length === 0 ? (
            <p className="text-sm text-gray-500">
              No Sure Pet Care devices found. Open the Integrations page to connect your SPC account
              first.
            </p>
          ) : (
            <ul className="space-y-1">
              {liveDevices.map((d) => {
                const isLinked = linked.has(d.spcDeviceId);
                const meta = spcProductMeta(d.spcProductId);
                const Icon = meta.icon;
                return (
                  <li key={d.spcDeviceId}>
                    <button
                      onClick={() => toggle(d.spcDeviceId)}
                      disabled={busy === d.spcDeviceId}
                      className={`w-full flex items-center gap-3 p-2 rounded-lg border text-left transition-colors disabled:opacity-50 ${
                        isLinked
                          ? "bg-amber-50 border-amber-200"
                          : "bg-white border-gray-200 hover:bg-gray-50"
                      }`}
                    >
                      <Icon className="w-4 h-4 text-amber-600 flex-shrink-0" />
                      <div className="flex-1 min-w-0">
                        <div className="text-sm font-medium text-gray-900 truncate">
                          {d.displayName}
                        </div>
                        <div className="text-xs text-gray-500 truncate">{meta.label}</div>
                      </div>
                      {busy === d.spcDeviceId ? (
                        <Loader2 className="w-4 h-4 animate-spin text-gray-400" />
                      ) : isLinked ? (
                        <Link2 className="w-4 h-4 text-amber-700" />
                      ) : (
                        <Link2Off className="w-4 h-4 text-gray-400" />
                      )}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}
