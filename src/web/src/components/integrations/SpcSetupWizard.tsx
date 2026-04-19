import { useEffect, useMemo, useState } from "react";
import { X, Loader2, AlertCircle, Check, ChevronRight } from "lucide-react";
import { api, SpcApiError } from "../../api";
import type { Pet } from "../../types";

type SpcHousehold = { id: string; name: string };
type SpcPet = { id: string; name: string; species: string | null; photoUrl: string | null };
type Mapping = { petId: string; spcPetId: string | null; spcPetName: string | null };

type Step = "credentials" | "choose-household" | "map-pets" | "summary";

interface Props {
  open: boolean;
  onClose: () => void;
  onLinked: () => void;
  pets: Pet[];
}

export default function SpcSetupWizard({ open, onClose, onLinked, pets }: Props) {
  const [step, setStep] = useState<Step>("credentials");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Step 1
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

  // Session carried through steps 2–4
  const [sessionId, setSessionId] = useState<string | null>(null);

  // Step 2
  const [spcHouseholds, setSpcHouseholds] = useState<SpcHousehold[]>([]);
  const [chosenHouseholdId, setChosenHouseholdId] = useState<string | null>(null);

  // Step 3
  const [spcPets, setSpcPets] = useState<SpcPet[]>([]);
  const [mapByPet, setMapByPet] = useState<Record<string, string>>({});
  // ""  = "None" (explicit no-match)
  // "spcPetId" = pick that SPC pet

  useEffect(() => {
    if (!open) {
      // Reset everything on close so re-opening starts fresh.
      setStep("credentials");
      setEmail("");
      setPassword("");
      setSessionId(null);
      setSpcHouseholds([]);
      setChosenHouseholdId(null);
      setSpcPets([]);
      setMapByPet({});
      setError(null);
      setBusy(false);
    }
  }, [open]);

  const duplicateSpcIds = useMemo(() => {
    const counts: Record<string, number> = {};
    Object.values(mapByPet).forEach((spcId) => {
      if (spcId) counts[spcId] = (counts[spcId] ?? 0) + 1;
    });
    return new Set(Object.entries(counts).filter(([, c]) => c > 1).map(([id]) => id));
  }, [mapByPet]);

  const allPetsDecided = pets.every((p) => mapByPet[p.petId] !== undefined);
  const mapValid = allPetsDecided && duplicateSpcIds.size === 0;

  const handleValidate = async () => {
    if (!email.trim() || !password) return;
    setBusy(true);
    setError(null);
    try {
      const res = await api.spc.validate(email.trim(), password);
      setSessionId(res.sessionId);
      const hh = await api.spc.listSpcHouseholds(res.sessionId);
      setSpcHouseholds(hh.households);
      if (hh.households.length === 1) {
        // Single household — auto-select and jump straight to pet mapping.
        const only = hh.households[0];
        setChosenHouseholdId(only.id);
        await loadSpcPetsFor(only.id, res.sessionId);
        setStep("map-pets");
      } else if (hh.households.length === 0) {
        setError("No Sure Pet Care households found on this account.");
      } else {
        setStep("choose-household");
      }
    } catch (e) {
      setError(spcErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const loadSpcPetsFor = async (spcHouseholdId: string, explicitSessionId?: string) => {
    const sid = explicitSessionId ?? sessionId!;
    const res = await api.spc.listSpcPets(sid, spcHouseholdId);
    setSpcPets(res.pets);
    setMapByPet((prev) => {
      const next = { ...prev };
      for (const p of pets) {
        if (next[p.petId] === undefined) {
          const byName = res.pets.find((sp) => sp.name.toLowerCase() === p.name.toLowerCase());
          next[p.petId] = byName ? byName.id : "";
        }
      }
      return next;
    });
  };

  const handleChooseHousehold = async (id: string) => {
    setChosenHouseholdId(id);
    setBusy(true);
    setError(null);
    try {
      await loadSpcPetsFor(id);
      setStep("map-pets");
    } catch (e) {
      setError(spcErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const handleLink = async () => {
    if (!sessionId || !chosenHouseholdId) return;
    setBusy(true);
    setError(null);
    const mappings: Mapping[] = pets.map((p) => {
      const spcId = mapByPet[p.petId];
      if (!spcId) return { petId: p.petId, spcPetId: null, spcPetName: null };
      const match = spcPets.find((sp) => sp.id === spcId);
      return { petId: p.petId, spcPetId: spcId, spcPetName: match?.name ?? null };
    });
    try {
      await api.spc.link({ sessionId, spcHouseholdId: chosenHouseholdId, mappings });
      onLinked();
      onClose();
    } catch (e) {
      setError(spcErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white rounded-lg p-6 max-w-2xl w-full mx-4 max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <div>
            <h3 className="text-lg font-semibold text-gray-900">Connect Sure Pet Care</h3>
            <p className="text-xs text-gray-500 mt-0.5">{stepLabel(step)}</p>
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

        {step === "credentials" && (
          <div className="space-y-3">
            <p className="text-sm text-gray-600">
              Enter the email and password for your Sure Pet Care app account. Your password is used
              once to sign in and is never stored &mdash; only the returned access token is saved.
            </p>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Email</label>
              <input
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="you@example.com"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
                autoFocus
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Password</label>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
              />
            </div>
            <div className="flex gap-2 pt-2">
              <button
                onClick={handleValidate}
                disabled={busy || !email.trim() || !password}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 text-sm font-medium flex items-center gap-2"
              >
                {busy && <Loader2 className="w-4 h-4 animate-spin" />}
                Continue
              </button>
              <button
                onClick={onClose}
                className="px-4 py-2 bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 text-sm font-medium"
              >
                Cancel
              </button>
            </div>
          </div>
        )}

        {step === "choose-household" && (
          <div className="space-y-3">
            <p className="text-sm text-gray-600">Pick the Sure Pet Care household to link to this SnoutSpotter household.</p>
            <div className="space-y-2">
              {spcHouseholds.map((hh) => (
                <button
                  key={hh.id}
                  onClick={() => handleChooseHousehold(hh.id)}
                  disabled={busy}
                  className="w-full text-left px-3 py-2 bg-white border border-gray-200 rounded-lg hover:bg-amber-50 hover:border-amber-300 disabled:opacity-50 flex items-center justify-between"
                >
                  <span className="text-sm font-medium text-gray-900">{hh.name}</span>
                  <ChevronRight className="w-4 h-4 text-gray-400" />
                </button>
              ))}
            </div>
          </div>
        )}

        {step === "map-pets" && (
          <div className="space-y-3">
            <p className="text-sm text-gray-600">
              Pair each SnoutSpotter pet with its Sure Pet Care equivalent, or choose "None" if there is no match.
              Each SPC pet can only be linked once.
            </p>
            {pets.length === 0 ? (
              <p className="text-sm text-gray-500 italic">You have no pets yet. Add some from the Pets page before linking.</p>
            ) : (
              <div className="space-y-2">
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
                      >
                        <option value="">None</option>
                        {spcPets.map((sp) => (
                          <option key={sp.id} value={sp.id}>
                            {sp.name}
                          </option>
                        ))}
                      </select>
                    </div>
                  );
                })}
              </div>
            )}
            {duplicateSpcIds.size > 0 && (
              <p className="text-xs text-red-600">Each SPC pet can only be mapped once.</p>
            )}
            <div className="flex gap-2 pt-2">
              <button
                onClick={() => setStep("summary")}
                disabled={!mapValid}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 text-sm font-medium"
              >
                Review
              </button>
              {spcHouseholds.length > 1 && (
                <button
                  onClick={() => setStep("choose-household")}
                  className="px-4 py-2 bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 text-sm"
                >
                  Back
                </button>
              )}
            </div>
          </div>
        )}

        {step === "summary" && (
          <div className="space-y-3">
            <p className="text-sm text-gray-600">Confirm the link. You can edit the mapping later from the Integrations page.</p>
            <div className="text-sm space-y-1 p-3 bg-gray-50 rounded-lg">
              <div><span className="text-gray-500">SPC household:</span> <span className="font-medium">{spcHouseholds.find((h) => h.id === chosenHouseholdId)?.name}</span></div>
              <div className="text-gray-500">Pet mapping:</div>
              <ul className="mt-1 space-y-0.5">
                {pets.map((p) => {
                  const spcId = mapByPet[p.petId];
                  const match = spcPets.find((sp) => sp.id === spcId);
                  return (
                    <li key={p.petId} className="flex items-center gap-2 pl-2">
                      <span className="font-medium text-gray-900">{p.name}</span>
                      <span className="text-gray-400">&rarr;</span>
                      <span className={match ? "text-gray-900" : "text-gray-400 italic"}>
                        {match ? match.name : "None"}
                      </span>
                    </li>
                  );
                })}
              </ul>
            </div>
            <div className="flex gap-2 pt-2">
              <button
                onClick={handleLink}
                disabled={busy}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 text-sm font-medium flex items-center gap-2"
              >
                {busy ? <Loader2 className="w-4 h-4 animate-spin" /> : <Check className="w-4 h-4" />}
                Link account
              </button>
              <button
                onClick={() => setStep("map-pets")}
                className="px-4 py-2 bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 text-sm"
              >
                Back
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function stepLabel(step: Step): string {
  switch (step) {
    case "credentials": return "Step 1 of 4: Sign in";
    case "choose-household": return "Step 2 of 4: Choose SPC household";
    case "map-pets": return "Step 3 of 4: Map your pets";
    case "summary": return "Step 4 of 4: Confirm";
  }
}

function spcErrorMessage(e: unknown): string {
  if (e instanceof SpcApiError) {
    switch (e.code) {
      case "invalid_credentials": return "Invalid Sure Pet Care email or password.";
      case "session_expired": return "Your sign-in session expired. Please re-enter your credentials.";
      case "session_invalid": return "Your sign-in session is no longer valid. Please re-enter your credentials.";
      case "upstream_unavailable": return "Sure Pet Care is temporarily unavailable. Try again shortly.";
      case "duplicate_spc_pet_mapping": return "Each SPC pet can only be mapped to one SnoutSpotter pet.";
      case "missing_mapping_for_pet": return "Every SnoutSpotter pet must have a Sure Pet Care selection (or 'None').";
      case "spc_household_not_accessible": return "That Sure Pet Care household is not accessible with these credentials.";
      case "no_households_found": return "No Sure Pet Care households found on this account.";
      default: return `Sure Pet Care error: ${e.code}`;
    }
  }
  return e instanceof Error ? e.message : "Unexpected error";
}
