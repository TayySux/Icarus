namespace Icarus.Core;

/// <summary>
/// Where a displayed number came from. A figure with no provenance is not displayed,
/// so every value the UI renders is wrapped in a <see cref="Measured{T}"/> carrying one
/// of these.
/// </summary>
public enum Provenance
{
    /// <summary>No value exists. The UI states the reason instead of showing a figure.</summary>
    Unavailable,

    /// <summary>Measured during this session, moments ago.</summary>
    MeasuredNow,

    /// <summary>Measured earlier, at the stated time, under the stated conditions.</summary>
    MeasuredEarlier,

    /// <summary>
    /// Not measured. The value is a user-chosen fallback, which is a scheduling input
    /// and never evidence of performance.
    /// </summary>
    UserSetDefault,
}

/// <summary>
/// A value together with where it came from and when. Formatting goes through
/// <see cref="Describe"/> so no screen can render a bare number without its source.
/// </summary>
public sealed record Measured<T>
{
    public required Provenance Source { get; init; }
    public T? Value { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>Why the value is missing, or a caveat that must accompany it.</summary>
    public string? Note { get; init; }

    public bool HasValue => Value is not null && Source != Provenance.Unavailable;

    /// <summary>A figure the user chose because no measurement exists. Not a result.</summary>
    public static Measured<T> Default(T value, string note) => new()
    {
        Source = Provenance.UserSetDefault,
        Value = value,
        Note = note,
    };

    public static Measured<T> Now(T value, string? note = null) => new()
    {
        Source = Provenance.MeasuredNow,
        Value = value,
        MeasuredAt = DateTimeOffset.Now,
        Note = note,
    };

    public static Measured<T> Earlier(T value, DateTimeOffset when, string? note = null) => new()
    {
        Source = Provenance.MeasuredEarlier,
        Value = value,
        MeasuredAt = when,
        Note = note,
    };

    /// <summary>No figure. <paramref name="reason"/> is shown in place of the number.</summary>
    public static Measured<T> Missing(string reason) => new()
    {
        Source = Provenance.Unavailable,
        Note = reason,
    };

    /// <summary>
    /// Short source label for a caption. Never returns an empty string, so a caption
    /// cannot accidentally imply a measurement that did not happen.
    /// </summary>
    public string SourceLabel() => Source switch
    {
        Provenance.MeasuredNow => "measured now",
        Provenance.MeasuredEarlier when MeasuredAt is not null
            => $"measured {MeasuredAt.Value:yyyy-MM-dd HH:mm:ss}",
        Provenance.MeasuredEarlier => "measured earlier",
        Provenance.UserSetDefault => "user-set default, not measured",
        _ => "not measured",
    };

    /// <summary>
    /// The text a screen shows for this value: the formatted number plus its source, or
    /// the reason when there is no number.
    /// </summary>
    public string Describe(Func<T, string> format)
    {
        if (!HasValue) return Note is { Length: > 0 } reason ? $"— ({reason})" : "— (not measured)";
        string text = $"{format(Value!)} · {SourceLabel()}";
        return Note is { Length: > 0 } caveat ? $"{text} · {caveat}" : text;
    }
}
