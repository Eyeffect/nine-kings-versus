using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NineKingsVersus;

/// <summary>
/// Match network facade. Encoding and sockets live in ITransport; sequencing,
/// dedup and ack tracking live in VersusQueue; the round barrier lives in
/// MatchBarrier. This class maps decoded messages onto the match flow.
///
/// The host is the only authority for `go` and for the per-round life table.
/// Every peer frame is relayed to the other peers, so all clients see the same
/// life reports and can compute the same elimination order.
///
/// Config: BepInEx/versus/config.json (mode/peer/port) or legacy net.json.
/// </summary>
internal static class VersusNet
{
    private sealed class Settings
    {
        public string mode { get; set; } = "host";
        public string peer { get; set; } = "127.0.0.1";
        public int port { get; set; } = 28764;
    }

    private sealed class Message
    {
        public string type { get; set; }
        public int playerId { get; set; }
        public int seq { get; set; }
        public int ack { get; set; }
        public int wave { get; set; }
        public string snapshot { get; set; }
        public int lives { get; set; } = -1;
        public int enemyHp { get; set; } = -1;
        public int loser { get; set; } = -1;
        public int eliminated { get; set; } = -1;
        public string livesMap { get; set; }
    }

    private static readonly object Lock = new();
    private static ITransport _transport;
    private static readonly VersusQueue Queue = new();
    private static int _seq;

    public static string Mode => _transport?.Mode ?? "off";
    public static bool Connected => _transport?.Connected ?? false;
    public static int LocalPlayerId => _transport?.LocalPlayerId ?? -1;
    public static IReadOnlyList<int> Peers => _transport?.Peers ?? Array.Empty<int>();
    public static int StateVersion { get; private set; }

    /// <summary>Host-side ready/life barrier. Pure state, safe to inspect from Update.</summary>
    public static MatchBarrier Barrier { get; } = new();

    /// <summary>Latest opponent snapshot JSON (main-thread accessible).</summary>
    public static string OpponentSnapshotJson { get; private set; }
    public static int OpponentWave { get; private set; } = -1;

    /// <summary>Wave number the peer reported ready for, or -1.</summary>
    public static int PeerReadyWave { get; private set; } = -1;

    /// <summary>Wave number the host told us to start, or -1.</summary>
    public static int GoWave { get; private set; } = -1;

    /// <summary>Life table for the last resolved round (playerId -> lives).</summary>
    public static IReadOnlyDictionary<int, int> RoundLives { get; private set; } =
        new Dictionary<int, int>();

    public static int RoundWave { get; private set; } = -1;

    /// <summary>Local life count from the latest native report.</summary>
    public static int LocalLives { get; private set; } = -1;

    /// <summary>First peer's life count; kept for the in-game diagnostic panel.</summary>
    public static int PeerLives
    {
        get
        {
            foreach (var pair in RoundLives)
            {
                if (pair.Key != LocalPlayerId)
                {
                    return pair.Value;
                }
            }

            return -1;
        }
    }

    // Compatibility fields used by the legacy diagnostic menu. They no longer
    // decide a round; the native life table above does.
    public static int HostWave { get; private set; } = -1;
    public static int WaveWinLoser { get; private set; } = -1;
    public static int WaveWinWave { get; private set; } = -1;
    public static int ResultLoser { get; private set; } = -1;
    public static int PeerEliminatedWave { get; private set; } = -1;

    public static void SetLocalLives(int lives)
    {
        LocalLives = lives;
        if (Mode == "host")
        {
            Barrier.SetLives(LocalPlayerId, lives);
        }
    }

    public static void Init()
    {
        try
        {
            var config = VersusConfig.Current;
            Settings settings = null;
            if (config != null && (config.mode == "host" || config.mode == "join"))
            {
                settings = new Settings { mode = config.mode, peer = config.peer, port = config.port };
            }
            else
            {
                var path = Path.Combine(VersusPlugin.DataDir, "net.json");
                if (!File.Exists(path))
                {
                    VersusPlugin.Log.LogInfo("net: no config or net.json; transport disabled");
                    return;
                }

                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            }

            if (settings == null || settings.port < 1 || settings.port > 65535)
            {
                VersusPlugin.Log.LogWarning("net: invalid network config");
                return;
            }

            var mode = (settings.mode ?? "host").Trim().ToLowerInvariant();
            if (mode != "host" && mode != "join")
            {
                VersusPlugin.Log.LogWarning($"net: unknown mode '{mode}'");
                return;
            }

            var transport = new TcpTransport
            {
                Log = line => VersusPlugin.Log.LogInfo(line),
            };
            transport.PeerLost += OnPeerLost;

            if (mode == "host")
            {
                transport.StartHost(settings.port);
            }
            else
            {
                transport.StartJoin(settings.peer, settings.port);
            }

            _transport = transport;
            VersusPlugin.Log.LogInfo($"net: {Mode} ready on port {settings.port} peer={settings.peer} id={LocalPlayerId}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net init failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void OnPeerLost(int playerId)
    {
        VersusPlugin.Log.LogInfo($"net: peer {playerId} left the match");
        Barrier.RemovePlayer(playerId);
        VersusBehaviour.OnPeerLost(playerId);
    }

    /// <summary>Called from Update. Publishes decoded network data to the main thread.</summary>
    public static void Tick()
    {
        var transport = _transport;
        if (transport == null)
        {
            return;
        }

        while (transport.TryReceive(out var raw))
        {
            var message = Decode(raw);
            if (message == null)
            {
                continue;
            }

            if (Mode == "host" && message.PlayerId != LocalPlayerId && !string.IsNullOrEmpty(raw))
            {
                if (Barrier.Wave >= 0 && !Barrier.Alive.Contains(message.PlayerId))
                {
                    Barrier.AddPlayer(message.PlayerId);
                    VersusPlugin.Log.LogInfo($"net: added late peer p{message.PlayerId} to wave {Barrier.Wave} barrier");
                }

                // Star topology: a join's frame is forwarded to the other joins
                // unchanged, including the original sender id and sequence.
                transport.SendExcept(message.PlayerId, raw);
            }

            Queue.Enqueue(message);
        }

        foreach (var message in Queue.Drain())
        {
            switch (message.Type)
            {
                case "state":
                    OpponentSnapshotJson = message.Snapshot;
                    OpponentWave = message.Wave;
                    StateVersion++;
                    if (message.Lives >= 0)
                    {
                        PeerLivesChanged(message.PlayerId, message.Lives);
                    }
                    VersusPlugin.Log.LogInfo($"net: received opponent state wave={message.Wave} bytes={message.Snapshot?.Length ?? 0}");
                    break;
                case "ready":
                    PeerReadyWave = message.Wave;
                    if (Mode == "host")
                    {
                        Barrier.MarkReady(message.PlayerId, message.Wave);
                        VersusPlugin.Log.LogInfo($"net: ready p{message.PlayerId} wave={message.Wave} " +
                            $"({Barrier.ReadyCount()}/{Barrier.Alive.Count})");
                        TryBroadcastGo(message.Wave);
                    }
                    break;
                case "go":
                    GoWave = message.Wave;
                    VersusPlugin.Log.LogInfo($"net: go for wave {message.Wave}");
                    break;
                case "waveend":
                    if (Mode == "host")
                    {
                        Barrier.MarkLife(message.PlayerId, message.Wave, message.Lives);
                        // The opponent army damages the opponent's base in the
                        // game's own multiplayer run data. Mirror that native HP
                        // onto the other player's match life.
                        if (message.EnemyHp >= 0)
                        {
                            foreach (var id in Barrier.Alive)
                            {
                                if (id != message.PlayerId)
                                {
                                    Barrier.SetLives(id, message.EnemyHp);
                                    break;
                                }
                            }
                        }

                        VersusPlugin.Log.LogInfo($"net: waveend p{message.PlayerId} wave={message.Wave} " +
                            $"lives={message.Lives} enemyHp={message.EnemyHp}");
                        TryBroadcastRound(message.Wave);
                    }
                    break;
                case "round":
                    RoundLives = ParseLives(message.LivesMap);
                    RoundWave = message.Wave;
                    VersusPlugin.Log.LogInfo($"net: round {message.Wave} lives={FormatLives(RoundLives)}");
                    break;
                case "sync":
                    HostWave = message.Wave;
                    break;
                case "result":
                    ResultLoser = message.Loser;
                    break;
                case "eliminated":
                    PeerEliminatedWave = message.Eliminated >= 0 ? message.Eliminated : message.Wave;
                    break;
                case "wavewin":
                    WaveWinLoser = message.Loser;
                    WaveWinWave = message.Wave;
                    break;
            }
        }
    }

    /// <summary>Initialise the barrier for a new wave with the currently linked players.</summary>
    public static void BeginWave(int wave)
    {
        var alive = new List<int> { LocalPlayerId };
        foreach (var id in Peers)
        {
            if (id != LocalPlayerId && !alive.Contains(id))
            {
                alive.Add(id);
            }
        }

        Barrier.BeginWave(wave, alive);
        if (Barrier.GetLives(LocalPlayerId) < 0 && LocalLives >= 0)
        {
            Barrier.SetLives(LocalPlayerId, LocalLives);
        }

        VersusPlugin.Log.LogInfo($"net: wave {wave} barrier players=[{string.Join(",", Barrier.Alive)}]");
    }

    /// <summary>Declare this client ready for a wave. Host also marks itself.</summary>
    public static void SendReady(int wave)
    {
        if (Mode == "host")
        {
            Barrier.MarkReady(LocalPlayerId, wave);
            TryBroadcastGo(wave);
        }

        Send(new Message { type = "ready", wave = wave });
    }

    /// <summary>Host releases all players once every alive player is ready.</summary>
    private static void TryBroadcastGo(int wave)
    {
        if (Mode != "host" || wave < Barrier.Wave || GoWave == wave ||
            Barrier.Alive.Count < 2 || !Barrier.AllReady())
        {
            return;
        }

        GoWave = wave;
        Send(new Message { type = "go", wave = wave });
        VersusPlugin.Log.LogInfo($"net: go broadcast for wave {wave}");
    }

    /// <summary>Report the native life count at the end of a wave.</summary>
    public static void SendWaveEnd(int wave, int lives, int enemyHp = -1)
    {
        LocalLives = lives;
        if (Mode == "host")
        {
            Barrier.MarkLife(LocalPlayerId, wave, lives);
            if (enemyHp >= 0)
            {
                foreach (var id in Barrier.Alive)
                {
                    if (id != LocalPlayerId)
                    {
                        Barrier.SetLives(id, enemyHp);
                    }
                }
            }

            TryBroadcastRound(wave);
            return;
        }

        Send(new Message { type = "waveend", wave = wave, lives = lives, enemyHp = enemyHp });
    }

    private static void TryBroadcastRound(int wave)
    {
        if (Mode != "host" || wave < 0 || RoundWave == wave || !Barrier.AllReported(wave))
        {
            return;
        }

        var map = new Dictionary<int, int>();
        foreach (var id in Barrier.Alive)
        {
            map[id] = Barrier.GetLives(id);
        }

        RoundLives = map;
        RoundWave = wave;
        var livesMap = FormatLives(map);
        Send(new Message { type = "round", wave = wave, livesMap = livesMap });
        VersusPlugin.Log.LogInfo($"net: round {wave} resolved, lives={livesMap}");
    }

    private static void PeerLivesChanged(int playerId, int lives)
    {
        if (Mode == "host")
        {
            Barrier.SetLives(playerId, lives);
        }
    }

    private static string FormatLives(IReadOnlyDictionary<int, int> map)
    {
        var parts = new List<string>();
        foreach (var pair in map)
        {
            parts.Add($"{pair.Key}:{pair.Value}");
        }
        return string.Join(",", parts);
    }

    private static Dictionary<int, int> ParseLives(string map)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrEmpty(map))
        {
            return result;
        }

        foreach (var entry in map.Split(','))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var lives))
            {
                result[id] = lives;
            }
        }

        return result;
    }

    public static void SendState(int wave, string snapshotJson, int lives)
    {
        Send(new Message { type = "state", wave = wave, snapshot = snapshotJson, lives = lives });
    }

    public static void SendResult(int loser)
    {
        Send(new Message { type = "result", loser = loser });
    }

    public static void SendEliminated(int wave)
    {
        Send(new Message { type = "eliminated", wave = wave, eliminated = wave });
    }

    public static void SendHostWave(int wave)
    {
        Send(new Message { type = "sync", wave = wave });
    }

    public static void SendWaveWin(int wave, int loser)
    {
        Send(new Message { type = "wavewin", wave = wave, loser = loser });
    }

    /// <summary>Clears per-match handshake/result state for a rematch (transport untouched).</summary>
    public static void ResetMatchState()
    {
        ResultLoser = -1;
        PeerEliminatedWave = -1;
        PeerReadyWave = -1;
        GoWave = -1;
        HostWave = -1;
        WaveWinLoser = -1;
        WaveWinWave = -1;
        OpponentWave = -1;
        OpponentSnapshotJson = null;
        RoundLives = new Dictionary<int, int>();
        RoundWave = -1;
        Queue.Reset();
        Barrier.Reset();
        // Seq stays monotonic so the peer's dedup window does not reject us.
    }

    public static void Stop()
    {
        _transport?.Stop();
    }

    private static void Send(Message message)
    {
        var transport = _transport;
        if (transport == null || transport.LocalPlayerId < 0)
        {
            VersusPlugin.Log.LogWarning($"net: cannot send {message.type}: no player id yet");
            return;
        }

        lock (Lock)
        {
            message.playerId = transport.LocalPlayerId;
            message.seq = ++_seq;
            message.ack = Queue.AckSeq;
        }

        transport.Send(JsonSerializer.Serialize(message));
    }

    private static MatchMessage Decode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            if (string.IsNullOrEmpty(type))
            {
                return null;
            }

            return new MatchMessage
            {
                Type = type,
                PlayerId = root.TryGetProperty("playerId", out var player) ? player.GetInt32() : 0,
                MatchId = 0,
                Round = 0,
                Seq = root.TryGetProperty("seq", out var seq) ? seq.GetInt32() : 0,
                AckSeq = root.TryGetProperty("ack", out var ack) ? ack.GetInt32() : 0,
                Wave = root.TryGetProperty("wave", out var wave) ? wave.GetInt32() : -1,
                Lives = root.TryGetProperty("lives", out var lives) ? lives.GetInt32() : -1,
                EnemyHp = root.TryGetProperty("enemyHp", out var enemyHp) ? enemyHp.GetInt32() : -1,
                Loser = root.TryGetProperty("loser", out var loser) ? loser.GetInt32() : -1,
                Eliminated = root.TryGetProperty("eliminated", out var eliminated) ? eliminated.GetInt32() : -1,
                LivesMap = root.TryGetProperty("livesMap", out var map) ? map.GetString() : null,
                Snapshot = root.TryGetProperty("snapshot", out var snapshot) ? snapshot.GetString() : null,
                Raw = json,
            };
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net: bad message: {e.Message}");
            return null;
        }
    }
}
