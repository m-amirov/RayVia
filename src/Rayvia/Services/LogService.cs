namespace Rayvia.Services;

public sealed class LogService
{
    public event Action<string>? EntryAdded;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        EntryAdded?.Invoke(line);
    }
}
