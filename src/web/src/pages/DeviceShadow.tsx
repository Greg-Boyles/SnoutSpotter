import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ArrowLeft, RefreshCw, Copy, Check } from "lucide-react";
import { api } from "../api";

export default function DeviceShadow() {
  const { thingName } = useParams<{ thingName: string }>();
  const [shadow, setShadow] = useState<unknown>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [copied, setCopied] = useState(false);

  const fetchShadow = () => {
    if (!thingName) return;
    setLoading(true);
    api.getRawShadow(thingName)
      .then((data) => { setShadow(data); setError(null); })
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  };

  useEffect(fetchShadow, [thingName]);

  const jsonString = shadow ? JSON.stringify(shadow, null, 2) : "";

  const handleCopy = () => {
    navigator.clipboard.writeText(jsonString);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="max-w-4xl">
      <Link to={`/device/${thingName}`} className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> {thingName}
      </Link>

      <div className="flex items-center justify-between mb-4">
        <h1 className="text-2xl font-bold text-gray-900">Device Shadow</h1>
        <div className="flex items-center gap-2">
          <button
            onClick={handleCopy}
            disabled={!shadow}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-gray-600 bg-white border border-gray-200 hover:bg-gray-50 rounded-lg disabled:opacity-50"
          >
            {copied ? <Check className="w-3.5 h-3.5 text-green-600" /> : <Copy className="w-3.5 h-3.5" />}
            {copied ? "Copied" : "Copy"}
          </button>
          <button
            onClick={fetchShadow}
            disabled={loading}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-gray-600 bg-white border border-gray-200 hover:bg-gray-50 rounded-lg disabled:opacity-50"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${loading ? "animate-spin" : ""}`} />
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="text-red-600 bg-red-50 p-4 rounded-lg mb-4">{error}</div>
      )}

      {loading && !shadow ? (
        <div className="text-gray-400">Loading...</div>
      ) : shadow ? (
        <pre className="bg-gray-900 text-green-400 p-5 rounded-xl text-xs font-mono overflow-x-auto max-h-[75vh] overflow-y-auto">
          {jsonString}
        </pre>
      ) : null}
    </div>
  );
}
