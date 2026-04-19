import { useState } from "react";
import { PawPrint, CheckCircle2, AlertTriangle, Circle, ChevronDown, ChevronUp } from "lucide-react";
import SpcDevicesPanel from "./SpcDevicesPanel";

type Status = "unlinked" | "linked" | "token_expired" | "error";

interface Props {
  status: Status;
  spcUserEmail: string | null;
  spcHouseholdName: string | null;
  linkedAt: string | null;
  onConfigure: () => void;
  onManage: () => void;
}

export default function SpcConnectorCard({ status, spcUserEmail, spcHouseholdName, linkedAt, onConfigure, onManage }: Props) {
  const [devicesOpen, setDevicesOpen] = useState(false);

  const badge = statusBadge(status);
  const Icon = badge.icon;

  return (
    <div className="bg-white border border-gray-200 rounded-lg p-5 shadow-sm">
      <div className="flex items-start gap-4">
        <div className="w-11 h-11 rounded-lg bg-amber-100 flex items-center justify-center flex-shrink-0">
          <PawPrint className="w-6 h-6 text-amber-600" />
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <h3 className="text-lg font-semibold text-gray-900">Sure Pet Care</h3>
            <span className={`inline-flex items-center gap-1 text-xs px-2 py-0.5 rounded-full ${badge.chipClass}`}>
              <Icon className="w-3 h-3" />
              {badge.text}
            </span>
          </div>
          <p className="text-sm text-gray-600 mt-1">
            Link SnoutSpotter pets to their Sure Pet Care counterparts so feeding, water and pet-door
            events can be attributed to the right dog.
          </p>
          {status === "linked" && (
            <div className="mt-3 text-xs text-gray-500 space-y-0.5">
              {spcHouseholdName && <div>Linked to <span className="font-medium text-gray-700">{spcHouseholdName}</span>{spcUserEmail ? ` via ${spcUserEmail}` : ""}</div>}
              {linkedAt && <div>Linked {new Date(linkedAt).toLocaleString()}</div>}
            </div>
          )}
          {status === "token_expired" && (
            <p className="mt-3 text-sm text-amber-700">
              Your Sure Pet Care session has expired. Click Re-link to sign in again.
            </p>
          )}
          <div className="mt-4 flex gap-2">
            {status === "unlinked" && (
              <button
                onClick={onConfigure}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 text-sm font-medium"
              >
                Configure
              </button>
            )}
            {status === "linked" && (
              <>
                <button
                  onClick={onManage}
                  className="px-4 py-2 bg-white border border-gray-300 text-gray-700 rounded-lg hover:bg-gray-50 text-sm font-medium"
                >
                  Manage
                </button>
                <button
                  onClick={() => setDevicesOpen((v) => !v)}
                  className="px-4 py-2 bg-white border border-gray-300 text-gray-700 rounded-lg hover:bg-gray-50 text-sm font-medium flex items-center gap-1"
                >
                  Devices
                  {devicesOpen ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                </button>
              </>
            )}
            {status === "token_expired" && (
              <button
                onClick={onConfigure}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 text-sm font-medium"
              >
                Re-link
              </button>
            )}
            {status === "error" && (
              <button
                onClick={onConfigure}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 text-sm font-medium"
              >
                Re-link
              </button>
            )}
          </div>
          {status === "linked" && devicesOpen && (
            <div className="mt-4 pt-4 border-t border-gray-100">
              <SpcDevicesPanel />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function statusBadge(status: Status): { text: string; chipClass: string; icon: React.ElementType } {
  switch (status) {
    case "linked":
      return { text: "Connected", chipClass: "bg-green-50 text-green-700 border border-green-200", icon: CheckCircle2 };
    case "token_expired":
      return { text: "Re-link required", chipClass: "bg-amber-50 text-amber-700 border border-amber-200", icon: AlertTriangle };
    case "error":
      return { text: "Error", chipClass: "bg-red-50 text-red-700 border border-red-200", icon: AlertTriangle };
    case "unlinked":
    default:
      return { text: "Not connected", chipClass: "bg-gray-50 text-gray-600 border border-gray-200", icon: Circle };
  }
}
