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

    private static void ConfigTests()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "versus-config-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var defaults = VersusConfig.Load(dir);
            Check("config defaults", defaults.mode == "off" && defaults.attachFlow && !defaults.autoStart && !defaults.autoVersus);
            Check("config unknown flag off", !defaults.HasFlag("does-not-exist"));

            var json = "{\"mode\":\"host\",\"peer\":\"192.168.1.5\",\"port\":29000," +
                       "\"autoStart\":true,\"autoVersus\":true,\"multiBot\":true,\"arena\":true," +
                       "\"botArmy\":true}";
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, VersusConfig.FileName), json);
            var loaded = VersusConfig.Load(dir);
            Check("config loads values", loaded.mode == "host" && loaded.peer == "192.168.1.5" && loaded.port == 29000 &&
                                        loaded.autoStart && loaded.autoVersus && loaded.multiBot && loaded.arena);
            Check("config IsEnabled maps flags", loaded.HasFlag("auto-versus") && loaded.HasFlag("multi-bot") && loaded.HasFlag("arena"));
            Check("config unlisted switch off", !loaded.HasFlag("native-gauntlet"));
            Check("bot-army switch maps", loaded.HasFlag("bot-army"));

            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "native-gauntlet"), "1");
            var withLegacy = VersusConfig.Load(dir);
            Check("legacy flag overrides config", withLegacy.HasFlag("native-gauntlet"));

            loaded.skipIntro = true;
            loaded.Save();
            var reread = VersusConfig.Load(dir);
            Check("config save roundtrip", reread.skipIntro && reread.mode == "host" && reread.autoStart);

            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, VersusConfig.FileName), "{not json");
            var broken = VersusConfig.Load(dir);
            Check("broken config falls back to defaults", broken.mode == "off" && !broken.autoStart);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    private static void StateTests()
    {
        var state = new MatchState();
        Check("state starts idle", state.Phase == MatchPhase.Idle && state.Round == -1);
        Check("idle cannot start battle", !state.BeginBattle());
        Check("idle cannot finish", !state.Finish());
        Check("rejects same-phase transition", !state.TryTransition(MatchPhase.Idle));

        state.BeginMatch(3, localPlayerId: 1);
        Check("begin match -> connecting", state.Phase == MatchPhase.Connecting && state.PlayerCount == 3 && state.AlivePlayers == 3);
        Check("connecting cannot start battle", !state.BeginBattle());

        state.EnterLobby();
        Check("connecting -> lobby", state.Phase == MatchPhase.Lobby);
        Check("lobby begins round 1", state.BeginRound(1) && state.Phase == MatchPhase.Placing && state.Round == 1);
        Check("placing begins battle", state.BeginBattle() && state.Phase == MatchPhase.Battle);
        Check("battle cannot begin another round", !state.BeginRound(2));

        state.ResolveRound(2, true);
        Check("battle -> resolving", state.Phase == MatchPhase.Resolving && state.LastLoser == 2 && state.AlivePlayers == 2);
        Check("resolving -> next round", state.BeginRound(2) && state.Round == 2 && state.Phase == MatchPhase.Placing);
        state.BeginBattle();
        state.ResolveRound(state.LocalPlayerId, false);
        Check("surviving a round does not reduce alive count", state.AlivePlayers == 2);

        state.Finish();
        Check("resolving -> finished", state.Phase == MatchPhase.Finished);
        Check("finished cannot begin round", !state.BeginRound(3));
        Check("finished -> idle via reset", state.TryTransition(MatchPhase.Idle) && state.Phase == MatchPhase.Idle);

        // Disconnect removes an army and ends the match at one survivor.
        state.BeginMatch(9, localPlayerId: 0);
        state.EnterLobby();
        state.BeginRound(1);
        for (var i = 1; i < 9; i++)
        {
            state.RemovePlayer(i);
        }
        Check("8 disconnects leave 1 alive", state.AlivePlayers == 1);
        Check("last survivor finishes match", state.Phase == MatchPhase.Finished);

        // Transition event fires.
        var seen = 0;
        var events = new MatchState();
        events.Changed += (_, _) => seen++;
        events.BeginMatch(2);
        events.EnterLobby();
        Check("transition events fire", seen == 2);
    }

    private static void BarrierTests()
    {
        var barrier = new MatchBarrier();
        barrier.BeginWave(1, new[] { 0, 1, 2 });
        Check("barrier starts unready", !barrier.AllReady() && barrier.ReadyCount() == 0);
        barrier.MarkReady(0, 1);
        barrier.MarkReady(1, 2);
        Check("later wave ready counts", barrier.ReadyCount() == 2 && !barrier.AllReady());
        barrier.MarkReady(2, 1);
        Check("barrier releases on all ready", barrier.AllReady());

        barrier.MarkLife(0, 1, 2);
        barrier.MarkLife(1, 1, 1);
        Check("barrier waits for all life reports", !barrier.AllReported());
        barrier.MarkLife(2, 1, 0);
        Check("barrier releases on all life reports", barrier.AllReported());
        Check("eliminated player identified", barrier.Eliminated().Length == 1 && barrier.Eliminated()[0] == 2);
        Check("lowest life identified", barrier.LowestLifePlayer() == 2);

        barrier.RemovePlayer(2);
        Check("removed player excluded", barrier.Alive.Count == 2 && barrier.AllReady() && barrier.AllReported());
        Check("lives survive player removal", barrier.GetLives(0) == 2 && barrier.GetLives(2) == 0);

        barrier.BeginWave(2, new[] { 0, 1 });
        Check("new wave resets marks", !barrier.AllReady() && !barrier.AllReported());
        Check("new wave keeps lives", barrier.GetLives(1) == 1);
        barrier.Reset();
        Check("barrier reset", barrier.Wave == -1 && barrier.Alive.Count == 0 && barrier.Lives.Count == 0);
    }

    private static void StarTests()
    {
        var port = 39200;
        using (var host = new TcpTransport())
        using (var join1 = new TcpTransport())
        using (var join2 = new TcpTransport())
        {
            var lost = new List<int>();
            host.PeerLost += id => lost.Add(id);
            host.StartHost(port);
            join1.StartJoin("127.0.0.1", port);
            join2.StartJoin("127.0.0.1", port);

            Check("joins get distinct ids", WaitUntil(() =>
                join1.LocalPlayerId > 0 && join2.LocalPlayerId > 0 &&
                join1.LocalPlayerId != join2.LocalPlayerId, 5000));
            Check("host id is zero", host.LocalPlayerId == 0);
            Check("host sees two peers", WaitUntil(() => host.Peers.Count == 2, 5000));
            Check("join sees the host", join1.Peers.Count == 1 && join1.Peers[0] == 0);

            var fromJoin1 = join1.LocalPlayerId;
            var other = fromJoin1 == 1 ? 2 : 1;
            host.SendExcept(fromJoin1, "{\"type\":\"round\",\"wave\":1}");
            Check("relay skips sender", Receive(join1) == null && WaitUntil(() => Receive(join2) != null, 5000));

            host.SendTo(fromJoin1, $"{{\"type\":\"go\",\"wave\":1,\"to\":{fromJoin1}}}");
            Check("targeted send reaches one peer", WaitUntil(() => Receive(join1) != null, 5000));

            join2.Stop();
            Check("host reports peer loss", WaitUntil(() => lost.Contains(other), 5000));
            Check("host peer list shrinks", WaitUntil(() => host.Peers.Count == 1, 5000));
        }
    }

    private static int Main()
    {
        QueueTests();
        ConfigTests();
        StateTests();
        BarrierTests();
        LoopbackTests();
        StarTests();

        Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : $"{_failed} TEST(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
