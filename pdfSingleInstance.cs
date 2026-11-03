using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace PdfViewer;

/// <summary>
/// "Single instance" mode: the first process owns a named mutex and listens on a named pipe.
/// Every later process sends its file paths through the pipe and exits.
/// </summary>
public static class SingleInstance
{
    // "Local\" = per Windows session, so different users on one machine do not interfere.
    private const string MutexName = @"Local\KlingelingPdfViewer.SingleInstance";
    private static readonly string PipeName = "KlingelingPdfViewer.Pipe." + Environment.UserName;

    private static Mutex? _mutex; // kept in a field so it lives as long as the process

    /// <summary>True if this process is the first one (and now owns the single-instance lock).</summary>
    public static bool TryBecomeFirst()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return createdNew;
    }

    /// <summary>Sends the file paths to the running instance (an empty list just brings it to the front).</summary>
    public static void SendToFirst(IEnumerable<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            foreach (string arg in args)
            {
                // Resolve here: the other process has a different working directory.
                try { writer.WriteLine(Path.GetFullPath(arg)); }
                catch { /* invalid path: skip */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not reach the running instance: {ex.Message}");
        }
    }

    /// <summary>Starts listening; <paramref name="onFiles"/> is called on the UI thread for every incoming request.</summary>
    public static void StartServer(Action<List<string>> onFiles)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();

                    var paths = new List<string>();
                    using var reader = new StreamReader(server);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                        if (line.Length > 0) paths.Add(line);

                    await Application.Current.Dispatcher.InvokeAsync(() => onFiles(paths));
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Single-instance pipe error: {ex.Message}");
                    await Task.Delay(500); // avoid a tight loop on repeated errors
                }
            }
        });
    }
}
