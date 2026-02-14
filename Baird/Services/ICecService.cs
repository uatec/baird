namespace Baird.Services;

public interface ICecService
{
    Task StartAsync();
    Task TogglePowerAsync();
}
