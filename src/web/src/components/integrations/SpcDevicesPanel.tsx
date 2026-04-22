import { useEffect, useState } from "react";
import { Loader2, AlertCircle, Utensils, Droplet, DoorOpen, Wifi, Box, Radio, Cpu, Activity } from "lucide-react";
import { api, SpcApiError } from "../../api";

interface Device {
  id: string;
  productId: number;
  name: string;
  serialNumber: string | null;
  lastActivityAt: string | null;
}

// SPC product_id lookup. Matches Sure Pet Care's DeviceType enum.
// Unknown product_ids fall back to a generic box icon so we never break the panel.
const PRODUCT_LABELS: Record<number, { label: string; icon: React.ElementType }> = {
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

export default function SpcDevicesPanel() {
  const [devices, setDevices] = useState<Device[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const res = await api.spc.devices();
        if (!cancelled) setDevices(res.devices);
      } catch (e) {
        if (!cancelled) {
          if (e instanceof SpcApiError && e.code === "token_expired") {
            setError("Your stored Sure Pet Care session has expired. Re-link the account to refresh.");
          } else if (e instanceof SpcApiError) {
            setError(`Could not load devices: ${e.code}`);
          } else {
            setError(e instanceof Error ? e.message : "Could not load devices");
          }
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-sm text-gray-500 py-3">
        <Loader2 className="w-4 h-4 animate-spin" /> Loading Sure Pet Care devices&hellip;
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-3 bg-amber-50 border border-amber-200 rounded-lg flex items-start gap-2 text-amber-900 text-sm">
        <AlertCircle className="w-4 h-4 flex-shrink-0 mt-0.5" />
        <span>{error}</span>
      </div>
    );
  }

  if (!devices || devices.length === 0) {
    return <p className="text-sm text-gray-500 italic py-2">No Sure Pet Care devices found on this household.</p>;
  }

  return (
    <div className="space-y-1.5">
      {devices.map((d) => {
        const meta = PRODUCT_LABELS[d.productId] ?? { label: `Product ${d.productId}`, icon: Box };
        const Icon = meta.icon;
        return (
          <div key={d.id} className="flex items-center gap-3 p-2 bg-gray-50 rounded-lg">
            <Icon className="w-4 h-4 text-amber-600 flex-shrink-0" />
            <div className="flex-1 min-w-0">
              <div className="text-sm font-medium text-gray-900 truncate">{d.name}</div>
              <div className="text-xs text-gray-500 truncate">
                {meta.label}
                {d.serialNumber ? ` · S/N ${d.serialNumber}` : ""}
              </div>
            </div>
            {d.lastActivityAt && (
              <div className="text-xs text-gray-400 flex-shrink-0">
                {new Date(d.lastActivityAt).toLocaleString()}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
