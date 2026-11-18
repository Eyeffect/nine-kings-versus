using System;
using System.IO;
using UnityEngine;

namespace NineKingsVersus;

/// <summary>
/// Small native IMGUI menu drawn from Core.Game.OnGUI, opened with Ctrl+Alt+F11.
/// It writes the same net.json / flag files the probe code already reads, so it
/// can be used to run a match without editing files by hand.
/// </summary>
internal static class VersusMenu
{
    private const float Width = 360f;
    private const float Height = 560f;

    public static bool Visible;

    private static string _mode = "host";
    private static string _peer = "127.0.0.1";
    private static string _port = "28764";
    private static string _status = "";
    private static bool _arena = true;
    private static bool _autoVersus = true;
    private static bool _singleLoad = true;
    private static bool _attachFlow = true;
    private static bool _buildEnemy;

    public static void Toggle()
    {
        Visible = !Visible;
        if (!Visible)
        {
            return;
        }

        _mode = ReadMode();
        _arena = HasFlag("arena");
        _autoVersus = HasFlag("auto-versus");
        _singleLoad = HasFlag("single-load");
        _attachFlow = HasFlag("attach-flow");
        _buildEnemy = HasFlag("build-enemy");
    }

    public static void Draw()
    {
        if (!Visible)
        {
            return;
        }

        var x = 12f;
        var y = 12f;
        var px = x + 12f;
        var py = y + 34f;
        GUI.Box(new Rect(x, y, Width, Height), "9 Kings Versus - test menu");

        GUI.Label(new Rect(px, py, Width - 24f, 18f), "LAN mode (restart to apply)");
        py += 20f;
        _mode = GUI.Toggle(new Rect(px, py, 80f, 20f), _mode == "host", "Host") ? "host" : "join";
        GUI.Toggle(new Rect(px + 90f, py, 80f, 20f), _mode == "join", "Join");
        py += 22f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), "Peer IP");
        py += 20f;
        _peer = GUI.TextField(new Rect(px, py, Width - 24f, 22f), _peer);
        py += 26f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), "Port");
        py += 20f;
        _port = GUI.TextField(new Rect(px, py, Width - 24f, 22f), _port);
        py += 26f;
        if (GUI.Button(new Rect(px, py, Width - 24f, 24f), "Apply network config"))
        {
            ApplyNetwork();
        }

        py += 30f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), "Match options");
        py += 20f;
        _arena = GUI.Toggle(new Rect(px, py, Width - 24f, 20f), _arena, "Arena: spawn opponent army");
        py += 22f;
        _autoVersus = GUI.Toggle(new Rect(px, py, Width - 24f, 20f), _autoVersus, "Auto start same wave");
        py += 22f;
        _singleLoad = GUI.Toggle(new Rect(px, py, Width - 24f, 20f), _singleLoad, "Single-player wave logic");
        py += 22f;
        _attachFlow = GUI.Toggle(new Rect(px, py, Width - 24f, 20f), _attachFlow, "Load run on startup");
        py += 22f;
        _buildEnemy = GUI.Toggle(new Rect(px, py, Width - 24f, 20f), _buildEnemy, "Build board with cards");
        py += 24f;
        if (GUI.Button(new Rect(px, py, Width - 24f, 24f), "Apply options"))
        {
            ApplyOptions();
        }

        py += 30f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), "Status");
        py += 18f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), $"Net: {VersusNet.Mode} connected={VersusNet.Connected}");
        py += 18f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), $"Peer lives={VersusNet.PeerLives} local={VersusNet.LocalLives} states={VersusNet.StateVersion}");
        py += 18f;
        var wave = Core.Wave.Data;
        GUI.Label(new Rect(px, py, Width - 24f, 18f),
            $"Wave: {(wave == null ? "no run" : wave.Wave.ToString())} running={(wave == null ? "-" : wave.Running.ToString())}");
        py += 18f;
        GUI.Label(new Rect(px, py, Width - 24f, 18f), _status ?? "");
        py += 22f;
        if (GUI.Button(new Rect(px, py, Width - 24f, 24f), "Start battle now"))
        {
            _status = VersusBehaviour.MenuStartBattle();
        }

        py += 28f;
        if (GUI.Button(new Rect(px, py, Width - 24f, 24f), "Rebuild opponent board"))
        {
            _status = VersusBehaviour.MenuBuildBoard();
        }

        py += 28f;
        if (GUI.Button(new Rect(px, py, Width - 24f, 24f), "Close (Ctrl+Alt+F11)"))
        {
            Visible = false;
        }
    }

    private static void ApplyNetwork()
    {
        try
        {
            if (!int.TryParse(_port, out var port) || port < 1 || port > 65535)
            {
                _status = "Invalid port";
                return;
            }

            var config = $"{{\"mode\":\"{_mode}\",\"peer\":\"{_peer}\",\"port\":{port}}}";
            File.WriteAllText(Path.Combine(VersusPlugin.DataDir, "net.json"), config);
            _status = $"Network config written ({_mode}, {_peer}:{port}). Restart the game to reconnect.";
            VersusPlugin.Log.LogInfo($"menu: {_status}");
        }
        catch (Exception e)
        {
            _status = $"Failed: {e.Message}";
        }
    }

    private static void ApplyOptions()
    {
        SetFlag("arena", _arena);
        SetFlag("auto-versus", _autoVersus);
        SetFlag("single-load", _singleLoad);
        SetFlag("attach-flow", _attachFlow);
        SetFlag("build-enemy", _buildEnemy);
        _status = "Options written. Load options need a restart.";
        VersusPlugin.Log.LogInfo($"menu: {_status}");
    }

    private static bool HasFlag(string name)
    {
        return File.Exists(Path.Combine(VersusPlugin.DataDir, name));
    }

    private static void SetFlag(string name, bool enabled)
    {
        var path = Path.Combine(VersusPlugin.DataDir, name);
        if (enabled)
        {
            File.WriteAllText(path, "1");
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ReadMode()
    {
        try
        {
            var config = File.ReadAllText(Path.Combine(VersusPlugin.DataDir, "net.json"));
            return config.Contains("\"join\"") ? "join" : "host";
        }
        catch
        {
            return "host";
        }
    }
}
