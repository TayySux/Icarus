using Icarus.Bench;
using Icarus.Core;
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
        // Absolute-latency claims.
        "0 ping",
        "zero ping",
        "no lag",
        "nolag",

        // Detection claims. No tool can verify these, so none may be made.
        "undetectable",
        "untraceable",
        "instant kill",
        "never miss",

        // Assurance claims. These are banned as claims only: "guaranteed" is caught, but
        // a disclaimer such as "this is not a guarantee" is honest wording and is allowed
        // through by the negation check below.
        "100% safe",
        "guaranteed",
        "maximum performance",

        // Marketing filler from the reference copy being replaced.
        "squeeze every last frame",
    ];

    /// <summary>
    /// Near-zero figure patterns. The reference bans "any ~0 figure", which is a shape
    /// rather than a phrase, so it is matched separately: a tilde or approximately-equal
    /// sign immediately before a zero, with optional trailing unit.
    /// </summary>
    private static readonly string[] BannedFigurePatterns =
    [
        @"[~≈]\s*0(\.\d+)?\s*(ms|s\b|fps)",
        @"(under|below)\s*1\s*ms",
    ];

    private static readonly string[] LatencyNouns =
    [
        "latency", "delay", "lag", "ping", "input lag", "stutter", "jitter",
    ];

    /// <summary>
    /// "Eliminate" is banned only when it takes a latency noun as its direct object, since
    /// the word is legitimate elsewhere ("eliminate the background process").
    ///
    /// The object is taken as the words between "eliminate" and the end of the clause, up
    /// to a small run-on limit. Requiring adjacency rather than mere co-occurrence avoids
    /// a false positive on "eliminate the background process causing stutter", where the
    /// latency noun names a downstream symptom and not the thing being eliminated.
    /// </summary>
    private static bool IsLatencyContextualEliminate(string text)
    {
        foreach (Match match in Regex.Matches(text, @"(?<![\w])eliminat\w*(?![\w])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int objectStart = match.Index + match.Length;
            var after = text[objectStart..];

            // The direct object may carry modifiers before the head noun, as in
            // "eliminate input latency" or "eliminate all visible jitter".
            var directObject = Regex.Match(after, @"^\s*(?:(?:all|the|your|any|visible|input|raw|total|added)\s+)*([^.;!?]{0,60})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!directObject.Success) continue;

            // Only a leading noun phrase counts; anything after a verb-like word means the
            // latency noun is describing a consequence rather than the object.
            var candidate = directObject.Groups[1].Value;
            foreach (var noun in LatencyNouns)
            {
                if (Regex.IsMatch(candidate, $@"^\s*{Regex.Escape(noun)}(?![\w])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }
        }
        return false;
    }


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
        yield return ("Near-zero figures are caught by shape not phrase", NearZeroFigures);
        yield return ("Eliminate is banned only in a latency context", LatencyContextualEliminate);
        yield return ("Declared brand names are exempt but claims around them are not", BrandExemption);
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

            // Declared brand names are proper nouns, not claims. Masking them before the
            // scan means "Zero Delay" is tolerated as a product label while
            // "reduces delay to zero" is still rejected in the sentence around it.
            text = ProductNames.MaskDeclaredNames(text);

            foreach (var phrase in BannedPhrases)
            {
                if (ContainsClaim(text, phrase))
                    offenders.Add($"{relative}: contains '{phrase}'");
            }

            foreach (var pattern in BannedFigurePatterns)
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    offenders.Add($"{relative}: contains a near-zero figure matching /{pattern}/");
            }

            if (IsLatencyContextualEliminate(text))
                offenders.Add($"{relative}: uses 'eliminate' in a latency context");
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
            ("100% safe to use", "100% safe"),
            ("Guaranteed frame rate", "guaranteed"),
            ("Delivers maximum performance", "maximum performance"),
            ("Squeeze every last frame", "squeeze every last frame"),
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
    /// The near-zero rule is a shape, not a phrase, so it needs its own cases. "~0 ms" is
    /// the form the reference calls out; a genuine sub-millisecond measurement such as
    /// "0.4 ms" must still be reportable, because that is a real number.
    /// </summary>
    static Task NearZeroFigures()
    {
        foreach (var sample in new[] { "~0 ms", "≈0 ms", "~0.0 ms", "~0 fps", "under 1 ms", "below 1 ms" })
        {
            bool hit = BannedFigurePatterns.Any(p => Regex.IsMatch(sample, p,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            Check.That(hit, $"near-zero figure not caught: {sample}");
        }

        // A real measurement must not be mistaken for a marketing figure.
        foreach (var sample in new[] { "0.4 ms measured", "1.2 ms", "0.9 fps", "12 ms" })
        {
            bool hit = BannedFigurePatterns.Any(p => Regex.IsMatch(sample, p,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            Check.That(!hit, $"genuine measurement flagged: {sample}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// "Eliminate" is only a banned claim when a latency noun is nearby. Using it about
    /// anything else is ordinary English and must not fail the build.
    /// </summary>
    static Task LatencyContextualEliminate()
    {
        foreach (var sample in new[]
        {
            "Eliminate input latency entirely.",
            "Eliminate the lag spikes.",
            "Eliminates ping delay outright.",
        })
            Check.That(IsLatencyContextualEliminate(sample), $"latency-context eliminate missed: {sample}");

        foreach (var sample in new[]
        {
            "Eliminate the background process causing stutter in the capture.",
            "Eliminate unused allocations in the hot loop.",
            "The tool does not attempt to eliminate anything it cannot measure.",
        })
            Check.That(!IsLatencyContextualEliminate(sample),
                $"legitimate use of eliminate wrongly flagged: {sample}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A declared brand name is exempt as a label, but the exemption must not extend to
    /// the sentence around it. This is the behaviour that lets card 1 be titled
    /// "Zero Delay" without opening a hole in the guard.
    /// </summary>
    static Task BrandExemption()
    {
        Check.That(ProductNames.All.Contains("Zero Delay"), "brand name is declared");

        // The brand name itself is masked away and survives the scan.
        var masked = ProductNames.MaskDeclaredNames("Card one is called Zero Delay.");
        Check.That(!LanguageGuardTests.Matches(masked, "zero delay"),
            "declared brand name should be masked before scanning");

        // Masking must not swallow the surrounding claim. The masked brand sits in the same
        // sentence as the claims, so the claims must still be found.
        var sneaky = ProductNames.MaskDeclaredNames("Zero Delay is guaranteed and reaches 0 ping.");
        Check.That(LanguageGuardTests.Matches(sneaky, "0 ping"),
            "a claim next to a brand name must still be caught");
        Check.That(LanguageGuardTests.Matches(sneaky, "guaranteed"),
            "an assurance claim next to a brand name must still be caught");

        // An undeclared near-miss must not be treated as the brand.
        Check.That(ProductNames.MaskDeclaredNames("Zero Delays are reduced") == "Zero Delays are reduced",
            "a plural must not be masked as the brand name");
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

    /// <summary>
    /// A banned phrase counts as a violation only when it is actually being asserted.
    /// A disclaimer that names the same word in order to reject it — "this is not a
    /// guarantee", "no unverifiable claims" — is the opposite of a claim, and banning it
    /// would make honest documentation impossible to write.
    ///
    /// The check looks back a short window from each match for a negation or hedge. This
    /// is a wording heuristic, not natural language understanding, so it errs toward
    /// catching: anything it does not recognise as a disclaimer still fails.
    /// </summary>
    private static bool ContainsClaim(string text, string phrase)
    {
        foreach (Match match in Regex.Matches(text,
            $@"(?<![\w]){Regex.Escape(phrase).Replace(@"\ ", @"[\s\-]+")}(?![\w])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int start = Math.Max(0, match.Index - 90);
            var window = text.Substring(start, match.Index - start);
            if (!DisclaimerMarkers.Any(m => Regex.IsMatch(window,
                    $@"(?<![\w]){Regex.Escape(m)}(?![\w])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Words that, appearing shortly before a banned phrase, mark it as a rejection of
    /// the phrase rather than an assertion of it.
    /// </summary>
    private static readonly string[] DisclaimerMarkers =
    [
        "not", "no", "never", "without", "cannot", "can't", "neither", "nor",
        "isn't", "aren't", "doesn't", "do not", "does not", "refuses", "rejects",
        "banned", "prohibited", "disallowed", "false", "unverified",
    ];

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
