using System.IO;
using System.Text.Json;

namespace PdfViewer;

/// <summary>
/// Most-recently-used list of opened PDF files, persisted as JSON in
/// %AppData%\KlingelingPdfViewer\recent-files.json. Newest entry first.
/// </summary>
public static class RecentFiles
{
    private const int MaxEntries = 10;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KlingelingPdfViewer", "recent-files.json");

    private static List<string>? _items;

    public static IReadOnlyList<string> Items => Load();

    /// <summary>Moves the file to the top of the list (adds it if new).</summary>
    public static void Add(string path)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        Save();
    }

    public static void Remove(string path)
    {
        Load().RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public static void Clear()
    {
        Load().Clear();
        Save();
    }

    // Loads the list once and keeps it in memory afterwards.
    private static List<string> Load()
    {
        if (_items != null) return _items;

        try
        {
            _items = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch
        {
            // Unreadable or corrupt file: start with an empty list.
            _items = new();
        }
        return _items;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_items));
        }
        catch
        {
            // Not critical (read-only profile etc.): the list just isn't remembered.
        }
    }
}
