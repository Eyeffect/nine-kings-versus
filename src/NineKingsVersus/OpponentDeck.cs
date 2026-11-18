using System;
using System.Runtime.InteropServices;
using Core.Entities;
using Core.Itens;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NineKingsVersus;

/// <summary>
/// Builds runtime Decks from opponent snapshots and installs them as custom
/// decks on enemy kings (KingSo + 0x1F8). The game then spawns those troops
/// through its own CreateEnemies path with full official AI.
/// </summary>
internal static class OpponentDeck
{
    private const int CustomDeckOffset = 0x1F8;

    private static System.Reflection.MethodInfo _createRuntimeCopy;
    private static object _keepAlive;

    public static int LastWave { get; private set; } = -1;
    public static int LastBattlers { get; private set; }
    public static int BotsInstalled { get; private set; }

    public static void Install(Gameplay.GameSnapshot snapshot)
    {
        try
        {
            var deck = BuildDeck(snapshot);
            if (deck == null)
            {
                return;
            }

            var battlers = deck.GetBattlers();
            LastWave = Core.Wave.Data?.Wave ?? -1;
            LastBattlers = battlers?.Length ?? 0;

            var king = Core.Wave.WaveKing;
            if (king?.Scriptable != null)
            {
                InstallToKing(king.Scriptable, deck, "wave king");
            }

            var enemyKings = Core.Wave.GetEnemyKings();
            var installed = 0;
            if (enemyKings != null)
            {
                foreach (var enemyKing in enemyKings)
                {
                    if (enemyKing?.Scriptable == null)
                    {
                        continue;
                    }

                    InstallToKing(enemyKing.Scriptable, deck, "enemy king");
                    installed++;
                }
            }

            BotsInstalled = installed;
            VersusPlugin.Log.LogInfo(
                $"opp-deck: installed 1 deck ({LastBattlers} battlers) on 1 + {installed} enemy kings");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"opp-deck failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// Installs one distinct deck per enemy king, sourced from that bot's run in
    /// the LAN session. This is the multi-bot version of Install().
    /// </summary>
    public static void InstallBots()
    {
        try
        {
            var session = MultiplayerSystems.MultiplayerManager.Session;
            var enemyKings = Core.Wave.GetEnemyKings();
            if (session == null || enemyKings == null)
            {
                VersusPlugin.Log.LogWarning("bots: no session or enemy kings");
                return;
            }

            var wave = Core.Wave.Data?.Wave ?? -1;
            var installed = 0;
            foreach (var enemyKing in enemyKings)
            {
                var scriptable = enemyKing?.Scriptable;
                if (scriptable == null)
                {
                    continue;
                }

                var run = session.GetDataByPlayerKing(enemyKing.Owner);
                var snapshot = run?.snapshot?.GetWaveSnapshot(wave, true) ?? run?.snapshot?.FinalSnapshot;
                if (snapshot == null)
                {
                    VersusPlugin.Log.LogWarning($"bots: no snapshot for king {enemyKing.Owner}");
                    continue;
                }

                var deck = BuildDeck(snapshot);
                if (deck == null)
                {
                    continue;
                }

                InstallToKing(scriptable, deck, enemyKing.Owner.ToString());
                installed++;
            }

            BotsInstalled = installed;
            LastWave = wave;
            VersusPlugin.Log.LogInfo($"bots: installed {installed} distinct bot decks");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"bots failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    private static void InstallToKing(KingSo king, DeckSo deck, string label)
    {
        Marshal.WriteIntPtr(king.Pointer, CustomDeckOffset, deck.Pointer);
        _keepAlive = deck;
    }

    private static DeckSo BuildDeck(Gameplay.GameSnapshot snapshot)
    {
        if (snapshot?.Areas == null)
        {
            VersusPlugin.Log.LogWarning("opp-deck: no snapshot");
            return null;
        }

        var cards = new Il2CppSystem.Collections.Generic.List<CardSo>();
        for (var i = 0; i < snapshot.Areas.Count; i++)
        {
            var area = snapshot.Areas[i];
            if (area == null || area.PlacedEntityType != "Troop" ||
                string.IsNullOrEmpty(area.PlacedCardName))
            {
                continue;
            }

            var troop = Core.Game.GetTroop(area.PlacedCardName);
            if (troop != null)
            {
                cards.Add(troop);
            }
        }

        if (cards.Count == 0)
        {
            VersusPlugin.Log.LogWarning("opp-deck: no cards resolved");
            return null;
        }

        var source = Core.Wave.PlayerDeck;
        if (source == null)
        {
            VersusPlugin.Log.LogWarning("opp-deck: no source deck");
            return null;
        }

        if (_createRuntimeCopy == null)
        {
            _createRuntimeCopy = typeof(DeckSo).GetMethod("CreateRuntimeCopy",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
        }

        if (_createRuntimeCopy == null)
        {
            VersusPlugin.Log.LogWarning("opp-deck: CreateRuntimeCopy not found");
            return null;
        }

        CardSo Slot01 = null, Slot02 = null, Slot03 = null;
        if (source.StartCards_Slot01 != null && source.StartCards_Slot01.Length > 0)
        {
            Slot01 = source.StartCards_Slot01[0];
        }

        if (source.StartCards_Slot02 != null && source.StartCards_Slot02.Length > 0)
        {
            Slot02 = source.StartCards_Slot02[0];
        }

        if (source.StartCards_Slot03 != null && source.StartCards_Slot03.Length > 0)
        {
            Slot03 = source.StartCards_Slot03[0];
        }

        var readOnly = cards.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<CardSo>>();
        var args = new object[] { readOnly, Slot01, Slot02, Slot03 };
        var deck = _createRuntimeCopy.Invoke(_createRuntimeCopy.IsStatic ? null : source, args) as DeckSo;
        if (deck == null)
        {
            VersusPlugin.Log.LogWarning("opp-deck: runtime copy returned null");
        }

        return deck;
    }
}
