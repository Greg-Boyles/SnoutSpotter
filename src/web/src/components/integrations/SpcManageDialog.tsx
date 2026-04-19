import { useEffect, useMemo, useState } from "react";
import { X, Loader2, AlertCircle, Trash2 } from "lucide-react";
import { api, SpcApiError } from "../../api";
import type { Pet } from "../../types";

type SpcPet = { id: string; name: string; species: string | null; photoUrl: string | null };

interface Props {
  open: boolean;
  onClose: () => void;
  onChanged: () => void;
  pets: Pet[];
  spcHouseholdId: string;
  spcHouseholdName: string;
}

// Edits the pet mapping for an already-linked household, plus an unlink action.
// We re-use a validated session by asking the user to re-enter credentials only
// when they want to change which SPC pets are available — for mapping edits
// alone we read the cached spc_pet_name from the existing Pet records.
export default function SpcManageDialog({ open, onClose, onChanged, pets, spcHouseholdId, spcHouseholdName }: Props) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reauthEmail, setReauthEmail] = useState("");
  const [reauthPassword, setReauthPassword] = useState("");
  const [spcPets, setSpcPets] = useState<SpcPet[]>([]);
  const [mapByPet, setMapByPet] = useState<Record<string, string>>({});
  const [confirmUnlink, setConfirmUnlink] = useState(false);

  useEffect(() => {
    if (!open) {
      setBusy(false);
      setError(null);
      setReauthEmail("");
      setReauthPassword("");
      setSpcPets([]);
      setMapByPet({});
      setConfirmUnlink(false);
      return;
    }
    // Seed the map from existing pet records.
    const initial: Record<string, string> = {};
    for (const p of pets) initial[p.petId] = p.spcPetId ?? "";
    setMapByPet(initial);
  }, [open, pets]);

  const duplicateSpcIds = useMemo(() => {
    const counts: Record<string, number> = {};
    Object.values(mapByPet).forEach((id) => {
      if (id) counts[id] = (counts[id] ?? 0) + 1;
    });
    return new Set(Object.entries(counts).filter(([, c]) => c > 1).map(([id]) => id));
  }, [mapByPet]);

  const loadSpcPets = async () => {
    if (!reauthEmail.trim() || !reauthPassword) return;
    setBusy(true);
    setError(null);
    try {
      const v = await api.spc.validate(reauthEmail.trim(), reauthPassword);
      const res = await api.spc.listSpcPets(v.sessionId, spcHouseholdId);
      setSpcPets(res.pets);
    } catch (e) {
      setError(spcErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const handleSave = async () => {
    setBusy(true);
    setError(null);
    try {
      // If the user never reloaded the SPC pet list, we still have the cached
      // spcPetName on each Pet — but we only need it for display, so fall back
      // to the name captured at link time if spcPets is empty.
      const cachedNameById: Record<string, string> = {};
      for (const p of pets) {
        if (p.spcPetId && p.spcPetName) cachedNameById[p.spcPetId] = p.spcPetName;
      }
      const mappings = pets.map((p) => {
        const spcId = mapByPet[p.petId];
        if (!spcId) return { petId: p.petId, spcPetId: null, spcPetName: null };
        const fresh = spcPets.find((sp) => sp.id === spcId);
        return {
          petId: p.petId,
          spcPetId: spcId,
          spcPetName: fresh?.name ?? cachedNameById[spcId] ?? null,
        };
      });
      await api.spc.updatePetLinks(mappings);
      onChanged();
      onClose();
    } catch (e) {
      setError(spcErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const handleUnlink = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.spc.unlink();
      onChanged();
      onClose();
    } catch (e) {
      setError(spcErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  if (!open) return null;

  const availableSpcPets = spcPets.length > 0
    ? spcPets
    : // Fall back to whatever is cached on our pet records so the user can at
      // least remove an existing mapping without re-authenticating.
      pets
        .filter((p): p is Pet & { spcPetId: string; spcPetName: string } => !!p.spcPetId && !!p.spcPetName)
        .map((p) => ({ id: p.spcPetId, name: p.spcPetName, species: null, photoUrl: null }));

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white rounded-lg p-6 max-w-2xl w-full mx-4 max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <div>
            <h3 className="text-lg font-semibold text-gray-900">Manage Sure Pet Care link</h3>
            <p className="text-xs text-gray-500 mt-0.5">Linked to {spcHouseholdName}</p>
          </div>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <X className="w-5 h-5" />
          </button>
        </div>

        {error && (
          <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg flex items-start gap-2 text-red-700 text-sm">
            <AlertCircle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <span>{error}</span>
          </div>
        )}

        {spcPets.length === 0 && (
          <div className="mb-4 p-3 bg-amber-50 border border-amber-200 rounded-lg text-sm text-amber-900">
            <p className="font-medium mb-2">To change which SPC pets are available, sign in again.</p>
            <div className="grid grid-cols-2 gap-2 mb-2">
              <input
                type="email"
                placeholder="Email"
                value={reauthEmail}
                onChange={(e) => setReauthEmail(e.target.value)}
                className="px-3 py-1.5 border border-amber-300 rounded-lg text-sm bg-white"
              />
              <input
                type="password"
                placeholder="Password"
                value={reauthPassword}
                onChange={(e) => setReauthPassword(e.target.value)}
                className="px-3 py-1.5 border border-amber-300 rounded-lg text-sm bg-white"
              />
            </div>
            <button
              onClick={loadSpcPets}
              disabled={busy || !reauthEmail.trim() || !reauthPassword}
              className="px-3 py-1.5 bg-amber-600 text-white rounded-lg text-sm hover:bg-amber-700 disabled:opacity-50 flex items-center gap-2"
            >
              {busy && <Loader2 className="w-4 h-4 animate-spin" />}
              Load SPC pets
            </button>
          </div>
        )}

        <div className="space-y-2 mb-4">
          {pets.map((p) => {
            const selected = mapByPet[p.petId] ?? "";
            const duplicate = selected && duplicateSpcIds.has(selected);
            return (
              <div key={p.petId} className="flex items-center gap-3 p-2 bg-gray-50 rounded-lg">
                <div className="flex-1">
                  <div className="text-sm font-medium text-gray-900">{p.name}</div>
                  {p.breed && <div className="text-xs text-gray-500">{p.breed}</div>}
                </div>
                <select
                  value={selected}
                  onChange={(e) => setMapByPet((prev) => ({ ...prev, [p.petId]: e.target.value }))}
                  className={`px-3 py-1.5 border rounded-lg text-sm ${duplicate ? "border-red-400 bg-red-50" : "border-gray-300"}`}
                  disabled={availableSpcPets.length === 0 && !p.spcPetId}
                >
                  <option value="">None</option>
                  {availableSpcPets.map((sp) => (
                    <option key={sp.id} value={sp.id}>
                      {sp.name}
                    </option>
                  ))}
                </select>
              </div>
            );
          })}
        </div>

        <div className="flex flex-wrap gap-2 pt-2 border-t border-gray-100">
          <button
            onClick={handleSave}
            disabled={busy || duplicateSpcIds.size > 0}
            className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 text-sm font-medium flex items-center gap-2"
          >
            {busy && <Loader2 className="w-4 h-4 animate-spin" />}
            Save mapping
          </button>
          <button
            onClick={onClose}
            className="px-4 py-2 bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 text-sm"
          >
            Cancel
          </button>
          <div className="flex-1" />
          {!confirmUnlink ? (
            <button
              onClick={() => setConfirmUnlink(true)}
              className="px-4 py-2 border border-red-300 text-red-700 rounded-lg hover:bg-red-50 text-sm font-medium flex items-center gap-2"
            >
              <Trash2 className="w-4 h-4" />
              Unlink account
            </button>
          ) : (
            <div className="flex items-center gap-2">
              <span className="text-sm text-red-700">Delete link?</span>
              <button
                onClick={handleUnlink}
                disabled={busy}
                className="px-3 py-1.5 bg-red-600 text-white rounded-lg hover:bg-red-700 disabled:opacity-50 text-sm"
              >
                {busy ? <Loader2 className="w-4 h-4 animate-spin" /> : "Confirm unlink"}
              </button>
              <button
                onClick={() => setConfirmUnlink(false)}
                className="px-3 py-1.5 bg-gray-100 text-gray-700 rounded-lg text-sm"
              >
                Cancel
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function spcErrorMessage(e: unknown): string {
  if (e instanceof SpcApiError) {
    switch (e.code) {
      case "invalid_credentials": return "Invalid Sure Pet Care email or password.";
      case "token_expired": return "Your stored Sure Pet Care session expired. Re-link the account to continue.";
      case "session_expired": return "Your sign-in session expired. Please re-enter your credentials.";
      case "session_invalid": return "Your sign-in session is no longer valid. Please re-enter your credentials.";
      case "upstream_unavailable": return "Sure Pet Care is temporarily unavailable. Try again shortly.";
      case "duplicate_spc_pet_mapping": return "Each SPC pet can only be mapped to one SnoutSpotter pet.";
      default: return `Sure Pet Care error: ${e.code}`;
    }
  }
  return e instanceof Error ? e.message : "Unexpected error";
}
