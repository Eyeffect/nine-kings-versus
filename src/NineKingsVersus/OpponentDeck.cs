using System;
using System.Runtime.InteropServices;
using Core.Itens;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace NineKingsVersus;

/// <summary>
/// Builds a runtime DeckSo from the opponent snapshot and installs it as the
/// wave enemy king's custom deck (KingSo + 0x1F8). The game then spawns the
/// opponent's real army through its own CreateEnemies path with full AI.
/// </summary>
internal static class OpponentDeck
{
    private const int CustomDeckOffset = 0x1F8;

    private static System.Reflection.MethodInfo _createRuntimeCopy;
    private static object _installedDeck; // keep alive

    public static void Install(Gameplay.GameSnapshot snapshot)
    {
        try
        {
            if (snapshot?.Areas == null)
            {
                VersusPlugin.Log.LogWarning("opp-deck: no snapshot");
                return;
            }

            var cards = new Il2CppSystem.Collections.Generic.List<Core.Itens.CardSo>();
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
                return;
            }

            var source = Core.Wave.PlayerDeck;
            if (source == null)
            {
                VersusPlugin.Log.LogWarning("opp-deck: no source deck");
                return;
            }

            if (_createRuntimeCopy == null)
            {
                _createRuntimeCopy = typeof(Core.Itens.DeckSo).GetMethod("CreateRuntimeCopy",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            }

            if (_createRuntimeCopy == null)
            {
                VersusPlugin.Log.LogWarning("opp-deck: CreateRuntimeCopy not found");
                return;
            }

            Core.Itens.CardSo Slot01 = null, Slot02 = null, Slot03 = null;
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

            var readOnly = cards.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<Core.Itens.CardSo>>();
            var args = new object[] { readOnly, Slot01, Slot02, Slot03 };
            VersusPlugin.Log.LogInfo(
                $"opp-deck: cards={cards.Count} starters={(Slot01 == null ? "null" : "set")}/" +
                $"{(Slot02 == null ? "null" : "set")}/{(Slot03 == null ? "null" : "set")}");
            object copyResult;
            try
            {
                copyResult = _createRuntimeCopy.Invoke(_createRuntimeCopy.IsStatic ? null : source, args);
            }
            catch (Exception e)
            {
                var detail = "";
                foreach (var p in _createRuntimeCopy.GetParameters())
                {
                    detail += $" [{p.ParameterType.Name} {p.Name}]";
                }

                VersusPlugin.Log.LogWarning(
                    $"opp-deck: copy invocation failed static={_createRuntimeCopy.IsStatic}:{detail} " +
                    $"{(e.InnerException ?? e).Message}");
                return;
            }

            var deck = copyResult as Core.Itens.DeckSo;
            if (deck == null)
            {
                VersusPlugin.Log.LogWarning($"opp-deck: runtime copy returned null ({copyResult?.GetType().Name ?? "null"})");
                return;
            }

            _installedDeck = deck;

            var king = Core.Wave.WaveKing;
            var scriptable = king?.Scriptable;
            if (scriptable == null)
            {
                VersusPlugin.Log.LogWarning("opp-deck: no wave king scriptable");
                return;
            }

            Marshal.WriteIntPtr(scriptable.Pointer, CustomDeckOffset, deck.Pointer);
            var battlers = deck.GetBattlers();

            var enemyKings = Core.Wave.GetEnemyKings();
            var installedOn = 0;
            if (enemyKings != null)
            {
                foreach (var enemyKing in enemyKings)
                {
                    var enemyScriptable = enemyKing?.Scriptable;
                    if (enemyScriptable == null)
                    {
                        continue;
                    }

                    Marshal.WriteIntPtr(enemyScriptable.Pointer, CustomDeckOffset, deck.Pointer);
                    installedOn++;
                }
            }

            VersusPlugin.Log.LogInfo(
                $"opp-deck: installed 8 cards on king {scriptable.name} + {installedOn} enemy kings; " +
                $"battlers={battlers?.Length ?? -1}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"opp-deck failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }
}
