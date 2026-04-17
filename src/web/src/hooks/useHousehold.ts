import { createContext, useContext } from "react";

export type Household = {
  householdId: string;
  name: string;
  createdAt: string;
};

export type HouseholdContextType = {
  households: Household[];
  activeHousehold: Household | null;
  setActiveHousehold: (id: string) => void;
  loading: boolean;
  userName: string | null;
};

export const HouseholdContext = createContext<HouseholdContextType>({
  households: [],
  activeHousehold: null,
  setActiveHousehold: () => {},
  loading: true,
  userName: null,
});

export function useHousehold() {
  return useContext(HouseholdContext);
}
