import { useCallback, useEffect, useState } from "react";
import {
  Loader2,
  AlertCircle,
  Utensils,
  Droplet,
  DoorOpen,
  Plug,
  Circle,
  ChevronDown,
  ChevronUp,
} from "lucide-react";
import { api } from "../../api";
import type { SpcEvent } from "../../types";

// Inline Activity panel on the Pets page. Lazy-loaded on expand so unmounted
// pets never hit the SPC events API.
export default function PetActivityPanel({ petId, petName }: { petId: string; petName: string }) {
  const [open, setOpen] = useState(false);
  const [events, setEvents] = useState<SpcEvent[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [nextPageKey, setNextPageKey] = useState<string | null>(null);

  const loadFirstPage = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const page = await api.listSpcEventsForPet(petId, 20);
      setEvents(page.events);
      setNextPageKey(page.nextPageKey);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load activity");
    } finally {
      setLoading(false);
    }
  }, [petId]);

  const loadMore = async () => {
    if (!nextPageKey) return;
    setLoading(true);
    try {
      const page = await api.listSpcEventsForPet(petId, 20, nextPageKey);
      setEvents((prev) => [...(prev ?? []), ...page.events]);
      setNextPageKey(page.nextPageKey);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load more");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (open && events === null && !loading) {
      loadFirstPage();
    }
  }, [open, events, loading, loadFirstPage]);

  return (
    <div className="mt-3 pt-3 border-t border-gray-100">
      <button
        onClick={() => setOpen((v) => !v)}
        className="flex items-center gap-1 text-xs text-amber-700 hover:text-amber-800"
      >
        {open ? <ChevronUp className="w-3.5 h-3.5" /> : <ChevronDown className="w-3.5 h-3.5" />}
        Sure Pet Care activity
      </button>

      {open && (
        <div className="mt-2">
          {error && (
            <div className="mb-2 p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 flex items-start gap-1.5">
              <AlertCircle className="w-3.5 h-3.5 flex-shrink-0 mt-0.5" />
              <span>{error}</span>
            </div>
          )}
          {loading && events === null ? (
            <div className="flex items-center gap-2 text-xs text-gray-500 py-2">
              <Loader2 className="w-3.5 h-3.5 animate-spin" /> Loading recent activity&hellip;
            </div>
          ) : events === null ? null : events.length === 0 ? (
            <p className="text-xs text-gray-500 italic py-1">
              No recent events for {petName}. Events appear here after motion at a linked camera
              triggers a Sure Pet Care poll.
            </p>
          ) : (
            <>
              <ul className="space-y-1">
                {events.map((e) => (
                  <EventRow key={e.spcEventId} event={e} petName={petName} />
                ))}
              </ul>
              {nextPageKey && (
                <button
                  onClick={loadMore}
                  disabled={loading}
                  className="mt-2 text-xs text-amber-700 hover:text-amber-800 disabled:opacity-50 flex items-center gap-1"
                >
                  {loading && <Loader2 className="w-3 h-3 animate-spin" />}
                  Load more
                </button>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

function EventRow({ event, petName }: { event: SpcEvent; petName: string }) {
  const { Icon } = iconForCategory(event.eventCategory);
  const sentence = derivedSentence(event, petName);
  const when = relativeTime(event.createdAt);

  return (
    <li className="flex items-start gap-2 text-xs text-gray-700 bg-gray-50 rounded px-2 py-1.5">
      <Icon className="w-3.5 h-3.5 text-amber-600 flex-shrink-0 mt-0.5" />
      <div className="flex-1 min-w-0">
        <span className="text-gray-900">{sentence}</span>
        <span
          className="text-gray-400 ml-2"
          title={`type=${event.spcEventType} · ${new Date(event.createdAt).toLocaleString()}`}
        >
          {when}
        </span>
      </div>
    </li>
  );
}

function iconForCategory(category: string): { Icon: React.ElementType } {
  switch (category) {
    case "feeding":
      return { Icon: Utensils };
    case "drinking":
      return { Icon: Droplet };
    case "movement":
      return { Icon: DoorOpen };
    case "device_status":
      return { Icon: Plug };
    default:
      return { Icon: Circle };
  }
}

// Phase-1 sentence derivation. raw_data decoding is a follow-up; this keeps
// the list readable without committing us to specific field shapes.
function derivedSentence(event: SpcEvent, petName: string): string {
  switch (event.eventCategory) {
    case "feeding":
      return `${petName} ate`;
    case "drinking":
      return `${petName} drank`;
    case "movement":
      return `${petName} moved through a pet door`;
    case "device_status":
      return `Device status update`;
    default:
      return `Event (type ${event.spcEventType})`;
  }
}

function relativeTime(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const deltaMs = Date.now() - t;
  const s = Math.max(0, Math.floor(deltaMs / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24);
  return `${d}d ago`;
}
