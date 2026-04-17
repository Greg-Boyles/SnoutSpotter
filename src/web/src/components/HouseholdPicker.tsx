import { useState } from "react";
import { Dog, Home, Plus } from "lucide-react";
import { api } from "../api";
import type { Household } from "../hooks/useHousehold";

type Props = {
  households: Household[];
  onSelect: (id: string) => void;
  onCreated: () => void;
};

export default function HouseholdPicker({ households, onSelect, onCreated }: Props) {
  const [creating, setCreating] = useState(households.length === 0);
  const [name, setName] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleCreate = async () => {
    if (!name.trim()) return;
    setSubmitting(true);
    setError(null);
    try {
      const hh = await api.createHousehold(name.trim());
      localStorage.setItem("activeHouseholdId", hh.householdId);
      onCreated();
      onSelect(hh.householdId);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="flex items-center justify-center min-h-screen bg-gray-50">
      <div className="w-full max-w-sm p-8 bg-white rounded-2xl border border-gray-200 shadow-sm">
        <div className="flex items-center gap-2 mb-6">
          <Dog className="w-7 h-7 text-amber-600" />
          <span className="text-lg font-bold text-gray-900">SnoutSpotter</span>
        </div>

        {!creating && households.length > 0 ? (
          <>
            <h2 className="text-sm font-semibold text-gray-900 mb-4">Choose a household</h2>
            <div className="space-y-2">
              {households.map((hh) => (
                <button
                  key={hh.householdId}
                  onClick={() => onSelect(hh.householdId)}
                  className="flex items-center gap-3 w-full px-4 py-3 rounded-lg border border-gray-200 text-left hover:bg-amber-50 hover:border-amber-200 transition-colors"
                >
                  <Home className="w-5 h-5 text-gray-400" />
                  <div>
                    <p className="text-sm font-medium text-gray-900">{hh.name}</p>
                    <p className="text-xs text-gray-400">{hh.householdId}</p>
                  </div>
                </button>
              ))}
            </div>
            <button
              onClick={() => setCreating(true)}
              className="flex items-center gap-2 mt-4 text-sm text-amber-600 hover:text-amber-700"
            >
              <Plus className="w-4 h-4" />
              Create new household
            </button>
          </>
        ) : (
          <>
            <h2 className="text-sm font-semibold text-gray-900 mb-1">
              {households.length === 0 ? "Welcome! Create your household" : "Create a new household"}
            </h2>
            <p className="text-xs text-gray-500 mb-4">
              {households.length === 0
                ? "You need a household to get started."
                : "Give your household a name."}
            </p>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && handleCreate()}
              placeholder="e.g. Smith Family"
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-transparent"
              autoFocus
            />
            {error && <p className="text-xs text-red-600 mt-2">{error}</p>}
            <div className="flex items-center gap-3 mt-4">
              <button
                onClick={handleCreate}
                disabled={submitting || !name.trim()}
                className="px-4 py-2 text-sm font-medium text-white bg-amber-600 hover:bg-amber-700 rounded-lg disabled:opacity-50"
              >
                {submitting ? "Creating..." : "Create"}
              </button>
              {households.length > 0 && (
                <button
                  onClick={() => setCreating(false)}
                  className="text-sm text-gray-500 hover:text-gray-700"
                >
                  Back
                </button>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
