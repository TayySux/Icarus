namespace Icarus.Core;

/// <summary>
/// Product and feature names that the claim guard treats as brand labels rather than
/// as performance claims.
///
/// A brand name is a proper noun that happens to contain a word which is otherwise
/// banned in a claim. "Zero Delay" names a feature; "reduces delay to zero" is a claim,
/// and the guard rejects the second while tolerating the first.
///
/// Declaring a name here is the only way to exempt it. The exemption is granted per
/// exact string, so adding a word to this list cannot silence the phrase everywhere: only
/// the declared spelling is skipped, and any surrounding sentence is still scanned.
/// </summary>
public static class ProductNames
{
    /// <summary>
    /// Declared brand strings. Matched case-insensitively on word boundaries, then
    /// removed from the text before the banned-phrase scan runs.
    /// </summary>
    private static readonly string[] Declared =
    [
        "Zero Delay",
    ];

    public static IReadOnlyList<string> All => Declared;

    /// <summary>
    /// Replaces every declared brand name with an equal-length run of underscores, so
    /// offsets stay stable and no remaining text can accidentally form a banned phrase
    /// across the seam of a removed name.
    /// </summary>
    public static string MaskDeclaredNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var name in Declared)
        {
            text = Mask(text, name);
        }
        return text;
    }

    private static string Mask(string text, string name)
    {
        var pattern = $@"(?<![\w]){System.Text.RegularExpressions.Regex.Escape(name)}(?![\w])";
        return System.Text.RegularExpressions.Regex.Replace(
            text, pattern, m => new string('_', m.Length),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
