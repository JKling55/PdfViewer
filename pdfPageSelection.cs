namespace PdfViewer;

/// <summary>Parses page selections such as "1, 3, 7-9" or "5-" (from page 5 to the end).</summary>
public static class PageSelection
{
    /// <summary>
    /// On success returns the 0-based page numbers in the order they were typed.
    /// On failure returns null and a message for the user.
    /// </summary>
    public static List<int>? TryParse(string text, int pageCount, out string error)
    {
        error = "";
        var result = new List<int>();

        foreach (string raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            int dash = part.IndexOf('-');
            int from, to;

            if (dash < 0)
            {
                if (!int.TryParse(part, out from)) { error = $"\"{part}\" is not a page number."; return null; }
                to = from;
            }
            else
            {
                string a = part[..dash].Trim(), b = part[(dash + 1)..].Trim();
                if (!int.TryParse(a, out from)) { error = $"\"{part}\" is not a valid range."; return null; }
                if (b.Length == 0) to = pageCount; // "5-" = to the end
                else if (!int.TryParse(b, out to)) { error = $"\"{part}\" is not a valid range."; return null; }
            }

            if (from < 1 || to > pageCount)
            {
                error = $"\"{part}\" is outside the document (pages 1–{pageCount}).";
                return null;
            }
            if (to < from) { error = $"\"{part}\": the range is reversed."; return null; }

            for (int p = from; p <= to; p++) result.Add(p - 1);
        }

        if (result.Count == 0) { error = "Please enter at least one page."; return null; }
        return result;
    }
}
