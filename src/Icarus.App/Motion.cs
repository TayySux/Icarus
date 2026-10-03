using System.Windows;

namespace Icarus.App;

/// <summary>
/// Motion policy for the interface.
///
/// Durations come from the reference: 150-200ms ease-out on hover, 250ms on panel
/// transitions. When Windows reports that animations are turned off
/// (SystemParameters.ClientAreaAnimation, surfaced by the OS accessibility setting that
/// the reference calls prefers-reduced-motion), every duration collapses to zero so the
/// UI still changes state, it just does so without animation.
/// </summary>
public static class Motion
{
    /// <summary>Hover transitions: 180ms, inside the reference's 150-200ms band.</summary>
    public static readonly Duration Hover = new(TimeSpan.FromMilliseconds(180));

    /// <summary>Panel and layout transitions: 250ms.</summary>
    public static readonly Duration Panel = new(TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// True when the user has asked for reduced motion at the OS level. Read on each call
    /// rather than cached, so changing the setting takes effect without a restart.
    /// ClientAreaAnimation is the WPF surface of the Windows "Animation effects"
    /// accessibility option, which is the equivalent the reference calls
    /// prefers-reduced-motion.
    /// </summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    /// <summary>Duration used when motion is suppressed.</summary>
    public static Duration NoAnimation => new(TimeSpan.Zero);

    /// <summary>
    /// Returns <paramref name="requested"/> unchanged when animation is permitted, or a
    /// zero-length duration when it is not. Every animation in the app routes its
    /// duration through this so the reduced-motion path cannot be forgotten in one place
    /// and missed in another.
    /// </summary>
    public static Duration Apply(Duration requested) => Reduced ? NoAnimation : requested;

    /// <summary>
    /// Runs <paramref name="work"/> immediately when motion is reduced, otherwise after
    /// the requested delay. Used for sequenced card reveals.
    /// </summary>
    public static Task AfterAsync(int milliseconds, Action work, CancellationToken token = default)
    {
        if (Reduced || milliseconds <= 0)
        {
            work();
            return Task.CompletedTask;
        }
        return Task.Delay(milliseconds, token).ContinueWith(_ => work(), token,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
