// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Text;

namespace ChromaBridge;

/// <summary>
/// A small append-only file log, one file per process role, rolled once at 5 MB.
/// </summary>
public static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly object s_lock = new();
    private static string? s_path;

    /// <summary>The file being written, or null before <see cref="Initialize"/>.</summary>
    public static string? FilePath => s_path;

    /// <summary>
    /// Whether <see cref="Verbose"/> lines are written. Off unless the settings turn it on;
    /// the service sets it from the settings and passes it to each host.
    /// </summary>
    public static volatile bool VerboseEnabled;

    public static void Initialize(string name)
    {
        try
        {
            Directory.CreateDirectory(BridgePaths.LogDirectory);
            s_path = Path.Combine(BridgePaths.LogDirectory, name + ".log");
        }
        catch (Exception)
        {
            // Without a log folder the process still runs; it just cannot log
            s_path = null;
        }
    }


    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("FAIL", message, exception);

    /// <summary>Detail for troubleshooting; written only while <see cref="VerboseEnabled"/>.</summary>
    public static void Verbose(string message)
    {
        if (VerboseEnabled)
            Write("VERB", message, null);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        var path = s_path;

        if (path == null)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(level).Append(' ').Append(message);

        if (exception != null)
            line.Append(" | ").Append(exception.GetType().Name).Append(" 0x").Append(exception.HResult.ToString("X8")).Append(": ").Append(exception.Message);

        line.AppendLine();

        lock (s_lock)
        {
            try
            {
                var info = new FileInfo(path);

                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);

                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
