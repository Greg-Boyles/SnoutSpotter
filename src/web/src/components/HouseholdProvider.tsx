import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { HouseholdContext, type Household } from "../hooks/useHousehold";
import HouseholdPicker from "./HouseholdPicker";

export default function HouseholdProvider({ children }: { children: React.ReactNode }) {
  const [households, setHouseholds] = useState<Household[]>([]);
  const [activeId, setActiveId] = useState<string | null>(
    () => localStorage.getItem("activeHouseholdId")
  );
  const [loading, setLoading] = useState(true);
  const [userName, setUserName] = useState<string | null>(null);

  const loadHouseholds = useCallback(async () => {
    try {
      const data = await api.listHouseholds();
      setHouseholds(data.households);
      setUserName(data.name || data.email || null);

      if (data.households.length === 1) {
        const id = data.households[0].householdId;
        setActiveId(id);
        localStorage.setItem("activeHouseholdId", id);
      } else if (activeId && !data.households.some(h => h.householdId === activeId)) {
        setActiveId(null);
        localStorage.removeItem("activeHouseholdId");
      }
    } catch (e) {
      console.error("Failed to load households:", e);
    } finally {
      setLoading(false);
    }
  }, [activeId]);

  useEffect(() => {
    loadHouseholds();
  }, []);

  const setActiveHousehold = (id: string) => {
    setActiveId(id);
    localStorage.setItem("activeHouseholdId", id);
    window.location.reload();
  };

  const activeHousehold = households.find(h => h.householdId === activeId) ?? null;

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <p className="text-gray-500">Loading...</p>
      </div>
    );
  }

  if (!activeHousehold) {
    return (
      <HouseholdPicker
        households={households}
        onSelect={setActiveHousehold}
        onCreated={loadHouseholds}
      />
    );
  }

  return (
    <HouseholdContext.Provider
      value={{ households, activeHousehold, setActiveHousehold, loading, userName }}
    >
      {children}
    </HouseholdContext.Provider>
  );
}
