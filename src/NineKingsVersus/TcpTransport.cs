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
/// The host accepts every private/loopback peer and broadcasts to all of them;
/// the join connects to exactly one host. Only raw strings cross this boundary.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private const int MaxFrame = 4 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly List<TcpClient> _clients = new();
    private readonly List<Thread> _readers = new();

    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;
    private string _mode = "off";

    /// <summary>Optional diagnostic sink. No game dependency.</summary>
    public Action<string> Log { get; set; }

    public string Mode => _mode;

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
        Register(client);
        Log?.Invoke($"net: joined {peer}:{port}");
    }

    public void Send(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(payload.Length);
        TcpClient[] targets;
        lock (_lock)
        {
            targets = _clients.ToArray();
        }

        foreach (var target in targets)
        {
            try
            {
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
    }

    public bool TryReceive(out string json)
    {
        return _incoming.TryDequeue(out json);
    }

    public void Stop()
    {
        _running = false;
        TcpClient[] clients;
        lock (_lock)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            try { client.Close(); } catch { }
        }

        try { _listener?.Stop(); } catch { }
        _listener = null;

        while (_incoming.TryDequeue(out _))
        {
        }

        _mode = "off";
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

                client.NoDelay = true;
                Register(client);
                Log?.Invoke($"net: peer connected {remote.Address}");
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

    private void Register(TcpClient client)
    {
        lock (_lock)
        {
            _clients.Add(client);
        }

        var thread = new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "versus-read" };
        lock (_lock)
        {
            _readers.Add(thread);
        }
        thread.Start();
    }

    private void ReadLoop(TcpClient client)
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

                _incoming.Enqueue(Encoding.UTF8.GetString(payload));
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
                _clients.Remove(client);
            }
            try { client.Close(); } catch { }
            Log?.Invoke("net: peer disconnected");
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
