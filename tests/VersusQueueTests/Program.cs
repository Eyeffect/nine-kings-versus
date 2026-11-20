using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }

    private static string Receive(ITransport transport)
    {
        return transport.TryReceive(out var json) ? json : null;
    }

    private static void QueueTests()
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
    }

    private static void LoopbackTests()
    {
        var port = 39100;

        // Single peer roundtrip
        using (var host = new TcpTransport())
        using (var join = new TcpTransport())
        {
            host.StartHost(port);
            join.StartJoin("127.0.0.1", port);
            Check("loopback both connected", WaitUntil(() => host.Connected && join.Connected, 5000));
            host.Send("{\"type\":\"ready\",\"seq\":1}");
            Check("loopback frame delivered", WaitUntil(() => Receive(join) != null, 5000));
        }

        // Realistic snapshot-sized frame (~30 KB)
        port++;
        using (var host = new TcpTransport())
        using (var join = new TcpTransport())
        {
            host.StartHost(port);
            join.StartJoin("127.0.0.1", port);
            WaitUntil(() => host.Connected && join.Connected, 5000);
            var big = new string('x', 30_000);
            host.Send(big);
            string got = null;
            WaitUntil(() => (got = Receive(join)) != null, 5000);
            Check("30 KB frame intact", got != null && got.Length == 30_000);
        }

        // Duplicate and reorder through transport -> queue
        port++;
        using (var host = new TcpTransport())
        using (var join = new TcpTransport())
        {
            host.StartHost(port);
            join.StartJoin("127.0.0.1", port);
            WaitUntil(() => host.Connected && join.Connected, 5000);

            var inbox = new VersusQueue();
            host.Send("{\"type\":\"state\",\"playerId\":0,\"seq\":3}");
            host.Send("{\"type\":\"state\",\"playerId\":0,\"seq\":2}");
            host.Send("{\"type\":\"state\",\"playerId\":0,\"seq\":3}");
            Thread.Sleep(200);
            var seen = 0;
            for (var i = 0; i < 30 && seen < 3; i++)
            {
                if (join.TryReceive(out var json))
                {
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    var seq = doc.RootElement.GetProperty("seq").GetInt32();
                    if (inbox.Enqueue(new MatchMessage { Type = "state", PlayerId = 0, Seq = seq }))
                    {
                        seen++;
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            Check("dup/reorder dedup end-to-end", seen == 1 && inbox.AckSeq == 3 && inbox.Count == 1);
        }

        // Disconnect detection
        port++;
        using (var host = new TcpTransport())
        {
            var join = new TcpTransport();
            host.StartHost(port);
            join.StartJoin("127.0.0.1", port);
            Check("join connected before stop", WaitUntil(() => host.Connected, 5000));
            join.Stop();
            Check("host sees disconnect", WaitUntil(() => !host.Connected, 5000));
            join.Dispose();
        }

        // Public address rejected
        using (var join = new TcpTransport())
        {
            var rejected = false;
            try { join.StartJoin("8.8.8.8", 12345); }
            catch (ArgumentException) { rejected = true; }
            Check("public peer rejected", rejected);
        }

        // Star topology: host broadcasts to eight joins (up to 9 players)
        port++;
        var joins = new List<TcpTransport>();
        try
        {
            using (var host = new TcpTransport())
            {
                host.StartHost(port);
                for (var i = 0; i < 8; i++)
                {
                    var join = new TcpTransport();
                    join.StartJoin("127.0.0.1", port);
                    joins.Add(join);
                }

                Check("host accepted 8 peers", WaitUntil(() =>
                {
                    foreach (var join in joins)
                    {
                        if (!join.Connected) return false;
                    }
                    return true;
                }, 8000));

                host.Send("{\"type\":\"go\",\"seq\":1}");
                Check("broadcast reached all 8", WaitUntil(() =>
                {
                    foreach (var join in joins)
                    {
                        if (Receive(join) == null) return false;
                    }
                    return true;
                }, 8000));
            }
        }
        finally
        {
            foreach (var join in joins)
            {
                join.Dispose();
            }
        }
    }

    private static int Main()
    {
        QueueTests();
        LoopbackTests();

        Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : $"{_failed} TEST(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
