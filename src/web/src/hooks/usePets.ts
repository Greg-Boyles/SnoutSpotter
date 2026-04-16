import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type { Pet } from "../types";

let cachedPets: Pet[] | null = null;
let cachePromise: Promise<Pet[]> | null = null;
let cacheVersion = 0;

export function usePets() {
  const [pets, setPets] = useState<Pet[]>(cachedPets ?? []);
  const [loading, setLoading] = useState(cachedPets === null);
  const [version, setVersion] = useState(cacheVersion);

  useEffect(() => {
    if (cachedPets && cacheVersion === version) {
      setPets(cachedPets);
      setLoading(false);
      return;
    }

    if (!cachePromise) {
      cachePromise = api.listPets().then((data) => {
        cachedPets = data;
        cachePromise = null;
        return data;
      });
    }

    cachePromise.then((data) => {
      setPets(data);
      setLoading(false);
    });
  }, [version]);

  const invalidate = useCallback(() => {
    cachedPets = null;
    cachePromise = null;
    cacheVersion++;
    setVersion(cacheVersion);
  }, []);

  const petName = useCallback(
    (petId: string): string => {
      if (petId === "other_dog") return "Other Dog";
      if (petId === "no_dog") return "No Dog";
      if (petId === "my_dog") return "My Dog"; // legacy
      const pet = pets.find((p) => p.petId === petId);
      return pet?.name ?? petId;
    },
    [pets],
  );

  return { pets, loading, invalidate, petName };
}
