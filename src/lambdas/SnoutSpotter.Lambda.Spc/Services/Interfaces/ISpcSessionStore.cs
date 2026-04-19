using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services.Interfaces;

public interface ISpcSessionStore
{
    SpcSession Create(string accessToken, string clientUid, long spcUserId, string spcUserEmail);
    SpcSession? Get(string sessionId, string householdId);
    void Delete(string sessionId, string householdId);
}
