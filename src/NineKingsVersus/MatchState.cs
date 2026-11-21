using System;
using System.Collections.Generic;

namespace NineKingsVersus;

/// <summary>
/// Where a private match is in its lifecycle. The game flow used to infer this
/// from a dozen independent static booleans, which could disagree with each
/// other. This is the single authoritative phase.
/// </summary>
public enum MatchPhase
{
    Idle,
    Connecting,
    Lobby,
    Placing,
    Battle,
    Resolving,
    Finished,
}

/// <summary>
/// Explicit, validated match lifecycle. Pure state: no sockets, no game objects,
/// so it can be tested without the game and reused by any transport.
/// </summary>
public sealed class MatchState
{
    private static readonly Dictionary<MatchPhase, MatchPhase[]> Allowed = new()
    {
        { MatchPhase.Idle,       new[] { MatchPhase.Connecting, MatchPhase.Lobby } },
        { MatchPhase.Connecting, new[] { MatchPhase.Lobby, MatchPhase.Finished, MatchPhase.Idle } },
        { MatchPhase.Lobby,      new[] { MatchPhase.Placing, MatchPhase.Finished, MatchPhase.Idle } },
        { MatchPhase.Placing,    new[] { MatchPhase.Battle, MatchPhase.Finished, MatchPhase.Idle } },
        { MatchPhase.Battle,     new[] { MatchPhase.Resolving, MatchPhase.Finished, MatchPhase.Idle } },
        { MatchPhase.Resolving,  new[] { MatchPhase.Placing, MatchPhase.Finished, MatchPhase.Idle } },
        { MatchPhase.Finished,   new[] { MatchPhase.Idle } },
    };

    public MatchPhase Phase { get; private set; } = MatchPhase.Idle;
    public int Round { get; private set; } = -1;
    public int PlayerCount { get; private set; }
    public int AlivePlayers { get; private set; }
    public int LocalPlayerId { get; private set; }
    public int LastLoser { get; private set; } = -1;

    /// <summary>Raised on every accepted transition (from, to).</summary>
    public event Action<MatchPhase, MatchPhase> Changed;

    public bool CanTransitionTo(MatchPhase to)
    {
        if (to == Phase)
        {
            return false;
        }
        return Allowed.TryGetValue(Phase, out var targets) && Array.IndexOf(targets, to) >= 0;
    }

    public bool TryTransition(MatchPhase to)
    {
        if (!CanTransitionTo(to))
        {
            return false;
        }

        var from = Phase;
        Phase = to;
        Changed?.Invoke(from, to);
        return true;
    }

    /// <summary>Create a new match for N players and move to Connecting.</summary>
    public void BeginMatch(int playerCount, int localPlayerId = 0)
    {
        if (playerCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount), "A match needs at least two players.");
        }

        Reset();
        PlayerCount = playerCount;
        AlivePlayers = playerCount;
        LocalPlayerId = localPlayerId;
        TryTransition(MatchPhase.Connecting);
    }

    /// <summary>Connected and in the native lobby / start panel.</summary>
    public void EnterLobby()
    {
        TryTransition(MatchPhase.Lobby);
    }

    /// <summary>Start a numbered round. Only valid from Lobby or Resolving.</summary>
    public bool BeginRound(int round)
    {
        if (!TryTransition(MatchPhase.Placing))
        {
            return false;
        }

        Round = round;
        return true;
    }

    /// <summary>Player pressed the native Start Battle button.</summary>
    public bool BeginBattle()
    {
        return TryTransition(MatchPhase.Battle);
    }

    /// <summary>
    /// Native life-loss event decided this round. Only an eliminated player is
    /// removed from the alive count; surviving a round with fewer lives is not
    /// an elimination.
    /// </summary>
    public bool ResolveRound(int loser, bool eliminated)
    {
        if (!TryTransition(MatchPhase.Resolving))
        {
            return false;
        }

        LastLoser = loser;
        if (eliminated && loser >= 0)
        {
            RemovePlayer(loser);
        }
        return true;
    }

    /// <summary>The match is over; official end screen can be shown.</summary>
    public bool Finish()
    {
        return TryTransition(MatchPhase.Finished);
    }

    /// <summary>Remove a disconnected player's army from later rounds.</summary>
    public void RemovePlayer(int playerId)
    {
        if (AlivePlayers <= 1)
        {
            return;
        }

        AlivePlayers--;
        if (AlivePlayers == 1 && Phase != MatchPhase.Finished)
        {
            TryTransition(MatchPhase.Finished);
        }
    }

    public void Reset()
    {
        Phase = MatchPhase.Idle;
        Round = -1;
        PlayerCount = 0;
        AlivePlayers = 0;
        LocalPlayerId = 0;
        LastLoser = -1;
    }
}
