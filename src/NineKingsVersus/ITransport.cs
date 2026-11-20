using System;

namespace NineKingsVersus;

/// <summary>
/// Transport boundary for the private match network. It moves already-encoded
/// JSON frames only: no game state, no IL2CPP objects, no Unity types.
///
/// Host is the star centre: it accepts every peer and broadcasts what it sends
/// to all of them. Join talks to exactly one host. Implementations must be safe
/// to use from a background thread for Send/TryReceive.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>"off", "host" or "join".</summary>
    string Mode { get; }

    /// <summary>True while at least one peer link is live.</summary>
    bool Connected { get; }

    /// <summary>Start listening for peers (host) on the given port.</summary>
    void StartHost(int port);

    /// <summary>Connect to a host (join). Implementations must reject public addresses.</summary>
    void StartJoin(string peer, int port);

    /// <summary>Send a frame to every connected peer.</summary>
    void Send(string json);

    /// <summary>Take one received frame, if any. Called on the main thread.</summary>
    bool TryReceive(out string json);

    /// <summary>Close all links and stop background threads.</summary>
    void Stop();
}
