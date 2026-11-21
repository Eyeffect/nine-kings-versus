using System;
using System.Collections.Generic;

namespace NineKingsVersus;

/// <summary>
/// Pure, game-free state for the two synchronisation points of one round:
/// every alive player must be ready before the host sends `go`, and every
/// alive player must report the native end-of-wave life count before the
/// round can be resolved. No sockets, no Unity, no timers.
/// </summary>
public sealed class MatchBarrier
{
    private readonly Dictionary<int, int> _ready = new();
    private readonly Dictionary<int, int> _reported = new();
    private readonly Dictionary<int, int> _lives = new();
    private readonly HashSet<int> _alive = new();

    public int Wave { get; private set; } = -1;

    public IReadOnlyDictionary<int, int> Lives => _lives;

    public HashSet<int> Alive => _alive;

    /// <summary>Reset the per-round marks. Lives persist across rounds.</summary>
    public void BeginWave(int wave, IEnumerable<int> alivePlayerIds)
    {
        Wave = wave;
        _ready.Clear();
        _reported.Clear();
        _alive.Clear();
        if (alivePlayerIds != null)
        {
            foreach (var id in alivePlayerIds)
            {
                _alive.Add(id);
            }
        }
    }

    /// <summary>Record an initial life count (host may know it before the first report).</summary>
    public void SetLives(int playerId, int lives)
    {
        _lives[playerId] = lives;
    }

    public bool HasLife(int playerId) => _lives.ContainsKey(playerId);

    public int GetLives(int playerId) => _lives.TryGetValue(playerId, out var lives) ? lives : -1;

    /// <summary>Mark a player ready. A report for a later wave is accepted too.</summary>
    public void MarkReady(int playerId, int wave)
    {
        if (wave >= Wave)
        {
            _ready[playerId] = wave;
        }
    }

    /// <summary>Mark a player's native life report for this wave.</summary>
    public void MarkLife(int playerId, int wave, int lives)
    {
        if (wave < Wave)
        {
            return;
        }

        _lives[playerId] = lives;
        _reported[playerId] = wave;
    }

    /// <summary>Add a player linked after the wave began.</summary>
    public void AddPlayer(int playerId)
    {
        if (playerId >= 0)
        {
            _alive.Add(playerId);
        }
    }

    public void RemovePlayer(int playerId)
    {
        _alive.Remove(playerId);
    }

    /// <summary>Number of alive players who reported ready for this wave.</summary>
    public int ReadyCount()
    {
        var count = 0;
        foreach (var id in _alive)
        {
            if (_ready.TryGetValue(id, out var wave) && wave >= Wave)
            {
                count++;
            }
        }

        return count;
    }

    public bool AllReady()
    {
        if (_alive.Count == 0)
        {
            return false;
        }

        foreach (var id in _alive)
        {
            if (!_ready.TryGetValue(id, out var wave) || wave < Wave)
            {
                return false;
            }
        }

        return true;
    }

    public bool AllReported()
    {
        if (_alive.Count == 0)
        {
            return false;
        }

        foreach (var id in _alive)
        {
            if (!_reported.TryGetValue(id, out var wave) || wave < Wave)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Ids of alive players whose last reported life count hit zero.</summary>
    public int[] Eliminated()
    {
        var result = new List<int>();
        foreach (var id in _alive)
        {
            if (_lives.TryGetValue(id, out var lives) && lives <= 0)
            {
                result.Add(id);
            }
        }

        result.Sort();
        return result.ToArray();
    }

    /// <summary>Id of the alive player with the fewest lives; -1 when unknown.</summary>
    public int LowestLifePlayer()
    {
        var best = -1;
        var fewest = int.MaxValue;
        foreach (var id in _alive)
        {
            if (!_lives.TryGetValue(id, out var lives))
            {
                continue;
            }

            if (lives < fewest)
            {
                fewest = lives;
                best = id;
            }
        }

        return best;
    }

    public void Reset()
    {
        Wave = -1;
        _ready.Clear();
        _reported.Clear();
        _lives.Clear();
        _alive.Clear();
    }
}
