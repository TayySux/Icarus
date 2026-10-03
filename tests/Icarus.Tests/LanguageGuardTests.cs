using Icarus.Bench;
using System.Text.RegularExpressions;

namespace Icarus.Tests;

/// <summary>
/// Repository-wide guard for the claims that are not permitted anywhere in the
/// product. This runs as a test rather than a review step so a banned phrase cannot
/// reappear in a string, tooltip, or document without failing the build.
///
/// The phrases are checked case-insensitively across source, markup and documentation,
/// and matched as whole words so that a legitimate sentence such as "there is no
/// measurable difference" is not mistaken for the banned claim.
/// </summary>
internal static class LanguageGuardTests
{
    private static readonly string[] BannedPhrases =
    [
        "0 ping",
        "zero ping",
        "no lag",
        "nolag",
        "undetectable",
        "untraceable",
        "instant kill",
        "never miss",
    ];

    /// <summary>
    /// Files that legitimately discuss the ban itself. Their text is skipped because
    /// the guard is defined in terms of these very words.
    /// </summary>
    private static readonly string[] AllowedFiles =
    [
        "LanguageGuardTests.cs",
    ];

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("No banned performance claims anywhere in the repository", NoBannedPhrases);
        yield return ("The claim guard actually detects violations", GuardDetectsViolations);
    }

    static Task NoBannedPhrases()
    {
        string root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerableExtensions.EnumerateSourceFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (AllowedFiles.Any(a => relative.EndsWith(a, StringComparison.OrdinalIgnoreCase))) continue;
            // Only text formats can carry user-visible claims.
            if (!IsTextSource(file)) continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }

            foreach (var phrase in BannedPhrases)
            {
                if (ContainsPhrase(text, phrase))
                    offenders.Add($"{relative}: contains '{phrase}'");
            }
        }

        Check.That(offenders.Count == 0,
            "Banned performance claims found:\n  " + string.Join("\n  ", offenders));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A guard that cannot fail proves nothing. These cases confirm the matcher catches
    /// each banned claim in the forms such copy actually takes, while leaving honest
    /// wording alone.
    /// </summary>
    static Task GuardDetectsViolations()
    {
        (string Sample, string Phrase)[] mustCatch =
        [
            ("Achieves 0 ping in every region.", "0 ping"),
            ("ACHIEVES ZERO PING", "zero ping"),
            ("Reduces no lag instantly.", "no lag"),
            ("No-Lag mode enabled", "no lag"),
            ("Completely undetectable by anti-cheat", "undetectable"),
            ("Undetectable", "undetectable"),
        ];
        foreach (var (sample, phrase) in mustCatch)
            Check.That(LanguageGuardTests.Matches(sample, phrase), $"guard missed '{phrase}' in: {sample}");

        // Honest wording that must survive: stating a measurement made no difference is
        // not the banned claim, and neither is describing a ping value.
        string[] mustAllow =
        [
            "This change made no measurable difference on this machine.",
            "Median latency measured at 21 ms.",
            "Loss percentage was zero.",
            "The lag spike was traced to a background process.",
            "Undetected input is not detected input.",
        ];
        foreach (var sample in mustAllow)
        {
            foreach (var phrase in new[] { "0 ping", "zero ping", "no lag", "undetectable" })
                Check.That(!LanguageGuardTests.Matches(sample, phrase),
                    $"guard false-positived on '{sample}' for '{phrase}'");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whole-word, case-insensitive match with a small allowance for the hyphen and
    /// space variants that appear in marketing-style copy.
    /// </summary>
    /// <summary>
    /// Proves the guard can actually fail. Scans a synthetic string rather than a real
    /// file, so this verifies the matching logic without needing a banned phrase to
    /// exist in the repository in order to test it.
    /// </summary>
    internal static bool Matches(string text, string phrase) => ContainsPhrase(text, phrase);

    private static bool ContainsPhrase(string text, string phrase)
    {
        return Regex.IsMatch(
            text,
            $@"(?<![\w]){Regex.Escape(phrase).Replace(@"\ ", @"[\s\-]+")}(?![\w])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsTextSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".xaml" or ".axaml" or ".md"
            or ".yml" or ".yaml" or ".json" or ".props" or ".csproj" or ".txt";

    /// <summary>
    /// Walks up from the test binary to the directory containing the solution, so the
    /// guard works the same whether tests run from the repo or from a build output.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}

internal static class EnumerableExtensions
{
    /// <summary>Source files only, skipping build output and version control directories.</summary>
    public static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var skip = new[] { "bin", "obj", ".git", ".vs", "node_modules", "artifacts", "publish" };
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => skip.Contains(s, StringComparer.OrdinalIgnoreCase))) continue;
            yield return file;
        }
    }
}
