using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NineKingsVersus;

/// <summary>
/// One config file for the whole private match. Replaces the pile of empty flag
/// files that used to drive startup.
///
/// Legacy flag files are still honoured as an override so an existing test
/// workflow keeps working, but none of them are required: setting the matching
/// property in config.json is enough to start a match.
/// </summary>
public sealed class VersusConfig
{
    public const string FileName = "config.json";

    public string mode { get; set; } = "off";
    public string peer { get; set; } = "127.0.0.1";
    public int port { get; set; } = 28764;

    // Match-critical switches (previously empty files).
    public bool autoStart { get; set; }
    public bool autoVersus { get; set; }
    public bool multiBot { get; set; }
    public bool arena { get; set; }
    public bool customDeck { get; set; }
    public bool nativeGauntlet { get; set; }
    public bool buildEnemy { get; set; }
    public bool skipIntro { get; set; }
    public bool startBattle { get; set; }
    public bool attachFlow { get; set; } = true;

    [JsonIgnore]
    public string Directory { get; private set; }

    /// <summary>Current process config. Initialised once at plugin load.</summary>
    public static VersusConfig Current { get; private set; }

    public static VersusConfig Load(string directory)
    {
        var config = new VersusConfig { Directory = directory };
        if (string.IsNullOrEmpty(directory))
        {
            return config;
        }

        try
        {
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<VersusConfig>(File.ReadAllText(path));
                if (loaded != null)
                {
                    config.mode = loaded.mode ?? config.mode;
                    config.peer = loaded.peer ?? config.peer;
                    config.port = loaded.port;
                    config.autoStart = loaded.autoStart;
                    config.autoVersus = loaded.autoVersus;
                    config.multiBot = loaded.multiBot;
                    config.arena = loaded.arena;
                    config.customDeck = loaded.customDeck;
                    config.nativeGauntlet = loaded.nativeGauntlet;
                    config.buildEnemy = loaded.buildEnemy;
                    config.skipIntro = loaded.skipIntro;
                    config.startBattle = loaded.startBattle;
                    config.attachFlow = loaded.attachFlow;
                }
            }
        }
        catch (Exception)
        {
            // A broken config must never prevent the game from starting.
        }

        return config;
    }

    public static void Init(string directory)
    {
        Current = Load(directory);
    }

    /// <summary>
    /// True when the switch is on, from config or from the legacy flag file.
    /// Flag files can only turn a switch on; config controls the default.
    /// </summary>
    public bool IsEnabled(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        switch (name)
        {
            case "auto-start": return autoStart;
            case "auto-versus": return autoVersus;
            case "multi-bot": return multiBot;
            case "arena": return arena;
            case "custom-deck": return customDeck;
            case "native-gauntlet": return nativeGauntlet;
            case "build-enemy": return buildEnemy;
            case "skip-intro": return skipIntro;
            case "start-battle": return startBattle;
            case "attach-flow": return attachFlow;
            default: return false;
        }
    }

    /// <summary>Legacy flag override, checked before the config value.</summary>
    public bool HasFlag(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Directory) && File.Exists(Path.Combine(Directory, name)))
        {
            return true;
        }

        return IsEnabled(name);
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(Directory))
        {
            return;
        }

        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(Directory, FileName), JsonSerializer.Serialize(this, options));
        }
        catch (Exception)
        {
        }
    }
}
