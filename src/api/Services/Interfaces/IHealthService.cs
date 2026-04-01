namespace SnoutSpotter.Api.Services;

public interface IHealthService
{
    Task<bool> IsPiOnlineAsync();
}
