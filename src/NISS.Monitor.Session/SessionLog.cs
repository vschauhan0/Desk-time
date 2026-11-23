using System.Globalization;

namespace NISS.Monitor.Session;

/// <summary>
/// Minimal per-user rolling log (%LOCALAPPDATA%\NISS\Session\session.log, 1 MB x 2 files). It never contains window
/// titles, key data or any monitored content - only lifecycle and error messages.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private const long MaxBytes = 1_048_576;
    private readonly string _path;
    private readonly object _gate = new();

    public SessionLog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NISS", "Session");
        try { Directory.CreateDirectory(dir); } catch (IOException) { }
        _path = Path.Combine(dir, "session.log");
    }

    public void Info(string message) => Write("INF", message);

    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) File.Move(_path, _path + ".1", true);
                File.AppendAllText(_path, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never break monitoring */ }
        }
    }

    public void Dispose() { }
}
