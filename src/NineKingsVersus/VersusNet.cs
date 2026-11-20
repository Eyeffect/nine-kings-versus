using System;
using System.IO;
using System.Text.Json;

namespace NineKingsVersus;

/// <summary>
/// Match network facade. Encoding and sockets live in ITransport; sequencing,
/// dedup and ack tracking live in VersusQueue. This class only maps decoded
/// messages onto the properties the game flow already reads.
///
/// Config: BepInEx/versus/net.json
///   { "mode": "host" | "join", "peer": "192.168.x.y", "port": 28764 }
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
        public int loser { get; set; } = -1;
        public int eliminated { get; set; } = -1;
    }

    private static readonly object Lock = new();
    private static ITransport _transport;
    private static readonly VersusQueue Queue = new();
    private static int _seq;
    private static int _localPlayerId;

    public static string Mode => _transport?.Mode ?? "off";
    public static bool Connected => _transport?.Connected ?? false;
    public static int StateVersion { get; private set; }

    /// <summary>Latest opponent snapshot JSON (main-thread accessible).</summary>
    public static string OpponentSnapshotJson { get; private set; }

    public static int OpponentWave { get; private set; } = -1;

    /// <summary>Wave number reported ready by the peer, or -1.</summary>
    public static int PeerReadyWave { get; private set; } = -1;

    /// <summary>Wave number the peer told us to start, or -1.</summary>
    public static int GoWave { get; private set; } = -1;

    /// <summary>Peer's current life count, or -1 until first reported.</summary>
    public static int PeerLives { get; private set; } = -1;

    /// <summary>Wave number the host reports, for keeping both copies in sync.</summary>
    public static int HostWave { get; private set; } = -1;

    /// <summary>Host-authoritative loser (0=host, 1=join) of the last resolved wave.</summary>
    public static int WaveWinLoser { get; private set; } = -1;

    /// <summary>Wave number of the last resolved wave, or -1.</summary>
    public static int WaveWinWave { get; private set; } = -1;

    /// <summary>Local life count from the latest captured snapshot.</summary>
    public static int LocalLives { get; private set; } = -1;

    /// <summary>Host-authoritative loser (0=host, 1=join), or -1.</summary>
    public static int ResultLoser { get; private set; } = -1;

    /// <summary>Wave at which the peer's army was destroyed, or -1.</summary>
    public static int PeerEliminatedWave { get; private set; } = -1;

    public static void SetLocalLives(int lives)
    {
        LocalLives = lives;
    }

    public static void SetPeerLives(int lives)
    {
        PeerLives = lives;
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
                VersusPlugin.Log.LogWarning("net: invalid net.json");
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

            if (mode == "host")
            {
                _localPlayerId = 0;
                transport.StartHost(settings.port);
            }
            else
            {
                _localPlayerId = 1;
                transport.StartJoin(settings.peer, settings.port);
            }

            _transport = transport;
            VersusPlugin.Log.LogInfo($"net: {Mode} ready on port {settings.port} peer={settings.peer}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net init failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Called from Update. Publishes decoded network data to the main thread.</summary>
    public static void Tick()
    {
        var transport = _transport;
        if (transport == null)
        {
            return;
        }

        while (transport.TryReceive(out var json))
        {
            var message = Decode(json);
            if (message != null)
            {
                Queue.Enqueue(message);
            }
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
                        PeerLives = message.Lives;
                        VersusPlugin.Log.LogInfo($"net: peer lives={message.Lives}");
                    }
                    VersusPlugin.Log.LogInfo($"net: received opponent state wave={message.Wave} bytes={message.Snapshot?.Length ?? 0}");
                    break;
                case "ready":
                    PeerReadyWave = message.Wave;
                    VersusPlugin.Log.LogInfo($"net: peer ready for wave {message.Wave}");
                    break;
                case "go":
                    GoWave = message.Wave;
                    VersusPlugin.Log.LogInfo($"net: peer says go for wave {message.Wave}");
                    break;
                case "result":
                    ResultLoser = message.Loser;
                    VersusPlugin.Log.LogInfo($"net: authoritative result loser={(message.Loser == 0 ? "host" : "join")}");
                    break;
                case "eliminated":
                    PeerEliminatedWave = message.Eliminated >= 0 ? message.Eliminated : message.Wave;
                    VersusPlugin.Log.LogInfo($"net: peer army eliminated at wave {PeerEliminatedWave}");
                    break;
                case "sync":
                    HostWave = message.Wave;
                    break;
                case "wavewin":
                    WaveWinLoser = message.Loser;
                    WaveWinWave = message.Wave;
                    VersusPlugin.Log.LogInfo($"net: wave {message.Wave} winner decided, loser={(message.Loser == 0 ? "host" : "join")}");
                    break;
            }
        }
    }

    public static void SendState(int wave, string snapshotJson, int lives)
    {
        Send(new Message { type = "state", wave = wave, snapshot = snapshotJson, lives = lives });
    }

    public static void SendReady(int wave)
    {
        Send(new Message { type = "ready", wave = wave });
    }

    public static void SendGo(int wave)
    {
        Send(new Message { type = "go", wave = wave });
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
        Queue.Reset();
        // Seq stays monotonic so the peer's dedup window does not reject us.
    }

    public static void Stop()
    {
        _transport?.Stop();
    }

    private static void Send(Message message)
    {
        var transport = _transport;
        if (transport == null)
        {
            VersusPlugin.Log.LogWarning($"net: cannot send {message.type}: not connected");
            return;
        }

        lock (Lock)
        {
            message.playerId = _localPlayerId;
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
                Loser = root.TryGetProperty("loser", out var loser) ? loser.GetInt32() : -1,
                Eliminated = root.TryGetProperty("eliminated", out var eliminated) ? eliminated.GetInt32() : -1,
                Snapshot = root.TryGetProperty("snapshot", out var snapshot) ? snapshot.GetString() : null,
            };
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net: bad message: {e.Message}");
            return null;
        }
    }
}
