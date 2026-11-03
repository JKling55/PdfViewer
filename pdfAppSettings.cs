using System.IO;
using System.Text.Json;

namespace PdfViewer;

/// <summary>
/// User options, persisted as JSON in %AppData%\KlingelingPdfViewer\settings.json.
/// Every write re-reads the file first and changes only its own fields, so several
/// running instances do not overwrite each other's changes.
/// </summary>
public static class AppSettings
{
    private sealed class Data
    {
        public bool SingleInstance { get; set; }          // false = several instances allowed
        public bool ReopenLastFiles { get; set; }         // false = start with no document
        public List<string> LastSession { get; set; } = new(); // files open when the program last closed
    }

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KlingelingPdfViewer", "settings.json");

    private static Data _data = new();

    /// <summary>True: only one program window exists; further starts hand their files to it.</summary>
    public static bool SingleInstance => _data.SingleInstance;

    /// <summary>True: the files of the last session are opened at startup.</summary>
    public static bool ReopenLastFiles => _data.ReopenLastFiles;

    public static IReadOnlyList<string> LastSession => _data.LastSession;

    /// <summary>Reads the file (call once at startup).</summary>
    public static void Load() => _data = Read();

    public static void SaveOptions(bool singleInstance, bool reopenLastFiles) =>
        Update(d => { d.SingleInstance = singleInstance; d.ReopenLastFiles = reopenLastFiles; });

    public static void SaveSession(IEnumerable<string> files) =>
        Update(d => d.LastSession = files.ToList());

    private static void Update(Action<Data> change)
    {
        var d = Read();
        change(d);
        _data = d;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath,
                JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not critical (read-only profile etc.): the settings just aren't remembered.
        }
    }

    private static Data Read()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(StorePath)) ?? new();
        }
        catch
        {
            // Unreadable or corrupt file: use the defaults.
        }
        return new();
    }
}
