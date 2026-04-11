import { useEffect, useState } from "react";
import { Settings as SettingsIcon, Save, RotateCcw, Loader2, CheckCircle } from "lucide-react";
import { api } from "../api";

type Setting = {
  key: string;
  value: string;
  default: string;
  label: string;
  type: string;
  min: number;
  max: number;
  description: string;
  options?: string[];
};

const SECTIONS = [
  {
    title: "Clip Ingest",
    description: "Controls how keyframes are extracted from uploaded clips",
    prefix: "ingest.",
  },
  {
    title: "Inference",
    description: "Controls the dog detection model parameters",
    prefix: "inference.",
  },
  {
    title: "Auto-Label",
    description: "Controls automatic dog detection on new keyframes",
    prefix: "autolabel.",
  },
  {
    title: "Dataset Export",
    description: "Controls training dataset generation",
    prefix: "export.",
  },
];

export default function ServerSettings() {
  const [settings, setSettings] = useState<Setting[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  const loadSettings = async () => {
    try {
      const data = await api.getSettings();
      setSettings(data.settings);
      const d: Record<string, string> = {};
      data.settings.forEach((s: Setting) => { d[s.key] = s.value; });
      setDraft(d);
    } catch (e) {
      setMessage({ text: (e as Error).message, error: true });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { loadSettings(); }, []);

  const hasChanges = () =>
    settings.some((s) => draft[s.key] !== s.value);

  const handleSave = async () => {
    setSaving(true);
    setMessage(null);
    const errors: string[] = [];
    let saved = 0;

    for (const s of settings) {
      if (draft[s.key] !== s.value) {
        try {
          await api.updateSetting(s.key, draft[s.key]);
          saved++;
        } catch (e) {
          errors.push(`${s.key}: ${(e as Error).message}`);
        }
      }
    }

    if (errors.length > 0) {
      setMessage({ text: errors.join("; "), error: true });
    } else {
      setMessage({ text: `${saved} setting${saved !== 1 ? "s" : ""} saved`, error: false });
    }

    await loadSettings();
    setSaving(false);
  };

  const handleReset = async () => {
    if (!window.confirm("Reset all settings to defaults? This cannot be undone.")) return;
    setResetting(true);
    setMessage(null);
    try {
      await api.resetSettings();
      setMessage({ text: "All settings reset to defaults", error: false });
      await loadSettings();
    } catch (e) {
      setMessage({ text: (e as Error).message, error: true });
    }
    setResetting(false);
  };

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-gray-400">
        <Loader2 className="w-4 h-4 animate-spin" /> Loading settings...
      </div>
    );
  }

  return (
    <div className="max-w-2xl">
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Server Settings</h1>
          <p className="text-sm text-gray-500 mt-1">Configure Lambda processing parameters</p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={handleReset}
            disabled={resetting}
            className="inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-gray-600 border border-gray-200 hover:bg-gray-50 rounded-lg disabled:opacity-50"
          >
            {resetting ? <Loader2 className="w-4 h-4 animate-spin" /> : <RotateCcw className="w-4 h-4" />}
            Reset
          </button>
          <button
            onClick={handleSave}
            disabled={saving || !hasChanges()}
            className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
          >
            {saving ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />}
            {saving ? "Saving..." : "Save Changes"}
          </button>
        </div>
      </div>

      {message && (
        <div className={`mb-4 p-3 rounded-lg text-sm flex items-center gap-2 ${message.error ? "bg-red-50 text-red-700" : "bg-green-50 text-green-700"}`}>
          {!message.error && <CheckCircle className="w-4 h-4" />}
          {message.text}
        </div>
      )}

      <div className="space-y-6">
        {SECTIONS.map((section) => {
          const sectionSettings = settings.filter((s) => s.key.startsWith(section.prefix));
          if (sectionSettings.length === 0) return null;

          return (
            <div key={section.prefix} className="bg-white rounded-xl border border-gray-200 p-5">
              <div className="flex items-center gap-2 mb-1">
                <SettingsIcon className="w-4 h-4 text-gray-400" />
                <h2 className="text-sm font-semibold text-gray-900">{section.title}</h2>
              </div>
              <p className="text-xs text-gray-400 mb-4">{section.description}</p>

              <div className="space-y-4">
                {sectionSettings.map((s) => {
                  const changed = draft[s.key] !== s.value;
                  const isSelect = s.type === "select";
                  return (
                    <div key={s.key}>
                      <div className="flex items-center justify-between gap-4">
                        <div className="flex-1 min-w-0">
                          <label className="text-sm font-medium text-gray-700">{s.label}</label>
                          <p className="text-xs text-gray-400 mt-0.5">{s.description}</p>
                        </div>
                        <div className="flex items-center gap-2 shrink-0">
                          {isSelect ? (
                            <select
                              value={draft[s.key] ?? s.default}
                              onChange={(e) => setDraft((prev) => ({ ...prev, [s.key]: e.target.value }))}
                              className={`px-2.5 py-1.5 text-sm border rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 ${
                                changed ? "border-blue-400 bg-blue-50" : "border-gray-300"
                              }`}
                            >
                              {(s.options ?? []).map((opt) => (
                                <option key={opt} value={opt}>{opt}</option>
                              ))}
                            </select>
                          ) : (
                            <input
                              type="number"
                              step={s.type === "float" ? 0.01 : 1}
                              min={s.min}
                              max={s.max}
                              value={draft[s.key] ?? s.default}
                              onChange={(e) => setDraft((prev) => ({ ...prev, [s.key]: e.target.value }))}
                              className={`w-24 px-2.5 py-1.5 text-sm border rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 ${
                                changed ? "border-blue-400 bg-blue-50" : "border-gray-300"
                              }`}
                            />
                          )}
                        </div>
                      </div>
                      <p className="text-xs text-gray-300 mt-1">
                        {isSelect ? `Default: ${s.default}` : `Range: ${s.min}–${s.max} · Default: ${s.default}`}
                      </p>
                    </div>
                  );
                })}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
