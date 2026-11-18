using System;
using System.Linq;
using UnityEngine;

namespace NineKingsVersus;

/// <summary>
/// Clean arena spawner: builds the opponent army with Troop's own internal
/// factory and RegisterOnWaveLists, so spawned troops join the wave's faction
/// lists and target each other through the game's normal combat AI.
/// No async-gauntlet / MultiplayerSession code is involved.
/// </summary>
internal static class CleanArena
{
    /// <summary>
    /// Replaces the local wave's troop types with the opponent's troops. The game's
    /// own SpawnWave then creates them as proper combat enemies with working AI.
    /// </summary>
    public static void InstallWaveTroops(Gameplay.GameSnapshot snapshot)
    {
        try
        {
            var types = new System.Collections.Generic.List<Core.Itens.TroopSo>();
            if (snapshot?.Areas != null)
            {
                foreach (var a in snapshot.Areas)
                {
                    if (a == null || string.IsNullOrEmpty(a.PlacedCardName) || a.PlacedEntityType != "Troop")
                    {
                        continue;
                    }

                    var type = Core.Game.GetTroop(a.PlacedCardName);
                    if (type != null)
                    {
                        types.Add(type);
                    }
                }
            }

            if (types.Count == 0)
            {
                VersusPlugin.Log.LogWarning("clean-arena: no opponent troops to install");
                return;
            }

            var array = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Core.Itens.TroopSo>(types.Count);
            for (var i = 0; i < types.Count; i++)
            {
                array[i] = types[i];
            }

            var property = typeof(Core.Wave).GetProperty("WaveTroops",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            property?.SetValue(null, array);
            VersusPlugin.Log.LogInfo($"clean-arena: installed {types.Count} opponent troop types into WaveTroops");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"clean-arena: install failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static System.Reflection.MethodInfo _factory;
    private static System.Collections.Generic.List<Core.Entities.Troop> _army =
        new System.Collections.Generic.List<Core.Entities.Troop>();

    public static int LastSpawned => _army.Count;
    public static int LastWave { get; private set; } = -1;

    public static void Clear()
    {
        foreach (var troop in _army)
        {
            if (troop != null)
            {
                UnityEngine.Object.Destroy(troop.gameObject);
            }
        }

        _army.Clear();
    }

    public static int SpawnArmy(Gameplay.GameSnapshot snapshot, int wave)
    {
        try
        {
            Clear();
            LastWave = wave;

            var despawn = typeof(Core.Wave).GetMethod("DespawnWaveEnemies",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            if (despawn != null)
            {
                if (despawn.IsStatic)
                {
                    despawn.Invoke(null, null);
                }
                else
                {
                    despawn.Invoke(Core.Wave.Instance, null);
                }
            }

            if (_factory == null)
            {
                _factory = typeof(Core.Entities.Troop).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Contains("Troop_TroopSo_KingdomArea_Boolean_Boolean"));
            }

            if (_factory == null)
            {
                VersusPlugin.Log.LogWarning("clean-arena: troop factory not found");
                return 0;
            }

            var area = FindArea();
            if (area == null)
            {
                VersusPlugin.Log.LogWarning("clean-arena: no KingdomArea found");
                return 0;
            }

            var types = new System.Collections.Generic.List<Core.Itens.TroopSo>();
            if (snapshot?.Areas != null)
            {
                foreach (var a in snapshot.Areas)
                {
                    if (a == null || string.IsNullOrEmpty(a.PlacedCardName) || a.PlacedEntityType != "Troop")
                    {
                        continue;
                    }

                    var type = Core.Game.GetTroop(a.PlacedCardName);
                    if (type == null)
                    {
                        continue;
                    }

                    var count = Math.Max(1, Math.Min(6, a.TroopQuantity / 9));
                    for (var i = 0; i < count; i++)
                    {
                        types.Add(type);
                    }
                }
            }

            var origin = FormationPosition();
            for (var i = 0; i < types.Count; i++)
            {
                Core.Entities.Troop troop;
                try
                {
                    var args = new object[] { types[i], area, true, false };
                    troop = (Core.Entities.Troop)(_factory.IsStatic
                        ? _factory.Invoke(null, args)
                        : _factory.Invoke(null, args));
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"clean-arena: factory failed: {(e.InnerException ?? e).Message}");
                    continue;
                }

                if (troop == null)
                {
                    continue;
                }

                try
                {
                    troop.transform.position = new Vector3(origin.x - (i % 8) * 1.6f, origin.y - (i / 8) * 1.6f, 0f);
                    troop.IsEnemy = true;
                    troop.Wave_Begin();
                    troop.SetReady();
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"clean-arena: setup failed: {e.GetType().Name}: {e.Message}");
                }

                _army.Add(troop);
            }

            VersusPlugin.Log.LogInfo(
                $"clean-arena: wave={wave} spawned={_army.Count}/{types.Count} area={area.name} at {origin}");
            return _army.Count;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"clean-arena failed: {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    private static Core.Kingdoms.KingdomArea FindArea()
    {
        try
        {
            var melee = Core.Wave.PlayerMeleeTroops;
            if (melee != null && melee.Troops != null && melee.Troops.Count > 0)
            {
                var troop = melee.Troops[0];
                var field = typeof(Core.Entities.Entity).GetField("m_Area",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var value = field?.GetValue(troop) as Core.Kingdoms.KingdomArea;
                if (value != null)
                {
                    return value;
                }
            }
        }
        catch
        {
        }

        var areas = UnityEngine.Object.FindObjectsOfType<Core.Kingdoms.KingdomArea>();
        return areas != null && areas.Length > 0 ? areas[0] : null;
    }

    private static Vector2 FormationPosition()
    {
        var formationField = typeof(Core.Wave).GetField("_rightMultiplayerEnemyFormation",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
        var transform = formationField?.GetValue(formationField.IsStatic ? null : Core.Wave.Instance) as Transform;
        if (transform != null)
        {
            return transform.position;
        }

        var baseGo = Core.Wave.MultiplayerEnemyBase;
        return baseGo != null ? baseGo.transform.position : Vector2.zero;
    }
}
