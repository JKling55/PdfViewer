using System.Text;
using System.Windows;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PdfViewer;

/// <summary>
/// Text of one PDF page together with the position of every character.
/// The same model is used for searching and for placing the highlight rectangles,
/// so character indexes found by the search always line up with the glyph positions.
/// </summary>
public sealed class PageText
{
    // A gap between two letters wider than this fraction of the font size counts as a space.
    // Many PDFs contain no real space glyphs, only positioning gaps between the words.
    private const double SpaceGapFactor = 0.2;

    private readonly IReadOnlyList<Letter> _letters;
    private readonly int[] _letterOfChar;   // index into _letters for each char of Text, -1 = inserted space
    private readonly PdfRectangle _crop;    // visible area of the page in PDF user space
    private readonly int _rotation;         // 0, 90, 180 or 270 (clockwise)

    public string Text { get; }

    private PageText(string text, int[] letterOfChar, IReadOnlyList<Letter> letters,
                     PdfRectangle crop, int rotation)
    {
        Text = text;
        _letterOfChar = letterOfChar;
        _letters = letters;
        _crop = crop;
        _rotation = rotation;
    }

    public static PageText Build(Page page)
    {
        var letters = page.Letters;
        var sb = new StringBuilder(letters.Count + letters.Count / 8);
        var map = new List<int>(letters.Count + letters.Count / 8);
        Letter? prev = null;

        for (int i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            string value = Normalize(letter.Value);
            if (value.Length == 0) continue;

            // Insert a space between two visible letters that are far apart (new word or new line).
            if (prev != null && !IsBlank(prev) && !IsBlank(letter) && IsWordGap(prev, letter))
            {
                sb.Append(' ');
                map.Add(-1);
            }

            // A letter can produce several chars (e.g. the ligature "ﬁ" becomes "fi").
            foreach (char c in value)
            {
                sb.Append(c);
                map.Add(i);
            }
            prev = letter;
        }

        int rotation = ((page.Rotation.Value % 360) + 360) % 360;
        return new PageText(sb.ToString(), map.ToArray(), letters, page.CropBox.Bounds, rotation);
    }

    /// <summary>
    /// Rectangles covering Text[start .. start+length), in PDF points relative to the
    /// top-left corner of the displayed (cropped and rotated) page. A hit that spans
    /// several lines yields one rectangle per line.
    /// </summary>
    public List<Rect> GetRects(int start, int length)
    {
        var result = new List<Rect>();
        int end = Math.Min(Text.Length, start + length);
        int lastLetter = -1;
        Rect? current = null;

        for (int c = Math.Max(0, start); c < end; c++)
        {
            int index = _letterOfChar[c];
            if (index < 0 || index == lastLetter) continue; // inserted space / same letter again
            lastLetter = index;

            var letter = _letters[index];
            if (IsBlank(letter)) continue;

            Rect r = ToDisplay(letter.GlyphRectangle);
            if (r.Width <= 0 || r.Height <= 0) continue;

            if (current is Rect cur && OnSameLine(cur, r))
            {
                current = Rect.Union(cur, r);
            }
            else
            {
                if (current is Rect done) result.Add(done);
                current = r;
            }
        }

        if (current is Rect last) result.Add(last);
        return result;
    }

    // ───────────────────────── helpers ─────────────────────────

    private static bool IsBlank(Letter letter) => string.IsNullOrWhiteSpace(letter.Value);

    // Compatibility normalization (ligatures, non-breaking spaces, ...) so that
    // searching for "fi" also finds "ﬁ".
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return value; // invalid surrogate pairs etc.
        }
    }

    private static bool IsWordGap(Letter prev, Letter cur)
    {
        // Font size in points; fall back to the glyph heights if a PDF reports no size.
        double size = Math.Max(prev.PointSize, cur.PointSize);
        if (size <= 0) size = Math.Max(prev.FontSize, cur.FontSize);
        if (size <= 0) size = Math.Max(prev.GlyphRectangle.Height, cur.GlyphRectangle.Height);
        if (size <= 0) return false;

        // Distance from the end of the previous letter to the start of the next one.
        // Works for new lines as well, because the distance is then large in y.
        double dx = cur.StartBaseLine.X - prev.EndBaseLine.X;
        double dy = cur.StartBaseLine.Y - prev.EndBaseLine.Y;
        return Math.Sqrt(dx * dx + dy * dy) > size * SpaceGapFactor;
    }

    // Two rectangles belong to the same line if they overlap vertically by at least half.
    private static bool OnSameLine(Rect a, Rect b)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        double minHeight = Math.Min(a.Height, b.Height);
        return minHeight > 0 && overlap >= 0.5 * minHeight;
    }

    // PDF user space (origin bottom-left, unrotated) -> displayed page (origin top-left).
    private Rect ToDisplay(PdfRectangle r)
    {
        Point a = Map(r.Left, r.Bottom);
        Point b = Map(r.Right, r.Top);
        return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private Point Map(double x, double y)
    {
        double u = x - _crop.Left;    // position inside the crop box, from the bottom-left corner
        double v = y - _crop.Bottom;
        double w = _crop.Width;
        double h = _crop.Height;

        return _rotation switch
        {
            90 => new Point(v, u),
            180 => new Point(w - u, v),
            270 => new Point(h - v, w - u),
            _ => new Point(u, h - v)
        };
    }
}
