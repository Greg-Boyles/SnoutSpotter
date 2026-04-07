import { Dog, Ban } from "lucide-react";

export function LabelBadge({ label, type }: { label: string; type: "auto" | "confirmed" }) {
  const isDog = label === "dog" || label === "my_dog";
  const isOtherDog = label === "other_dog";
  const color = type === "confirmed"
    ? isDog ? "bg-green-100 text-green-800" : isOtherDog ? "bg-orange-100 text-orange-800" : "bg-gray-100 text-gray-700"
    : isDog ? "bg-amber-50 text-amber-700" : "bg-gray-50 text-gray-500";
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${color}`}>
      {isDog ? <Dog className="w-3 h-3" /> : <Ban className="w-3 h-3" />}
      {label}
    </span>
  );
}

export function DetectionBadge({ label, type }: { label: string; type: "inference" | "auto" | "confirmed" }) {
  const colors =
    label === "my_dog" ? "bg-amber-100 text-amber-700" :
    label === "other_dog" ? "bg-blue-100 text-blue-700" :
    label === "dog" ? "bg-amber-50 text-amber-600" :
    "bg-gray-100 text-gray-600";
  const prefix = type === "inference" ? "Detected" : type === "confirmed" ? "Confirmed" : "Auto";
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${colors}`}>
      {prefix}: {label}
    </span>
  );
}
