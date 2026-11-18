using System;
using System.IO;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NineKingsVersus;

/// <summary>
/// Native UGUI panel (built from the game's own UI components) for driving a
/// private 1v1 match. Open with Ctrl+Alt+F11.
/// </summary>
internal static class VersusNativeMenu
{
    private const float PanelW = 360f;
    private const float PanelH = 560f;

    private static GameObject _root;
    private static TMP_Text _status;
    private static TMP_Text _network;
    private static TMP_Text _options;
    private static TMP_FontAsset _font;

    private static string _mode = "host";
    private static string _peer = "127.0.0.1";
    private static int _port = 28764;
    private static string _message = "";

    public static bool Visible => _root != null && _root.activeSelf;

    /// <summary>
    /// Clones the main menu's Start button into a native VERSUS button so the
    /// test menu is reachable from the game's own menu instead of a hotkey.
    /// </summary>
    public static void TryInstallMainMenuButton()
    {
        try
        {
            if (GameObject.Find("VersusBtn") != null)
            {
                return;
            }

            var start = GameObject.Find("MainMenuUI/Holder/Main/MainButtonHolder/StartBtn");
            if (start == null || start.transform.parent == null)
            {
                return;
            }

            var clone = UnityEngine.Object.Instantiate(start, start.transform.parent);
            clone.name = "VersusBtn";
            VersusPlugin.Log.LogInfo("menu-button: cloned start button");

            var tmp = clone.GetComponentInChildren<TMPro.TMP_Text>();
            if (tmp != null)
            {
                tmp.text = "VERSUS";
            }
            else
            {
                var legacy = clone.GetComponentInChildren<Text>();
                if (legacy != null)
                {
                    legacy.text = "VERSUS";
                }
            }

            var button = clone.GetComponent<Button>();
            if (button == null)
            {
                VersusPlugin.Log.LogWarning("menu-button: cloned button missing");
                return;
            }

            button.onClick.RemoveAllListeners();
            VersusPlugin.Log.LogInfo("menu-button: listeners cleared");
            button.onClick.AddListener(
                DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(() => Toggle()));
            VersusPlugin.Log.LogInfo("menu-button: listener added");

            VersusPlugin.Log.LogInfo("native VERSUS button installed in main menu");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"main menu button failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public static void Toggle()
    {
        if (_root == null)
        {
            Build();
        }
        else
        {
            _root.SetActive(!_root.activeSelf);
        }
    }

    public static void Refresh()
    {
        if (!Visible)
        {
            return;
        }

        _network.text = $"Mode: {_mode.ToUpperInvariant()}    Peer: {_peer}:{_port}\n" +
                        $"Net: {VersusNet.Mode} connected={VersusNet.Connected}";
        _options.text =
            $"Arena: {OnOff("arena")}    Auto-start: {OnOff("auto-versus")}\n" +
            $"Single-wave: {OnOff("single-load")}    Board: {OnOff("build-enemy")}\n" +
            $"Run-on-start: {OnOff("attach-flow")}";
        var data = Core.Wave.Data;
        _status.text = $"Wave: {(data == null ? "no run" : data.Wave.ToString())} " +
                       $"running={(data == null ? "-" : data.Running.ToString())}\n" +
                       $"Lives: local {VersusNet.LocalLives} / peer {VersusNet.PeerLives}  states={VersusNet.StateVersion}\n" +
                       _message;
    }

    private static string OnOff(string flag)
    {
        return File.Exists(Path.Combine(VersusPlugin.DataDir, flag)) ? "ON" : "OFF";
    }

    private static void Build()
    {
        try
        {
            var existingText = UnityEngine.Object.FindObjectOfType<TMP_Text>();
            _font = existingText != null ? existingText.font : null;

            var canvasGo = new GameObject("VersusNativeMenu");
            canvasGo.AddComponent<RectTransform>();
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);
            canvasGo.SetActive(false);
            _root = canvasGo;

            var panelGo = new GameObject("Panel");
            panelGo.AddComponent<RectTransform>();
            panelGo.transform.SetParent(canvasGo.transform, false);
            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0.5f);
            panelRect.anchorMax = new Vector2(0f, 0.5f);
            panelRect.pivot = new Vector2(0f, 0.5f);
            panelRect.anchoredPosition = new Vector2(16f, 0f);
            panelRect.sizeDelta = new Vector2(PanelW, PanelH);
            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0.05f, 0.06f, 0.1f, 0.94f);

            var y = -36f;
            AddText(panelGo.transform, "9 KINGS VERSUS", 24, FontStyle.Bold,
                new Vector2(14f, y), new Vector2(PanelW - 28f, 30f));
            y -= 36f;
            _network = AddText(panelGo.transform, "", 16, FontStyle.Normal,
                new Vector2(14f, y), new Vector2(PanelW - 28f, 46f));
            y -= 52f;

            AddButton(panelGo.transform, "HOST", new Vector2(14f, y), new Vector2(150f, 32f), () =>
            {
                _mode = "host";
                ApplyNetwork();
            });
            AddButton(panelGo.transform, "JOIN", new Vector2(176f, y), new Vector2(150f, 32f), () =>
            {
                _mode = "join";
                ApplyNetwork();
            });
            y -= 36f;

            AddButton(panelGo.transform, "IP -", new Vector2(14f, y), new Vector2(72f, 30f), () => ShiftIp(-1));
            AddButton(panelGo.transform, "IP +", new Vector2(96f, y), new Vector2(72f, 30f), () => ShiftIp(1));
            AddButton(panelGo.transform, "LOCALHOST", new Vector2(176f, y), new Vector2(150f, 30f), () =>
            {
                _peer = "127.0.0.1";
                ApplyNetwork();
            });
            y -= 36f;

            AddButton(panelGo.transform, "ARENA", new Vector2(14f, y), new Vector2(96f, 30f), () => ToggleFlag("arena"));
            AddButton(panelGo.transform, "AUTO", new Vector2(128f, y), new Vector2(96f, 30f), () => ToggleFlag("auto-versus"));
            AddButton(panelGo.transform, "SINGLE", new Vector2(240f, y), new Vector2(86f, 30f), () => ToggleFlag("single-load"));
            y -= 34f;

            AddButton(panelGo.transform, "BOARD", new Vector2(14f, y), new Vector2(96f, 30f), () => ToggleFlag("build-enemy"));
            AddButton(panelGo.transform, "LOAD RUN", new Vector2(128f, y), new Vector2(96f, 30f), () => ToggleFlag("attach-flow"));
            y -= 38f;
            _options = AddText(panelGo.transform, "", 14, FontStyle.Normal,
                new Vector2(14f, y), new Vector2(PanelW - 28f, 52f));
            y -= 58f;

            AddButton(panelGo.transform, "START BATTLE", new Vector2(14f, y), new Vector2(310f, 34f), () =>
            {
                _message = VersusBehaviour.MenuStartBattle();
            });
            y -= 38f;
            AddButton(panelGo.transform, "REBUILD BOARD", new Vector2(14f, y), new Vector2(310f, 34f), () =>
            {
                _message = VersusBehaviour.MenuBuildBoard();
            });
            y -= 38f;
            AddButton(panelGo.transform, "CLOSE (Ctrl+Alt+F11)", new Vector2(14f, y), new Vector2(310f, 34f), () =>
            {
                _root.SetActive(false);
            });
            y -= 42f;
            _status = AddText(panelGo.transform, "", 14, FontStyle.Normal,
                new Vector2(14f, y), new Vector2(PanelW - 28f, 80f));

            ReadNetwork();
            canvasGo.SetActive(true);
            Refresh();
            VersusPlugin.Log.LogInfo("native versus menu built");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"native menu build failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static TMP_Text AddText(Transform parent, string text, int size, FontStyle style, Vector2 pos, Vector2 sizeDelta)
    {
        var go = new GameObject("Text");
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = pos;
        rect.sizeDelta = sizeDelta;
        var label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        if (_font != null)
        {
            label.font = _font;
        }

        label.fontSize = size;
        label.fontStyle = style == FontStyle.Bold ? FontStyles.Bold : FontStyles.Normal;
        label.color = new Color(0.88f, 0.9f, 0.95f, 1f);
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        return label;
    }

    private static void AddButton(Transform parent, string caption, Vector2 pos, Vector2 sizeDelta, Action action)
    {
        var go = new GameObject("Button");
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = pos;
        rect.sizeDelta = sizeDelta;
        var image = go.AddComponent<Image>();
        image.color = new Color(0.16f, 0.19f, 0.26f, 1f);
        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(
            DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(() => action())));

        var label = AddText(go.transform, caption, 17, FontStyle.Bold, Vector2.zero, sizeDelta);
        label.alignment = TextAlignmentOptions.Center;
        label.color = new Color(0.95f, 0.95f, 1f, 1f);
    }

    private static void ToggleFlag(string flag)
    {
        var path = Path.Combine(VersusPlugin.DataDir, flag);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, "1");
        }

        _message = $"{flag} toggled";
    }

    private static void ShiftIp(int delta)
    {
        var parts = _peer.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[3], out var last))
        {
            last = 1;
        }

        parts[3] = Math.Max(1, Math.Min(254, last + delta)).ToString();
        _peer = string.Join(".", parts);
        ApplyNetwork();
    }

    private static void ApplyNetwork()
    {
        var config = $"{{\"mode\":\"{_mode}\",\"peer\":\"{_peer}\",\"port\":{_port}}}";
        File.WriteAllText(Path.Combine(VersusPlugin.DataDir, "net.json"), config);
        _message = "Network config saved (restart to reconnect)";
        Refresh();
    }

    private static void ReadNetwork()
    {
        try
        {
            var config = File.ReadAllText(Path.Combine(VersusPlugin.DataDir, "net.json"));
            _mode = config.Contains("\"join\"") ? "join" : "host";
            var peerMatch = System.Text.RegularExpressions.Regex.Match(config, "\"peer\"\\s*:\\s*\"([^\"]+)\"");
            if (peerMatch.Success)
            {
                _peer = peerMatch.Groups[1].Value;
            }

            var portMatch = System.Text.RegularExpressions.Regex.Match(config, "\"port\"\\s*:\\s*(\\d+)");
            if (portMatch.Success)
            {
                _port = int.Parse(portMatch.Groups[1].Value);
            }
        }
        catch
        {
            // Use defaults.
        }
    }
}
