using System;
using System.Collections.Concurrent;

namespace NineKingsVersus;

/// <summary>
/// A decoded match message. Replaces the old "last value wins" slots in VersusNet.
/// Sequence numbers are per sender and start at 1; seq 0 is invalid for a network message.
/// </summary>
public sealed class MatchMessage
{
    public string Type;
    public int PlayerId;
    public int MatchId;
    public int Round;
    public int Seq;
    public int AckSeq;
    public int Wave;
    public int Lives;
    public int Loser;
    public int Eliminated;
    public string LivesMap;
    public string Snapshot;

    /// <summary>Original frame, used by the host to relay a join's message.</summary>
    public string Raw;
}

/// <summary>
/// Thread-safe inbox with per-sender sequence dedup and ack tracking.
/// The network thread enqueues decoded messages; the main thread drains them in order.
/// </summary>
public sealed class VersusQueue
{
    private readonly ConcurrentQueue<MatchMessage> _incoming = new();
    private readonly ConcurrentDictionary<int, int> _lastSeen = new();
    private readonly concurrent_int _ackSeq = new();

    private sealed class concurrent_int
    {
        private int _value;
        public int Value => System.Threading.Volatile.Read(ref _value);
        public void Set(int v) => System.Threading.Volatile.Write(ref _value, v);
    }

    /// <summary>Highest sequence number received from any sender, for acks.</summary>
    public int AckSeq => _ackSeq.Value;

    /// <summary>Number of messages currently queued (diagnostics).</summary>
    public int Count => _incoming.Count;

    /// <summary>
    /// Enqueue a decoded message. Returns false for a duplicate or out-of-order
    /// message (already seen at this or a higher seq for that sender).
    /// </summary>
    public bool Enqueue(MatchMessage message)
    {
        if (message == null || string.IsNullOrEmpty(message.Type) || message.Seq < 1)
        {
            return false;
        }

        {
            var previous = _lastSeen.GetOrAdd(message.PlayerId, 0);
            if (message.Seq <= previous)
            {
                return false;
            }

            // CAS so two network threads cannot both advance past the same seq.
            while (previous < message.Seq)
            {
                var observed = _lastSeen.AddOrUpdate(message.PlayerId, message.Seq,
                    (_, current) => current >= message.Seq ? current : message.Seq);
                if (observed >= message.Seq)
                {
                    break;
                }

                previous = observed;
            }

            if (_ackSeq.Value < message.Seq)
            {
                _ackSeq.Set(message.Seq);
            }
        }

        _incoming.Enqueue(message);
        return true;
    }

    /// <summary>Drain all queued messages in FIFO order.</summary>
    public MatchMessage[] Drain()
    {
        var list = new System.Collections.Generic.List<MatchMessage>();
        while (_incoming.TryDequeue(out var message))
        {
            list.Add(message);
        }

        return list.ToArray();
    }

    /// <summary>Clear all state (new match).</summary>
    public void Reset()
    {
        while (_incoming.TryDequeue(out _))
        {
        }

        _lastSeen.Clear();
        _ackSeq.Set(0);
    }
}
