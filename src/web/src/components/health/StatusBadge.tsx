export default function StatusBadge({ ok, label }: { ok: boolean; label: string }) {
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
