import { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft, Loader2, CheckCircle, XCircle, Clock, Send } from "lucide-react";
import { formatDistanceToNow } from "date-fns";
import { api } from "../api";

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    success: "bg-green-50 text-green-700",
    failed: "bg-red-50 text-red-700",
    sent: "bg-blue-50 text-blue-700",
  };
  const icons: Record<string, React.ReactNode> = {
    success: <CheckCircle className="w-3 h-3" />,
    failed: <XCircle className="w-3 h-3" />,
    sent: <Send className="w-3 h-3" />,
  };
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] || "bg-gray-50 text-gray-700"}`}>
      {icons[status] || <Clock className="w-3 h-3" />}
      {status}
    </span>
  );
}

export default function CommandHistory() {
  const { thingName } = useParams<{ thingName: string }>();
  const [commands, setCommands] = useState<Record<string, string>[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!thingName) return;
    api.getCommandHistory(thingName)
      .then((data) => setCommands(data.commands))
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false));
  }, [thingName]);

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-gray-400">
        <Loader2 className="w-4 h-4 animate-spin" />
        Loading command history...
      </div>
    );
  }

  if (error) {
    return (
      <div>
        <Link to="/health" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
          <ArrowLeft className="w-4 h-4" /> System Health
        </Link>
        <div className="text-red-600 bg-red-50 p-4 rounded-lg">{error}</div>
      </div>
    );
  }

  return (
    <div className="max-w-3xl">
      <Link to="/health" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> System Health
      </Link>

      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Command History</h1>
        <p className="text-sm text-gray-500 mt-1">{thingName}</p>
      </div>

      {commands.length === 0 ? (
        <div className="bg-gray-50 rounded-xl border border-gray-200 p-8 text-center">
          <Clock className="w-12 h-12 text-gray-300 mx-auto mb-3" />
          <p className="text-gray-500">No commands sent to this device yet</p>
        </div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b border-gray-200">
              <tr>
                <th className="text-left px-4 py-3 text-xs font-medium text-gray-500 uppercase">Action</th>
                <th className="text-left px-4 py-3 text-xs font-medium text-gray-500 uppercase">Status</th>
                <th className="text-left px-4 py-3 text-xs font-medium text-gray-500 uppercase">Message</th>
                <th className="text-left px-4 py-3 text-xs font-medium text-gray-500 uppercase">Sent</th>
                <th className="text-left px-4 py-3 text-xs font-medium text-gray-500 uppercase">Completed</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {commands.map((cmd) => (
                <tr key={cmd.command_id} className="hover:bg-gray-50">
                  <td className="px-4 py-3 font-medium text-gray-900">{cmd.action}</td>
                  <td className="px-4 py-3"><StatusBadge status={cmd.status} /></td>
                  <td className="px-4 py-3 text-gray-600">{cmd.message || cmd.error || "—"}</td>
                  <td className="px-4 py-3 text-gray-400 text-xs">
                    {cmd.requested_at ? formatDistanceToNow(new Date(cmd.requested_at), { addSuffix: true }) : "—"}
                  </td>
                  <td className="px-4 py-3 text-gray-400 text-xs">
                    {cmd.completed_at ? formatDistanceToNow(new Date(cmd.completed_at), { addSuffix: true }) : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
