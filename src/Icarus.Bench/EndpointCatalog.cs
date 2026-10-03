namespace Icarus.Bench;

/// <summary>
/// Where probe targets come from.
///
/// No endpoint is hardcoded as a verified game server. Fortnite assigns a host per
/// match, and Epic does not publish a fixed list of regional game hostnames. Shipping a
/// guessed list and labelling it "Epic regional endpoints" would mean reporting latency
/// to a host that has nothing to do with the game session.
///
/// Targets therefore arrive from one of two honest sources:
///   - observed live, from connections the game process actually has while running
///   - entered by the user, who can confirm what it is
/// </summary>
public static class EndpointCatalog
{
    /// <summary>
    /// A connected socket belonging to the game process, captured from the OS. This is
    /// the only source that yields a genuinely game-relevant endpoint.
    /// </summary>
    public static ProbeTarget FromObservedConnection(string remoteAddress, int port, string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteAddress);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        string host = remoteAddress;
        // Reverse-resolve when possible so the label names a host rather than a number.
        try
        {
            var entry = System.Net.Dns.GetHostEntry(remoteAddress);
            if (!string.IsNullOrWhiteSpace(entry.HostName)) host = entry.HostName;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
        {
            // No PTR record. The address itself is still a valid target, so keep it.
        }

        return new ProbeTarget
        {
            Id = $"observed:{remoteAddress}:{port}",
            Label = $"{processName} → {host}:{port}",
            Host = remoteAddress,
            Port = port,
            IsVerifiedGameService = true,
        };
    }

    /// <summary>
    /// A target supplied by the user. It is not treated as a verified game service,
    /// because this code has no way to confirm what the host is.
    /// </summary>
    public static ProbeTarget FromUserEntry(string host, int port, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new ProbeTarget
        {
            Id = $"user:{host}:{port}",
            Label = label,
            Host = host,
            Port = port,
            IsVerifiedGameService = false,
        };
    }

    /// <summary>
    /// How a result must be presented when its endpoint was not confirmed to belong to
    /// the game. Returning guidance rather than a figure keeps a real measurement from
    /// being read as a measurement of Fortnite's servers.
    /// </summary>
    public static string DescribeProvenance(ProbeResult result) => result.VerifiedGameService
        ? "Endpoint observed from a live game connection."
        : "Endpoint supplied by the user and not confirmed as a Fortnite game server. "
        + "This measures the path to that host, not the player's in-match latency.";
}
