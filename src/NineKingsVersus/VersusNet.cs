using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace NineKingsVersus;

/// <summary>
/// Minimal LAN-only transport for two private copies of the game.
///
/// Config: BepInEx/versus/net.json
///   { "mode": "host" | "join", "peer": "192.168.x.y", "port": 28764 }
///
/// Messages are length-prefixed UTF-8 JSON, so the background reader never
/// touches IL2CPP objects: it only hands raw JSON strings to the main thread.
/// Only private/loopback peer addresses are accepted; this never contacts the
/// game's servers or the internet.
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
        public int wave { get; set; }
        public string snapshot { get; set; }
        public int lives { get; set; } = -1;
        public int loser { get; set; } = -1;
        public int eliminated { get; set; } = -1;
    }

    private static readonly object Lock = new();
    private static TcpListener _listener;
    private static TcpClient _client;
    private static Thread _thread;
    private static volatile bool _running;
    private static volatile bool _connected;
    private static Stream _stream;

    private static string _incomingState;
    private static int _incomingWave;
    private static int _incomingReadyWave = -1;
    private static int _incomingGoWave = -1;
    private static int _incomingLives = -1;
    private static int _incomingLoser = -1;
    private static int _incomingEliminated = -1;
    private static int _stateVersion;

    public static string Mode { get; private set; } = "off";
    public static bool Connected => _connected;
    public static int StateVersion => _stateVersion;

    /// <summary>Latest opponent snapshot JSON (main-thread accessible).</summary>
    public static string OpponentSnapshotJson { get; private set; }

    public static int OpponentWave { get; private set; } = -1;

    /// <summary>Wave number reported ready by the peer, or -1.</summary>
    public static int PeerReadyWave { get; private set; } = -1;

    /// <summary>Wave number the peer told us to start, or -1.</summary>
    public static int GoWave { get; private set; } = -1;

    /// <summary>Peer's current life count, or -1 until first reported.</summary>
    public static int PeerLives { get; private set; } = -1;

    /// <summary>Local life count from the latest captured snapshot.</summary>
    public static int LocalLives { get; private set; } = -1;

    public static void SetLocalLives(int lives)
    {
        LocalLives = lives;
    }

    /// <summary>Host-authoritative loser (0=host, 1=join), or -1.</summary>
    public static int ResultLoser { get; private set; } = -1;

    /// <summary>Wave at which the peer's army was destroyed, or -1.</summary>
    public static int PeerEliminatedWave { get; private set; } = -1;

    public static void Init()
    {
        try
        {
            var path = Path.Combine(VersusPlugin.DataDir, "net.json");
            if (!File.Exists(path))
            {
                VersusPlugin.Log.LogInfo("net: no net.json; transport disabled");
                return;
            }

            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            if (settings == null || settings.port < 1 || settings.port > 65535)
            {
                VersusPlugin.Log.LogWarning("net: invalid net.json");
                return;
            }

            Mode = (settings.mode ?? "host").Trim().ToLowerInvariant();
            if (Mode != "host" && Mode != "join")
            {
                VersusPlugin.Log.LogWarning($"net: unknown mode '{Mode}'");
                Mode = "off";
                return;
            }

            if (Mode == "join" && !IsPrivateAddress(settings.peer))
            {
                VersusPlugin.Log.LogWarning($"net: refusing non-private peer address '{settings.peer}'");
                Mode = "off";
                return;
            }

            _running = true;
            _thread = new Thread(Mode == "host" ? HostLoop : JoinLoop) { IsBackground = true };
            _thread.Start(settings);
            VersusPlugin.Log.LogInfo($"net: {Mode} starting on port {settings.port} peer={settings.peer}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net init failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Called from Update. Publishes network data to the main thread.</summary>
    public static void Tick()
    {
        string state;
        int stateWave;
        int readyWave;
        int goWave;
        int lives;
        int loser;
        int eliminated;

        lock (Lock)
        {
            state = _incomingState;
            stateWave = _incomingWave;
            readyWave = _incomingReadyWave;
            goWave = _incomingGoWave;
            lives = _incomingLives;
            loser = _incomingLoser;
            eliminated = _incomingEliminated;
            _incomingState = null;
            _incomingReadyWave = -1;
            _incomingGoWave = -1;
            _incomingLoser = -1;
            _incomingEliminated = -1;
        }

        if (state != null)
        {
            OpponentSnapshotJson = state;
            OpponentWave = stateWave;
            _stateVersion++;
            VersusPlugin.Log.LogInfo($"net: received opponent state wave={stateWave} bytes={state.Length}");
        }

        if (readyWave >= 0)
        {
            PeerReadyWave = readyWave;
            VersusPlugin.Log.LogInfo($"net: peer ready for wave {readyWave}");
        }

        if (goWave >= 0)
        {
            GoWave = goWave;
            VersusPlugin.Log.LogInfo($"net: peer says go for wave {goWave}");
        }

        if (lives >= 0)
        {
            PeerLives = lives;
        }

        if (loser >= 0)
        {
            ResultLoser = loser;
            VersusPlugin.Log.LogInfo($"net: authoritative result loser={(loser == 0 ? "host" : "join")}");
        }

        if (eliminated >= 0)
        {
            PeerEliminatedWave = eliminated;
            VersusPlugin.Log.LogInfo($"net: peer army eliminated at wave {eliminated}");
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

    public static void Stop()
    {
        _running = false;
        try
        {
            _stream?.Close();
            _client?.Close();
            _listener?.Stop();
        }
        catch
        {
        }
    }

    private static void Send(Message message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message);
            var payload = Encoding.UTF8.GetBytes(json);
            var length = BitConverter.GetBytes(payload.Length);
            Stream stream;
            lock (Lock)
            {
                stream = _stream;
            }

            if (stream == null)
            {
                VersusPlugin.Log.LogWarning($"net: cannot send {message.type}: not connected");
                return;
            }

            lock (stream)
            {
                stream.Write(length, 0, length.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net send {message.type} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void HostLoop(object state)
    {
        var settings = (Settings)state;
        try
        {
            _listener = new TcpListener(IPAddress.Any, settings.port);
            _listener.Start();
            VersusPlugin.Log.LogInfo("net: host listening");
            using (var client = _listener.AcceptTcpClient())
            {
                StartReader(client);
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net host failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _connected = false;
            VersusPlugin.Log.LogWarning("net: host peer disconnected");
        }
    }

    private static void JoinLoop(object state)
    {
        var settings = (Settings)state;
        try
        {
            var client = new TcpClient();
            var result = client.BeginConnect(IPAddress.Parse(settings.peer), settings.port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)) || !client.Connected)
            {
                client.Close();
                VersusPlugin.Log.LogWarning("net: join timed out");
                return;
            }

            client.NoDelay = true;
            VersusPlugin.Log.LogInfo("net: joined host");
            StartReader(client);
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net join failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _connected = false;
            VersusPlugin.Log.LogWarning("net: host disconnected");
        }
    }

    private static void StartReader(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            lock (Lock)
            {
                _client = client;
                _stream = stream;
                _connected = true;
            }

            var header = new byte[4];
            while (_running)
            {
                var read = ReadExact(stream, header, 4);
                if (read < 4)
                {
                    break;
                }

                var length = BitConverter.ToInt32(header, 0);
                if (length < 2 || length > 4 * 1024 * 1024)
                {
                    VersusPlugin.Log.LogWarning($"net: rejecting frame length {length}");
                    break;
                }

                var payload = new byte[length];
                if (ReadExact(stream, payload, length) < length)
                {
                    break;
                }

                Message message = null;
                try
                {
                    using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
                    var root = document.RootElement;
                    message = new Message
                    {
                        type = root.TryGetProperty("type", out var type) ? type.GetString() : null,
                        wave = root.TryGetProperty("wave", out var wave) ? wave.GetInt32() : -1,
                        snapshot = root.TryGetProperty("snapshot", out var snapshot) ? snapshot.GetString() : null,
                        lives = root.TryGetProperty("lives", out var lives) ? lives.GetInt32() : -1,
                        loser = root.TryGetProperty("loser", out var loser) ? loser.GetInt32() : -1,
                        eliminated = root.TryGetProperty("eliminated", out var eliminatedValue) ? eliminatedValue.GetInt32() : -1,
                    };
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"net: bad message: {e.Message}");
                }

                if (message == null)
                {
                    continue;
                }

                lock (Lock)
                {
                    switch (message.type)
                    {
                        case "state":
                            _incomingState = message.snapshot;
                            _incomingWave = message.wave;
                            _incomingLives = message.lives;
                            break;
                        case "ready":
                            _incomingReadyWave = message.wave;
                            break;
                        case "go":
                            _incomingGoWave = message.wave;
                            break;
                        case "result":
                            _incomingLoser = message.loser;
                            break;
                        case "eliminated":
                            _incomingEliminated = message.eliminated >= 0 ? message.eliminated : message.wave;
                            break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"net reader failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _connected = false;
            lock (Lock)
            {
                _stream = null;
                _client = null;
            }
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static bool IsPrivateAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        // IPv4 private ranges only: 10/8, 172.16/12, 192.168/16.
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
