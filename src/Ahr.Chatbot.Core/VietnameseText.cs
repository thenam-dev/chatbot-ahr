using System.Globalization;
using System.Text;

namespace Ahr.Chatbot.Core;

internal static class VietnameseText
{
    public static string Fold(string value)
    {
        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormD).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        var result = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var character in decomposed)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var normalized = character == 'đ' ? 'd' : character;
            if (normalized is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Append(normalized);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                result.Append(' ');
                lastWasSpace = true;
            }
        }

        return result.ToString().TrimEnd();
    }
}
