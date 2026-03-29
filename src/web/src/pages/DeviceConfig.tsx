import { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { ArrowLeft, Save, Loader2 } from "lucide-react";
import { api } from "../api";

const CONFIG_SECTIONS: {
  title: string;
  keys: { key: string; label: string; type: "int" | "bool"; min?: number; max?: number; unit?: string; description: string }[];
}[] = [
  {
    title: "Motion Detection",
    keys: [
      { key: "motion.threshold", label: "Threshold", type: "int", min: 500, max: 50000, unit: "px", description: "Number of changed pixels to trigger recording" },
      { key: "motion.blur_kernel", label: "Blur kernel", type: "int", min: 3, max: 51, description: "Gaussian blur kernel size (must be odd)" },
    ],
  },
  {
    title: "Camera",
    keys: [
      { key: "camera.detection_fps", label: "Detection FPS", type: "int", min: 1, max: 15, unit: "fps", description: "Frame rate for motion detection preview" },
    ],
  },
  {
    title: "Recording",
    keys: [
      { key: "recording.max_clip_length", label: "Max clip length", type: "int", min: 10, max: 300, unit: "s", description: "Maximum recording duration per clip" },
      { key: "recording.post_motion_buffer", label: "Post-motion buffer", type: "int", min: 3, max: 60, unit: "s", description: "Keep recording after last motion detected" },
    ],
  },
  {
    title: "Upload",
    keys: [
      { key: "upload.max_retries", label: "Max retries", type: "int", min: 1, max: 20, description: "Retry count for failed uploads" },
      { key: "upload.delete_after_upload", label: "Delete after upload", type: "bool", description: "Remove local clip file after successful upload" },
    ],
  },
  {
    title: "Agent",
    keys: [
      { key: "health.interval_seconds", label: "Heartbeat interval", type: "int", min: 60, max: 3600, unit: "s", description: "How often the agent reports health to the cloud" },
    ],
  },
];

export default function DeviceConfig() {
  const { thingName } = useParams<{ thingName: string }>();
  const [config, setConfig] = useState<Record<string, number | boolean> | null>(null);
  const [configErrors, setConfigErrors] = useState<Record<string, string> | null>(null);
  const [draft, setDraft] = useState<Record<string, number | boolean>>({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saveMessage, setSaveMessage] = useState<{ text: string; error: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadConfig = async () => {
    if (!thingName) return;
    try {
      const data = await api.getPiConfig(thingName);
      setConfig(data.config || {});
      setConfigErrors(data.configErrors || null);
      setDraft(data.config || {});
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadConfig();
  }, [thingName]);

  const updateDraft = (key: string, value: number | boolean) => {
    setDraft((prev) => ({ ...prev, [key]: value }));
  };

  const hasChanges = () => {
    if (!config) return false;
    return Object.keys(draft).some((key) => draft[key] !== config[key]);
  };

  const handleSave = async () => {
    if (!thingName || !config) return;
    const changes: Record<string, number | boolean> = {};
    for (const [key, value] of Object.entries(draft)) {
      if (value !== config[key]) {
        changes[key] = value;
      }
    }
    if (Object.keys(changes).length === 0) return;

    setSaving(true);
    setSaveMessage(null);
    try {
      const result = await api.updatePiConfig(thingName, changes);
      const errCount = Object.keys(result.errors || {}).length;
      if (errCount > 0) {
        setSaveMessage({ text: `Saved with ${errCount} validation error(s)`, error: true });
      } else {
        setSaveMessage({ text: "Changes saved. Applying to device...", error: false });
      }
      // Refresh config after Pi applies changes
      setTimeout(loadConfig, 8000);
      setTimeout(loadConfig, 20000);
    } catch (e) {
      setSaveMessage({ text: `Save failed: ${(e as Error).message}`, error: true });
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-gray-400">
        <Loader2 className="w-4 h-4 animate-spin" />
        Loading config...
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
    <div className="max-w-2xl">
      <Link to="/health" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> System Health
      </Link>

      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Device Settings</h1>
          <p className="text-sm text-gray-500 mt-1">{thingName}</p>
        </div>
        <button
          onClick={handleSave}
          disabled={saving || !hasChanges()}
          className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {saving ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />}
          {saving ? "Saving..." : "Save Changes"}
        </button>
      </div>

      {saveMessage && (
        <div className={`mb-4 p-3 rounded-lg text-sm ${saveMessage.error ? "bg-red-50 text-red-700" : "bg-blue-50 text-blue-700"}`}>
          {saveMessage.text}
        </div>
      )}

      <div className="space-y-6">
        {CONFIG_SECTIONS.map((section) => (
          <div key={section.title} className="bg-white rounded-xl border border-gray-200 p-5">
            <h2 className="text-sm font-semibold text-gray-900 mb-4">{section.title}</h2>
            <div className="space-y-4">
              {section.keys.map(({ key, label, type, min, max, unit, description }) => {
                const fieldError = configErrors?.[key];
                const value = draft[key];
                const changed = config && value !== config[key];

                return (
                  <div key={key}>
                    <div className="flex items-center justify-between gap-4">
                      <div className="flex-1 min-w-0">
                        <label className="text-sm font-medium text-gray-700">{label}</label>
                        <p className="text-xs text-gray-400 mt-0.5">{description}</p>
                      </div>
                      <div className="flex items-center gap-2 shrink-0">
                        {type === "bool" ? (
                          <button
                            type="button"
                            onClick={() => updateDraft(key, !value)}
                            className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                              value ? "bg-blue-600" : "bg-gray-200"
                            }`}
                          >
                            <span
                              className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                                value ? "translate-x-6" : "translate-x-1"
                              }`}
                            />
                          </button>
                        ) : (
                          <div className="flex items-center gap-1">
                            <input
                              type="number"
                              min={min}
                              max={max}
                              value={value as number ?? ""}
                              onChange={(e) => updateDraft(key, Number(e.target.value))}
                              className={`w-24 px-2.5 py-1.5 text-sm border rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 ${
                                changed ? "border-blue-400 bg-blue-50" : "border-gray-300"
                              }`}
                            />
                            {unit && <span className="text-xs text-gray-400">{unit}</span>}
                          </div>
                        )}
                      </div>
                    </div>
                    {fieldError && (
                      <p className="text-xs text-red-600 mt-1">{fieldError}</p>
                    )}
                    {type === "int" && min != null && max != null && (
                      <p className="text-xs text-gray-300 mt-1">Range: {min} - {max}</p>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
