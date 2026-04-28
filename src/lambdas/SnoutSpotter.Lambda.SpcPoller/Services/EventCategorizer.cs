namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Maps SPC's TimelineEventType enum to a coarse category the UI can filter
// and iconify on. Raw spc_event_type is always persisted so the UI can show
// a precise label in a tooltip. Values sourced from the SPC backend's
// TimelineEventType.cs enum definition.
public static class EventCategorizer
{
    public static string Categorize(int eventType) => eventType switch
    {
        0 => "movement",              // Movement (pet door)
        7 => "movement",              // IntruderMovement
        21 => "feeding",              // WeightChanged (feeder device snapshot)
        22 => "feeding",              // Feeding (pet encounter at feeder)
        23 => "feeding",              // FeederSettingsChanged
        24 => "feeding",              // Tare
        29 => "drinking",             // Drinking (Poseidon / no-ID dog bowl)
        30 => "drinking",             // PoseidonTankReplaced (refill)
        31 => "drinking",             // PoseidonTare
        32 => "drinking",             // WaterFreshness
        33 => "drinking",             // LowWater
        34 => "drinking",             // WaterRemoved
        35 => "feeding",              // FoodRemoved
        50 => "feeding",              // NoIdDogBowlSettingsChanged
        51 => "feeding",              // ConsumptionAlert
        52 => "feeding",              // TaringNeeded
        53 => "feeding",              // TarringOccurred
        54 => "drinking",             // NoDrinkingEvents
        55 => "feeding",              // TimedAccessOverride
        1 or 9 or 18 => "device_status",  // LowBattery, HubChildOnline, HubOnline
        30000 or 30001 or 30002 => "device_status", // Wifi/Firmware status
        >= 20000 and <= 28999 => "device_status",   // Device telemetry ranges
        _ => "other"
    };
}
