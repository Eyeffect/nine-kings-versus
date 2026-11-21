using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NineKingsVersus;

/// <summary>
/// LAN-only TCP transport. Frames are 4-byte little-endian length + UTF-8 JSON.
/// The host accepts up to eight private/loopback peers, assigns each a player
/// id and relays their frames to the others; the join connects to one host.
/// Only raw strings cross this boundary.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private const int MaxFrame = 4 * 1024 * 1024;
    private const int MaxPeers = 8;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly Dictionary<int, TcpClient> _clients = new();
    private readonly List<Thread> _readers = new();

    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;
    private string _mode = "off";
    private int _localPlayerId;
    private int _nextPlayerId = 1;

    /// <summary>Optional diagnostic sink. No game dependency.</summary>
    public Action<string> Log { get; set; }

    public event Action<int> PeerLost;

    public string Mode => _mode;

    public int LocalPlayerId => _localPlayerId;

    public IReadOnlyList<int> Peers
    {
        get
        {
            lock (_lock)
            {
                if (_mode == "join")
                {
                    return _clients.Count > 0 ? new[] { 0 } : Array.Empty<int>();
                }

                var ids = new int[_clients.Count];
                _clients.Keys.CopyTo(ids, 0);
                Array.Sort(ids);
                return ids;
            }
        }
    }

    public bool Connected
    {
        get
        {
            lock (_lock)
            {
                return _clients.Count > 0;
            }
        }
    }

    public void StartHost(int port)
    {
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Stop();
        _mode = "host";
        _localPlayerId = 0;
        _nextPlayerId = 1;
        _running = true;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Log?.Invoke($"net: host listening on {port}");

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "versus-accept" };
        _acceptThread.Start();
    }

    public void StartJoin(string peer, int port)
    {
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (!IsPrivateAddress(peer))
        {
            throw new ArgumentException($"Refusing non-private peer address '{peer}'", nameof(peer));
        }

        Stop();
        _mode = "join";
        _localPlayerId = -1;
        _running = true;

        var client = new TcpClient();
        var result = client.BeginConnect(IPAddress.Parse(peer), port, null, null);
        if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)) || !client.Connected)
        {
            client.Close();
            _running = false;
            _mode = "off";
            Log?.Invoke("net: join timed out");
            return;
        }

        client.NoDelay = true;
        Register(0, client);
        Log?.Invoke($"net: joined {peer}:{port}; waiting for player id");
    }

    public void Send(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        TcpClient[] targets;
        lock (_lock)
        {
            targets = new TcpClient[_clients.Count];
            _clients.Values.CopyTo(targets, 0);
        }

        foreach (var target in targets)
        {
            Write(target, json);
        }
    }

    public void SendTo(int playerId, string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        TcpClient target;
        lock (_lock)
        {
            if (!_clients.TryGetValue(playerId, out target))
            {
                return;
            }
        }

        Write(target, json);
    }

    public void SendExcept(int playerId, string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        List<TcpClient> targets = null;
        lock (_lock)
        {
            foreach (var pair in _clients)
            {
                if (pair.Key == playerId)
                {
                    continue;
                }

                (targets ??= new List<TcpClient>()).Add(pair.Value);
            }
        }

        if (targets == null)
        {
            return;
        }

        foreach (var target in targets)
        {
            Write(target, json);
        }
    }

    public bool TryReceive(out string json)
    {
        return _incoming.TryDequeue(out json);
    }

    public void Stop()
    {
        _running = false;
        KeyValuePair<int, TcpClient>[] clients;
        lock (_lock)
        {
            clients = new KeyValuePair<int, TcpClient>[_clients.Count];
            ((ICollection<KeyValuePair<int, TcpClient>>)_clients).CopyTo(clients, 0);
            _clients.Clear();
        }

        foreach (var pair in clients)
        {
            try { pair.Value.Close(); } catch { }
        }

        try { _listener?.Stop(); } catch { }
        _listener = null;

        while (_incoming.TryDequeue(out _))
        {
        }

        _mode = "off";
        _localPlayerId = 0;
        _nextPlayerId = 1;
    }

    public void Dispose()
    {
        Stop();
    }

    private void AcceptLoop()
    {
        try
        {
            while (_running && _listener != null)
            {
                var client = _listener.AcceptTcpClient();
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote == null || !IsPrivateAddress(remote.Address.ToString()))
                {
                    Log?.Invoke($"net: refused non-private peer {remote?.Address}");
                    client.Close();
                    continue;
                }

                int id;
                lock (_lock)
                {
                    if (_clients.Count >= MaxPeers)
                    {
                        Log?.Invoke("net: match full (8 peers)");
                        client.Close();
                        continue;
                    }

                    id = _nextPlayerId++;
                }

                client.NoDelay = true;
                Register(id, client);
                // The join cannot know its id until the host tells it. This control
                // frame is consumed by the transport reader, never by VersusNet.
                Write(client, $"{{\"t\":\"welcome\",\"playerId\":{id}}}");
                Log?.Invoke($"net: peer {id} connected from {remote.Address}");
            }
        }
        catch (SocketException)
        {
            // Listener stopped.
        }
        catch (Exception e)
        {
            Log?.Invoke($"net accept failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void Register(int playerId, TcpClient client)
    {
        lock (_lock)
        {
            _clients[playerId] = client;
        }

        var thread = new Thread(() => ReadLoop(playerId, client)) { IsBackground = true, Name = "versus-read" };
        lock (_lock)
        {
            _readers.Add(thread);
        }
        thread.Start();
    }

    private void ReadLoop(int playerId, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var header = new byte[4];
            while (_running)
            {
                if (ReadExact(stream, header, 4) < 4)
                {
                    break;
                }

                var length = BitConverter.ToInt32(header, 0);
                if (length < 2 || length > MaxFrame)
                {
                    Log?.Invoke($"net: rejecting frame length {length}");
                    break;
                }

                var payload = new byte[length];
                if (ReadExact(stream, payload, length) < length)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(payload);
                if (_mode == "join" && TryReadWelcome(json))
                {
                    continue;
                }

                _incoming.Enqueue(json);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"net reader failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(playerId);
            }
            try { client.Close(); } catch { }
            Log?.Invoke($"net: peer {playerId} disconnected");
            try { PeerLost?.Invoke(playerId); } catch { }
        }
    }

    private bool TryReadWelcome(string json)
    {
        if (json == null || json.IndexOf("\"welcome\"", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("t", out var kind) && kind.GetString() == "welcome" &&
                root.TryGetProperty("playerId", out var id))
            {
                _localPlayerId = id.GetInt32();
                Log?.Invoke($"net: assigned player id {_localPlayerId}");
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void Write(TcpClient target, string json)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var header = BitConverter.GetBytes(payload.Length);
            var stream = target.GetStream();
            lock (stream)
            {
                stream.Write(header, 0, header.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"net send failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static int ReadExact(NetworkStream stream, byte[] buffer, int count)
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

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
