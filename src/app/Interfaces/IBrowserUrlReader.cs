using CluckIn.App.Models;

namespace CluckIn.App.Interfaces;

public interface IBrowserUrlReader
{
    Task<string?> ReadUrlAsync(ActiveWindowInfo window);
}
