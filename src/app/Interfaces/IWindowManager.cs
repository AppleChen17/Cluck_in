using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IWindowManager
{
    Task<ActiveWindowInfo> GetActiveWindowAsync();
}
