namespace SnoutSpotter.Api.Services.Interfaces;

public interface IHealthService
{
    Task<bool> IsPiOnlineAsync();
}
