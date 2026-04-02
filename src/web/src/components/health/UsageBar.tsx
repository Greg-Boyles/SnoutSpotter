export default function UsageBar({ percent, color }: { percent: number; color: string }) {
  return (
    <div className="w-full h-1.5 bg-gray-100 rounded-full overflow-hidden">
      <div className={`h-full rounded-full ${color}`} style={{ width: `${Math.min(percent, 100)}%` }} />
    </div>
  );
}
