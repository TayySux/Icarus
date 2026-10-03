using System.ComponentModel;
using System.Runtime.CompilerServices;
using Icarus.Core;

namespace Icarus.App;

/// <summary>
/// Shared shape for the three capability cards. Every card carries a title, a category
/// line, honest body copy, an action, and a state that is shown as a word rather than a
/// bare spinner.
/// </summary>
public abstract class CardViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Product name. May be a feature brand, which the claim guard exempts.</summary>
    public string Title { get; protected init; } = "";

    /// <summary>Small-caps category line shown under the title.</summary>
    public string Category { get; protected init; } = "";

    /// <summary>Body copy. Makes no claim the app cannot prove.</summary>
    public string Body { get; protected init; } = "";

    /// <summary>Label on the primary action.</summary>
    public string ActionLabel { get; protected init; } = "";

    /// <summary>Stable identifier used to persist card order in the layout.</summary>
    public abstract string ActionKind { get; }

    /// <summary>True while the card's action may be started.</summary>
    public abstract bool CanRun { get; }

    /// <summary>
    /// Present-tense state word shown beside the progress bar: Optimizing...,
    /// Measuring..., Reverting... Never a bare spinner.
    /// </summary>
    public abstract string StateWord { get; }

    /// <summary>Progress 0..1 while running, null when idle.</summary>
    public abstract double? Progress { get; }

    /// <summary>Runs the card's action. Implementations must not invent results.</summary>
    public abstract Task RunAsync(CancellationToken token);
}

/// <summary>One measured row in a card's breakdown.</summary>
public sealed record CardRow(string Label, string Value, string Source, bool IsAvailable);

/// <summary>Helpers that turn a <see cref="Measured{T}"/> into display text.</summary>
public static class CardFormat
{
    /// <summary>Formats a millisecond figure with one decimal place.</summary>
    public static string Ms(double value) => $"{value:0.0} ms";

    /// <summary>Formats a kilometre figure.</summary>
    public static string Km(double value) => value >= 100 ? $"{value:0} km" : $"{value:0.0} km";

    /// <summary>Formats a frame-time figure with two decimals, since 16.67 matters.</summary>
    public static string FrameMs(double value) => $"{value:0.00} ms";

    /// <summary>Formats a rate.</summary>
    public static string Hz(double value) => $"{value:0} Hz";

    /// <summary>
    /// The text shown when there is no value. Always states why, so an empty region is
    /// never mistaken for a good result.
    /// </summary>
    public static string Unavailable(string reason) => reason.Length > 0 ? reason : "not measured";
}
