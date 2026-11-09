using System.Text;

namespace Varco.Core;

/// <summary>
/// Logger minimale su file (con rotazione) e, opzionalmente, su console.
/// Volutamente senza dipendenze: gira anche come SYSTEM sul desktop sicuro.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private const long MaxBytes = 5 * 1024 * 1024;

    public static bool ToConsole { get; set; }
    public static bool Verbose { get; set; }

    public static void Init(string directory, string fileName)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, fileName);
        }
        catch
        {
            _path = null;
        }
    }

    public static void Debug(string msg) { if (Verbose) Write("DBG", msg); }
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write("ERR", ex is null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId,3}] {msg}";
        lock (Gate)
        {
            if (ToConsole) Console.WriteLine(line);
            if (_path is null) return;
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = _path + ".1";
                    File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // il logging non deve mai far cadere l'app
            }
        }
    }
}
