using Microsoft.Extensions.Caching.Memory;
using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services;

public class SpcSessionStore : ISpcSessionStore
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public SpcSessionStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public SpcSession Create(string accessToken, string clientUid, long spcUserId, string spcUserEmail)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var expires = DateTime.UtcNow.Add(Ttl);
        var session = new SpcSession(sessionId, accessToken, clientUid, spcUserId, spcUserEmail, expires);
        _cache.Set(sessionId, session, expires);
        return session;
    }

    public SpcSession? Get(string sessionId, string householdId)
    {
        // householdId is currently only used for future tenant binding; included
        // now so callers can't accidentally look up a session without it.
        _ = householdId;
        return _cache.TryGetValue(sessionId, out SpcSession? session) ? session : null;
    }

    public void Delete(string sessionId, string householdId)
    {
        _ = householdId;
        _cache.Remove(sessionId);
    }
}
