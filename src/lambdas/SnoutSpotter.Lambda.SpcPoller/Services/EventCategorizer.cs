namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Coarse bucketing of SPC's TimelineEventType into a small category the UI
// can filter / iconify on without decoding the full ~100-value enum. Raw
// spc_event_type is always persisted alongside so the UI can show a precise
// label in a tooltip when users care.
public static class EventCategorizer
{
    public static string Categorize(int eventType) => eventType switch
    {
        >= 20000 and <= 20999 => "feeding",
        >= 22000 and <= 22999 => "drinking",
        20 => "movement",
        >= 21000 and <= 21999 => "movement",
        >= 9000 and <= 19999 => "device_status",
        _ => "other"
    };
}
