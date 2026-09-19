namespace CluckIn.App.Interfaces;

public interface IDesktopManager
{
    Task OpenApplicationAsync(string executablePath);
    Task OpenUrlAsync(string url);
}
