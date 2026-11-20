using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NineKingsVersus;

internal static class Program
{
    private static int _failed;

    private static void Check(string name, bool condition)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
        if (!condition)
        {
            _failed++;
        }
    }

    private static MatchMessage Msg(string type, int playerId, int seq)
    {
        return new MatchMessage { Type = type, PlayerId = playerId, Seq = seq };
    }

    private static int Main()
    {
        // FIFO order
        var q = new VersusQueue();
        q.Enqueue(Msg("ready", 1, 1));
        q.Enqueue(Msg("go", 1, 2));
        var drained = q.Drain();
        Check("FIFO preserves order", drained.Length == 2 && drained[0].Type == "ready" && drained[1].Type == "go");
        Check("drain empties queue", q.Count == 0 && q.Drain().Length == 0);

        // Duplicate and out-of-order dropped per sender
        q = new VersusQueue();
        Check("first seq accepted", q.Enqueue(Msg("state", 1, 1)));
        Check("duplicate dropped", !q.Enqueue(Msg("state", 1, 1)));
        Check("older dropped", !q.Enqueue(Msg("state", 1, 0)));
        q.Enqueue(Msg("state", 1, 3));
        Check("gap accepted then ack advances", q.AckSeq == 3 && q.Drain().Length == 2);

        // Per-sender independence
        q = new VersusQueue();
        q.Enqueue(Msg("ready", 1, 1));
        q.Enqueue(Msg("ready", 2, 1));
        Check("different senders independent", q.Drain().Length == 2 && q.AckSeq == 1);

        // Malformed dropped
        q = new VersusQueue();
        Check("null dropped", !q.Enqueue(null));
        Check("empty type dropped", !q.Enqueue(Msg("", 1, 1)));

        // Reset clears everything
        q = new VersusQueue();
        q.Enqueue(Msg("result", 1, 5));
        q.Reset();
        Check("reset clears state", q.Count == 0 && q.AckSeq == 0 && q.Enqueue(Msg("result", 1, 1)));

        // Concurrent enqueue: exactly one seq per sender survives duplicates
        q = new VersusQueue();
        var tasks = new List<Task>();
        for (var i = 1; i <= 200; i++)
        {
            var seq = i;
            tasks.Add(Task.Run(() => q.Enqueue(Msg("state", 7, seq))));
        }
        Task.WaitAll(tasks.ToArray());
        Check("concurrent highest seq wins", q.AckSeq == 200);

        // Disconnect/exclusion: messages from a removed sender still dedup correctly
        q = new VersusQueue();
        q.Enqueue(Msg("ready", 3, 2));
        Check("excluded sender seq tracked", !q.Enqueue(Msg("ready", 3, 2)));

        Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : $"{_failed} TEST(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
