using System.Text;

namespace FreeSpace.Infrastructure.Files;

/// <summary>
/// RFC 6266 Content-Disposition values: an ASCII <c>filename</c> fallback plus <c>filename*</c> in
/// UTF-8, so names with accents or emoji survive in every browser.
/// </summary>
public static class ContentDispositions
{
    public static string Attachment(string fileName) => Build("attachment", fileName);

    public static string Inline(string fileName) => Build("inline", fileName);

    private static string Build(string type, string fileName)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var c in fileName.Normalize(NormalizationForm.FormD))
        {
            if (c is >= ' ' and < (char)127 && c is not '"' and not '\\') ascii.Append(c);
            else if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) ascii.Append('_');
        }
        return $"{type}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}
