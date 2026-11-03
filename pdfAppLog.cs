using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfViewer;

/// <summary>
/// Simple diagnostic log: one text file per program start in
/// %AppData%\KlingelingPdfViewer\logs\session-yyyyMMdd-HHmmss.log (the newest 10 are kept).
///
/// Every line is flushed to disk immediately, so the last line of a log shows what the program
/// was doing when it died, even after a hard crash that no exception handler can catch.
/// A marker file ("running-PID.marker") exists while the program runs; if the next start finds
/// one left behind, the previous session did not end normally and this is written to the new log.
/// </summary>
public static class AppLog
{
    private const int KeepFiles = 10;
    private const string MarkerPrefix = "running-";

    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static string? _markerPath;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KlingelingPdfViewer", "logs");

    public static string? CurrentFile { get; private set; }

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            // Markers of sessions that are no longer running = those sessions ended abnormally.
            var abnormal = new List<string>();
            foreach (string marker in Directory.GetFiles(LogDirectory, MarkerPrefix + "*.marker"))
            {
                if (IsProcessRunning(marker)) continue; // another instance is still running
                try
                {
                    abnormal.Add(File.ReadAllText(marker));
                    File.Delete(marker);
                }
                catch { /* ignore */ }
            }

            // Keep only the newest logs.
            var oldLogs = new DirectoryInfo(LogDirectory).GetFiles("session-*.log")
                .OrderByDescending(f => f.Name).Skip(KeepFiles - 1);
            foreach (var old in oldLogs)
            {
                try { old.Delete(); } catch { /* ignore */ }
            }

            CurrentFile = Path.Combine(LogDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var stream = new FileStream(CurrentFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            _markerPath = Path.Combine(LogDirectory, $"{MarkerPrefix}{Environment.ProcessId}.marker");
            File.WriteAllText(_markerPath, CurrentFile);

            var version = typeof(AppLog).Assembly.GetName().Version;
            Write($"Session started. Klingeling PdfViewer {version?.ToString(3)}, PID {Environment.ProcessId}");
            Write($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, " +
                  $"{(Environment.Is64BitProcess ? "64" : "32")}-bit process, {Environment.ProcessorCount} CPUs");
            Write(Memory());

            foreach (string previous in abnormal)
                Write($"WARNING: the previous session did not end normally. Its log: {previous}");
        }
        catch
        {
            // Logging must never prevent the program from starting.
        }
    }

    /// <summary>Called on a normal exit: closes the log and removes the "running" marker.</summary>
    public static void Stop()
    {
        Write("Session ended normally.");
        try
        {
            lock (Sync)
            {
                _writer?.Dispose();
                _writer = null;
            }
            if (_markerPath != null) File.Delete(_markerPath);
        }
        catch { /* ignore */ }
    }

    /// <summary>Writes one line with time and thread number. Safe to call from any thread.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId,2}] {message}");
            }
        }
        catch { /* never throw from the logger */ }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    /// <summary>Current memory use of the process, for log lines.</summary>
    public static string Memory()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return $"memory: working set {p.WorkingSet64 >> 20} MB, private {p.PrivateMemorySize64 >> 20} MB, " +
                   $"managed {GC.GetTotalMemory(false) >> 20} MB";
        }
        catch
        {
            return "memory: n/a";
        }
    }

    // The marker file name contains the process id: "running-1234.marker".
    private static bool IsProcessRunning(string markerPath)
    {
        string name = Path.GetFileNameWithoutExtension(markerPath);
        if (!int.TryParse(name.AsSpan(MarkerPrefix.Length), out int pid)) return false;

        try
        {
            using var p = Process.GetProcessById(pid);
            // The name check guards against another program that reuses the process id.
            return !p.HasExited && p.ProcessName.Contains("Klingeling", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch
        {
            return false;
        }
    }
}
