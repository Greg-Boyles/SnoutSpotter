import { useEffect, useState } from "react";
import { PawPrint, Plus, Pencil, Trash2, Loader2, AlertCircle, ArrowRightCircle } from "lucide-react";
import { api } from "../api";
import type { Pet } from "../types";
import { usePets } from "../hooks/usePets";

export default function Pets() {
  const [pets, setPets] = useState<Pet[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Form state
  const [showForm, setShowForm] = useState(false);
  const [editingPet, setEditingPet] = useState<Pet | null>(null);
  const [formName, setFormName] = useState("");
  const [formBreed, setFormBreed] = useState("");
  const [saving, setSaving] = useState(false);

  // Delete state
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const { invalidate: invalidatePetsCache } = usePets();

  // Migration state
  const [migrating, setMigrating] = useState(false);
  const [migrationResult, setMigrationResult] = useState<{ labelsUpdated: number; clipsUpdated: number } | null>(null);

  const loadPets = async () => {
    try {
      const data = await api.listPets();
      setPets(data);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load pets");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadPets();
  }, []);

  const openCreate = () => {
    setEditingPet(null);
    setFormName("");
    setFormBreed("");
    setShowForm(true);
  };

  const openEdit = (pet: Pet) => {
    setEditingPet(pet);
    setFormName(pet.name);
    setFormBreed(pet.breed ?? "");
    setShowForm(true);
  };

  const handleSave = async () => {
    if (!formName.trim()) return;
    setSaving(true);
    setError(null);
    try {
      if (editingPet) {
        await api.updatePet(editingPet.petId, formName.trim(), formBreed.trim() || undefined);
        setSuccess(`Updated ${formName.trim()}`);
      } else {
        await api.createPet(formName.trim(), formBreed.trim() || undefined);
        setSuccess(`Created ${formName.trim()}`);
      }
      setShowForm(false);
      invalidatePetsCache();
      await loadPets();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save pet");
    } finally {
      setSaving(false);
    }
  };

  const handleMigrate = async (petId: string) => {
    setMigrating(true);
    setError(null);
    try {
      const result = await api.migrateLegacyData(petId);
      setMigrationResult(result);
      setSuccess(`Migration complete: ${result.labelsUpdated} labels and ${result.clipsUpdated} clips updated`);
      invalidatePetsCache();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Migration failed");
    } finally {
      setMigrating(false);
    }
  };

  const handleDelete = async (pet: Pet) => {
    if (!confirm(`Delete ${pet.name}? This cannot be undone.`)) return;
    setDeletingId(pet.petId);
    setError(null);
    try {
      await api.deletePet(pet.petId);
      setSuccess(`Deleted ${pet.name}`);
      invalidatePetsCache();
      await loadPets();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete pet");
    } finally {
      setDeletingId(null);
    }
  };

  return (
    <div className="max-w-4xl mx-auto">
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-3">
          <PawPrint className="w-7 h-7 text-amber-600" />
          <h1 className="text-2xl font-bold text-gray-900">Pets</h1>
        </div>
        <button
          onClick={openCreate}
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 transition-colors text-sm font-medium"
        >
          <Plus className="w-4 h-4" />
          Add Pet
        </button>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg flex items-center gap-2 text-red-700 text-sm">
          <AlertCircle className="w-4 h-4 flex-shrink-0" />
          {error}
        </div>
      )}

      {success && (
        <div className="mb-4 p-3 bg-green-50 border border-green-200 rounded-lg text-green-700 text-sm">
          {success}
        </div>
      )}

      {/* Create / Edit Form */}
      {showForm && (
        <div className="mb-6 p-4 bg-white border border-gray-200 rounded-lg shadow-sm">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">
            {editingPet ? `Edit ${editingPet.name}` : "New Pet"}
          </h2>
          <div className="space-y-3">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
              <input
                type="text"
                value={formName}
                onChange={(e) => setFormName(e.target.value)}
                placeholder="e.g. Biscuit"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
                autoFocus
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Breed (optional)</label>
              <input
                type="text"
                value={formBreed}
                onChange={(e) => setFormBreed(e.target.value)}
                placeholder="e.g. Labrador Retriever"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
              />
            </div>
            <div className="flex gap-2 pt-1">
              <button
                onClick={handleSave}
                disabled={saving || !formName.trim()}
                className="px-4 py-2 bg-amber-600 text-white rounded-lg hover:bg-amber-700 disabled:opacity-50 text-sm font-medium flex items-center gap-2"
              >
                {saving && <Loader2 className="w-4 h-4 animate-spin" />}
                {editingPet ? "Update" : "Create"}
              </button>
              <button
                onClick={() => setShowForm(false)}
                className="px-4 py-2 bg-gray-100 text-gray-700 rounded-lg hover:bg-gray-200 text-sm font-medium"
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Pet Cards */}
      {loading ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-gray-400" />
        </div>
      ) : pets.length === 0 ? (
        <div className="text-center py-12 bg-white border border-gray-200 rounded-lg">
          <PawPrint className="w-12 h-12 text-gray-300 mx-auto mb-3" />
          <p className="text-gray-500">No pets yet. Add your first pet to get started.</p>
          <p className="text-xs text-gray-400 mt-2">
            If you have existing "my_dog" labelled data, create a pet first then migrate the data.
          </p>
        </div>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2">
          {pets.map((pet) => (
            <div
              key={pet.petId}
              className="bg-white border border-gray-200 rounded-lg p-4 shadow-sm"
            >
              <div className="flex items-start justify-between">
                <div className="flex items-center gap-3">
                  <div className="w-10 h-10 rounded-full bg-amber-100 flex items-center justify-center">
                    <PawPrint className="w-5 h-5 text-amber-600" />
                  </div>
                  <div>
                    <h3 className="font-semibold text-gray-900">{pet.name}</h3>
                    {pet.breed && (
                      <p className="text-sm text-gray-500">{pet.breed}</p>
                    )}
                  </div>
                </div>
                <div className="flex items-center gap-1">
                  <button
                    onClick={() => openEdit(pet)}
                    className="p-1.5 rounded-lg text-gray-400 hover:text-gray-600 hover:bg-gray-100"
                    title="Edit"
                  >
                    <Pencil className="w-4 h-4" />
                  </button>
                  <button
                    onClick={() => handleDelete(pet)}
                    disabled={deletingId === pet.petId}
                    className="p-1.5 rounded-lg text-gray-400 hover:text-red-600 hover:bg-red-50 disabled:opacity-50"
                    title="Delete"
                  >
                    {deletingId === pet.petId ? (
                      <Loader2 className="w-4 h-4 animate-spin" />
                    ) : (
                      <Trash2 className="w-4 h-4" />
                    )}
                  </button>
                </div>
              </div>
              <div className="mt-3 pt-3 border-t border-gray-100 flex items-center justify-between">
                <p className="text-xs text-gray-400">
                  ID: <span className="font-mono">{pet.petId}</span>
                </p>
                {!migrationResult && (
                  <button
                    onClick={() => handleMigrate(pet.petId)}
                    disabled={migrating}
                    className="inline-flex items-center gap-1 px-2 py-1 text-xs font-medium text-amber-700 bg-amber-50 hover:bg-amber-100 rounded-md disabled:opacity-50"
                    title="Migrate legacy 'my_dog' data to this pet"
                  >
                    {migrating ? <Loader2 className="w-3 h-3 animate-spin" /> : <ArrowRightCircle className="w-3 h-3" />}
                    Migrate my_dog data
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
