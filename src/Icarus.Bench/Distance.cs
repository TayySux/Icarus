namespace Icarus.Bench;

/// <summary>
/// Converts round trip time into a distance floor using the speed of light in fibre.
/// This exists to make the physical floor visible: a server 1200 km away cannot be
/// reached in 20 ms, and saying so is more useful than a bare number.
/// </summary>
public static class Distance
{
    /// <summary>Speed of light in optical fibre, km per millisecond (~200,000 km/s).</summary>
    public const double SpeedOfLightKmPerMs = 200.0;

    /// <summary>
    /// Lower bound on round trip path length implied by a measured round trip time.
    /// This is a floor, not an estimate of real distance: the actual path includes
    /// routing detours, and the caller's known great-circle distance is typically
    /// larger. Returns null when no measurement exists.
    /// </summary>
    public static double? PathFloorKm(double? roundTripMs)
    {
        if (roundTripMs is null || roundTripMs <= 0) return null;
        return roundTripMs.Value * SpeedOfLightKmPerMs;
    }

    /// <summary>
    /// True when the measured round trip is faster than the propagation floor for the
    /// stated distance, which means either the distance or the measurement is wrong.
    /// </summary>
    public static bool IsPhysicallyInconsistent(double? roundTripMs, double? knownDistanceKm)
    {
        if (roundTripMs is not > 0 || knownDistanceKm is not > 0) return false;
        double floorMs = knownDistanceKm.Value / SpeedOfLightKmPerMs;
        return roundTripMs.Value < floorMs;
    }
}
