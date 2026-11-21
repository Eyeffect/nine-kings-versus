using System;
using System.Collections.Generic;

namespace NineKingsVersus;

/// <summary>
/// Transport boundary for the private match network. It moves already-encoded
/// JSON frames only: no game state, no IL2CPP objects, no Unity types.
///
/// Host is the star centre: it assigns an id to every accepted peer, relays
/// their frames to the other peers and broadcasts its own. Join talks to
/// exactly one host. Implementations must be safe to use from a background
/// thread for Send/TryReceive.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>"off", "host" or "join".</summary>
    string Mode { get; }

    /// <summary>True while at least one peer link is live.</summary>
    bool Connected { get; }

    /// <summary>Our id in the match: 0 for the host, assigned by the host for a join.</summary>
    int LocalPlayerId { get; }

    /// <summary>Ids of the peers currently linked. Host: joins; join: the host.</summary>
    IReadOnlyList<int> Peers { get; }

    /// <summary>Raised with the peer id when a link drops (main-thread safe to observe).</summary>
    event Action<int> PeerLost;

    /// <summary>Start listening for peers (host) on the given port.</summary>
    void StartHost(int port);

    /// <summary>Connect to a host (join). Implementations must reject public addresses.</summary>
    void StartJoin(string peer, int port);

    /// <summary>Send a frame to every connected peer.</summary>
    void Send(string json);

    /// <summary>Send a frame to one peer.</summary>
    void SendTo(int playerId, string json);

    /// <summary>Send a frame to every peer except one (host-side relay).</summary>
    void SendExcept(int playerId, string json);

    /// <summary>Take one received frame, if any. Called on the main thread.</summary>
    bool TryReceive(out string json);

    /// <summary>Close all links and stop background threads.</summary>
    void Stop();
}
