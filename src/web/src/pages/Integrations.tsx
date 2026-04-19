import { useEffect, useState } from "react";
import { Plug, Loader2, AlertCircle } from "lucide-react";
import { api } from "../api";
import type { Pet } from "../types";
import { usePets } from "../hooks/usePets";
import SpcConnectorCard from "../components/integrations/SpcConnectorCard";
import SpcSetupWizard from "../components/integrations/SpcSetupWizard";
import SpcManageDialog from "../components/integrations/SpcManageDialog";

type SpcStatusResponse = Awaited<ReturnType<typeof api.spc.status>>;

export default function Integrations() {
  const [spcStatus, setSpcStatus] = useState<SpcStatusResponse | null>(null);
  const [pets, setPets] = useState<Pet[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [manageOpen, setManageOpen] = useState(false);
  const { invalidate: invalidatePetsCache } = usePets();

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const [status, petList] = await Promise.all([api.spc.status(), api.listPets()]);
      setSpcStatus(status);
      setPets(petList);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load integrations");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  const handleLinked = () => {
    invalidatePetsCache();
    load();
  };

  const handleUnlinked = () => {
    invalidatePetsCache();
    load();
  };

  return (
    <div className="max-w-4xl mx-auto">
      <div className="flex items-center gap-3 mb-6">
        <Plug className="w-7 h-7 text-amber-600" />
        <h1 className="text-2xl font-bold text-gray-900">Integrations</h1>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg flex items-center gap-2 text-red-700 text-sm">
          <AlertCircle className="w-4 h-4 flex-shrink-0" />
          {error}
        </div>
      )}

      {loading || !spcStatus ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-gray-400" />
        </div>
      ) : (
        <div className="space-y-4">
          <SpcConnectorCard
            status={spcStatus.status}
            spcUserEmail={spcStatus.spcUserEmail}
            spcHouseholdName={spcStatus.spcHouseholdName}
            linkedAt={spcStatus.linkedAt}
            onConfigure={() => setWizardOpen(true)}
            onManage={() => setManageOpen(true)}
          />
        </div>
      )}

      <SpcSetupWizard
        open={wizardOpen}
        onClose={() => setWizardOpen(false)}
        onLinked={handleLinked}
        pets={pets}
      />

      {spcStatus?.status === "linked" && spcStatus.spcHouseholdId && (
        <SpcManageDialog
          open={manageOpen}
          onClose={() => setManageOpen(false)}
          onChanged={handleUnlinked}
          pets={pets}
          spcHouseholdId={spcStatus.spcHouseholdId}
          spcHouseholdName={spcStatus.spcHouseholdName ?? ""}
        />
      )}
    </div>
  );
}
