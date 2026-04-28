using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface ISpcEventsService
{
    // Newest-first events for a single pet within a household. nextPageKey is
    // the SK cursor of the previous page's last item (opaque to callers).
    Task<SpcEventsPage> ListForPetAsync(string householdId, string petId, int limit, string? nextPageKey);
}
