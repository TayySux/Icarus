using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Icarus.Core;

public enum StepKind { KeyPress, KeyDown, KeyUp, MouseClick, MouseMove, Delay }
public sealed record MacroStep
{
    public StepKind Kind { get; init; }
    public ushort ScanCode { get; init; }
    public bool Extended { get; init; }
    public int MouseButton { get; init; } = 1;
    public int X { get; init; }
    public int Y { get; init; }
    public int DurationMs { get; init; } = 20;
}
public sealed record MacroProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "Untitled";
    public string InputType { get; init; } = "keyboard";
    public string Key { get; init; } = "F8";
    public string FocusWindow { get; init; } = "FortniteClient-Win64-Shipping";
    public double? MeasuredFps { get; init; }
    public int FallbackHoldMs { get; init; } = 25;
    public int GapMs { get; init; } = 10;
    public List<MacroStep> Steps { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported profile version.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("A profile needs a name.");
        if (InputType != "keyboard" || Key != "F8") throw new InvalidDataException("This increment supports the F8 keyboard trigger only.");
        if (MeasuredFps is not null && (!double.IsFinite(MeasuredFps.Value) || MeasuredFps < 1 || MeasuredFps > 2000))
            throw new InvalidDataException("FPS must be a measured positive value, or null when unavailable.");
        if (FallbackHoldMs is < 1 or > 5000 || GapMs is < 1 or > 5000)
            throw new InvalidDataException("Hold and gap must be between 1 and 5000 ms.");
        if (Steps is null || Steps.Count is < 1 or > 1000) throw new InvalidDataException("Use 1 to 1000 steps.");
        if (Extra?.Keys.Any(k => k is "conditions" or "alt_conditions" or "toggle_bind") == true)
            throw new InvalidDataException("Legacy trigger conditions are not implemented. Import is blocked rather than silently changing behavior.");
        foreach (var s in Steps)
        {
            if (!Enum.IsDefined(s.Kind)) throw new InvalidDataException("Unknown step kind.");
            if (s.DurationMs is < 0 or > 60000) throw new InvalidDataException("Step duration must be 0 to 60000 ms.");
            if (s.Kind is StepKind.KeyPress or StepKind.KeyDown or StepKind.KeyUp && s.ScanCode is < 1 or > 255)
                throw new InvalidDataException("Keyboard steps need a scan_code from 1 to 255.");
            if (s.Kind == StepKind.MouseClick && s.MouseButton is < 1 or > 5) throw new InvalidDataException("Mouse buttons: 1 left, 2 right, 3 middle, 4 X1, 5 X2.");
            if (Math.Abs((long)s.X) > 10000 || Math.Abs((long)s.Y) > 10000) throw new InvalidDataException("Mouse movement is limited to 10000 units per axis per step.");
        }
    }
}
public static class ProfileCodec
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false) }
    };
    public static string ToJson(MacroProfile p) { p.Validate(); return JsonSerializer.Serialize(p, Options); }
    public static MacroProfile FromJson(string json)
    {
        if (json.Length > 1_000_000) throw new InvalidDataException("Profile exceeds 1 MB.");
        var p = JsonSerializer.Deserialize<MacroProfile>(json, Options) ?? throw new InvalidDataException("Empty profile.");
        p.Validate(); return p;
    }
    public static string Export(MacroProfile p) => Convert.ToBase64String(Encoding.UTF8.GetBytes(ToJson(p)));
    public static MacroProfile Import(string code)
    {
        if (code.Length > 1_400_000) throw new InvalidDataException("Share code too large.");
        return FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(code)));
    }
}
public static class Timing
{
    // Scheduling target, not a guarantee of delivery or a claim of game acceptance.
    public static int MinimumHold(double? fps, int fallback) => fps is > 0 ? Math.Max(1, (int)Math.Ceiling(1500d / fps.Value)) : fallback;
}
public static class StickMath
{
    public static (double X, double Y) Transform(double x, double y, double inner, double outer, double exponent, double anti)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(inner) || !double.IsFinite(outer) || !double.IsFinite(exponent) || !double.IsFinite(anti) || inner < 0 || outer <= inner || outer > 1 || exponent <= 0 || anti < 0 || anti >= 1)
            throw new ArgumentOutOfRangeException(nameof(inner));
        double magnitude = Math.Sqrt(x * x + y * y);
        if (magnitude <= inner) return (0, 0);
        double t = Math.Clamp((magnitude - inner) / (outer - inner), 0, 1);
        double output = anti + (1 - anti) * Math.Pow(t, exponent);
        return (x / magnitude * output, y / magnitude * output);
    }
}
