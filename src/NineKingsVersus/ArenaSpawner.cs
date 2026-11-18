using System;
using System.Linq;
using Il2CppSystem.Collections.Generic;
using UnityEngine;

namespace NineKingsVersus;

/// <summary>
/// Custom 1v1 arena spawner. The shipped async gauntlet never constructs an
/// enemy base offline, so instead of reusing it we resolve the opponent's troop
/// cards from their snapshot and spawn that army through the game's own
/// Wave.CreateEnemies path, then mark the new entities as enemies.
/// </summary>
internal static class ArenaSpawner
{
    private static System.Reflection.MethodInfo _createEnemies;
    private static bool _spawnFailureLogged;

    /// <summary>Number of opponent entities spawned for the current wave.</summary>
    public static int LastSpawned { get; private set; }

    public static int LastSpawnWave { get; private set; } = -1;

    /// <summary>
    /// Prevents the local wave generator from spawning its usual bot army, so
    /// the only enemies in the arena are the opponent's troops.
    /// </summary>
    public static void SuppressLocalWaveEnemies()
    {
        try
        {
            var property = typeof(Core.Wave).GetProperty("WaveTroops",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                VersusPlugin.Log.LogWarning("arena: WaveTroops not writable");
                return;
            }

            var empty = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Core.Itens.TroopSo>(0);
            property.SetValue(null, empty);
            VersusPlugin.Log.LogInfo("arena: local wave troops cleared");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"arena: failed to clear wave troops: {e.Message}");
        }
    }

    /// <summary>
    /// Spawns the opponent army from a snapshot. Typically called right after
    /// the wave's own SpawnWave, so normal enemies are already suppressed.
    /// </summary>
    public static int SpawnOpponentArmy(Gameplay.GameSnapshot snapshot)
    {
        try
        {
            var wave = Core.Wave.Instance;
            if (wave == null || snapshot?.Areas == null)
            {
                VersusPlugin.Log.LogWarning("arena: missing wave or snapshot");
                return 0;
            }

            if (_createEnemies == null)
            {
                _createEnemies = typeof(Core.Wave).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "CreateEnemies");
            }

            if (_createEnemies == null)
            {
                VersusPlugin.Log.LogWarning("arena: CreateEnemies not found");
                return 0;
            }

            var types = new List<Core.Itens.TroopSo>();
            foreach (var area in snapshot.Areas)
            {
                if (area == null || string.IsNullOrEmpty(area.PlacedCardName) || area.PlacedEntityType != "Troop")
                {
                    continue;
                }

                var troop = Core.Game.GetTroop(area.PlacedCardName);
                if (troop == null)
                {
                    VersusPlugin.Log.LogWarning($"arena: unresolved troop '{area.PlacedCardName}'");
                    continue;
                }

                // One entity per 9 stacked troops, capped so a single area cannot
                // flood the arena.
                var count = Math.Max(1, Math.Min(6, area.TroopQuantity / 9));
                for (var i = 0; i < count; i++)
                {
                    types.Add(troop);
                }
            }

            if (types.Count == 0)
            {
                VersusPlugin.Log.LogWarning("arena: snapshot has no resolvable troops");
                return 0;
            }

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
                    despawn.Invoke(wave, null);
                }

                VersusPlugin.Log.LogInfo("arena: normal wave enemies despawned");
            }

            var before = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
            var beforePointers = new System.Collections.Generic.HashSet<IntPtr>();
            if (before != null)
            {
                foreach (var entity in before)
                {
                    if (entity != null)
                    {
                        beforePointers.Add(entity.Pointer);
                    }
                }
            }

            var baseGo = Core.Wave.MultiplayerEnemyBase;
            var formation = typeof(Core.Wave).GetField("_rightMultiplayerEnemyFormation",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            var formationTransform = formation?.GetValue(formation.IsStatic ? null : wave) as UnityEngine.Transform;
            var origin = formationTransform != null ? formationTransform.position : baseGo?.transform.position ?? Vector3.zero;
            var position = new Vector2(origin.x, origin.y);

            var battleSpawn = typeof(Core.Entities.Troop).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SpawnInBattle");

            if (battleSpawn != null)
            {
                var template = (Core.Entities.Troop)null;
                var waveMelee = Core.Wave.PlayerMeleeTroops;
                if (waveMelee != null && waveMelee.Troops != null && waveMelee.Troops.Count > 0)
                {
                    template = waveMelee.Troops[0];
                }

                if (template == null)
                {
                    var troops = UnityEngine.Object.FindObjectsOfType<Core.Entities.Troop>();
                    if (troops != null && troops.Length > 0)
                    {
                        template = troops[0];
                    }
                }

                if (template == null)
                {
                    VersusPlugin.Log.LogWarning("arena: no template troop for SpawnInBattle");
                }
                else
                {
                var areas = UnityEngine.Object.FindObjectsOfType<Core.Kingdoms.KingdomArea>();
                var area = areas != null && areas.Length > 0 ? areas[0] : null;
                var battlers = new List<Core.Entities.Troop>();
                var attempts = types.Count * 5;
                for (var i = 0; i < attempts; i++)
                {
                    var troopType = types[i % types.Count];
                    var args = new object[]
                    {
                        troopType != null ? troopType.name : "Troop",
                        3,
                        area,
                        true,
                        false,
                        false,
                        null,
                    };

                    Core.Entities.Troop troop;
                    try
                    {
                        troop = (Core.Entities.Troop)(battleSpawn.IsStatic
                            ? battleSpawn.Invoke(null, args)
                            : battleSpawn.Invoke(template, args));
                    }
                    catch (Exception e)
                    {
                        if (!_spawnFailureLogged)
                        {
                            _spawnFailureLogged = true;
                            VersusPlugin.Log.LogWarning(
                                $"arena: SpawnInBattle failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
                        }

                        continue;
                    }

                    if (troop != null)
                    {
                        battlers.Add(troop);
                    }
                }

                var enemiesList = Core.Wave.Enemies;
                var melee = Core.Wave.EnemyMeleeTroops;
                if (enemiesList != null && melee != null)
                {
                    for (var j = 0; j < battlers.Count; j++)
                    {
                        var troop = battlers[j];
                        if (troop == null)
                        {
                            continue;
                        }

                        try
                        {
                            troop.IsEnemy = true;
                        }
                        catch
                        {
                        }

                        enemiesList.Add(troop);
                        melee.Add(troop);

                        try
                        {
                            Core.Wave.RegisterTargetTroop(troop);
                        }
                        catch (Exception e)
                        {
                            VersusPlugin.Log.LogWarning($"arena: RegisterTargetTroop failed: {e.Message}");
                        }
                    }
                }

                VersusPlugin.Log.LogInfo(
                    $"arena: SpawnInBattle battlers={battlers.Count} area={(area == null ? "null" : "set")} " +
                    $"enemiesList={(enemiesList == null ? "null" : enemiesList.Count.ToString())}");

                LastSpawned = battlers.Count;
                LastSpawnWave = Core.Wave.Data?.Wave ?? -1;
                var afterSpawn = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
                VersusPlugin.Log.LogInfo($"arena: totalEntities={afterSpawn?.Length ?? 0}");
                if (battlers.Count > 0)
                {
                    return battlers.Count;
                }
                }
            }

            var spawn = typeof(Core.Wave).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SpawnBattlers");
            if (spawn != null)
            {
                var battlers = new List<Core.Entities.Troop>();
                var attempts = types.Count * 5;
                for (var i = 0; i < attempts; i++)
                {
                    var troopType = types[i % types.Count];
                    var oneType = new List<Core.Itens.TroopSo>();
                    oneType.Add(troopType);
                    var batch = new List<Core.Entities.Troop>();
                    var args = new object[] { oneType, batch };
                    if (spawn.IsStatic)
                    {
                        spawn.Invoke(null, args);
                    }
                    else
                    {
                        spawn.Invoke(wave, args);
                    }

                    for (var j = 0; j < batch.Count; j++)
                    {
                        battlers.Add(batch[j]);
                    }
                }

                var enemiesList = Core.Wave.Enemies;
                var melee = Core.Wave.EnemyMeleeTroops;
                if (enemiesList != null && melee != null)
                {
                    for (var i = 0; i < battlers.Count; i++)
                    {
                        var troop = battlers[i];
                        if (troop == null)
                        {
                            continue;
                        }

                        try
                        {
                            troop.IsEnemy = true;
                        }
                        catch
                        {
                        }

                        enemiesList.Add(troop);
                        melee.Add(troop);

                        try
                        {
                            Core.Wave.RegisterTargetTroop(troop);
                        }
                        catch (Exception e)
                        {
                            VersusPlugin.Log.LogWarning($"arena: RegisterTargetTroop failed: {e.Message}");
                        }
                    }
                }

                VersusPlugin.Log.LogInfo(
                    $"arena: SpawnBattlers battlers={battlers.Count}; enemiesList={(enemiesList == null ? "null" : enemiesList.Count.ToString())} " +
                    $"melee={(melee == null ? "null" : melee.count.ToString())}");
            }
            else
            {
                // CreateEnemies builds one troop per call from the supplied type list,
                // so issue one call per opponent unit (with a small horizontal spread).
                for (var i = 0; i < types.Count; i++)
                {
                    var oneType = new List<Core.Itens.TroopSo>();
                    oneType.Add(types[i]);
                    var enemies = new List<Core.Entities.Troop>();
                    var spawnPosition = new Vector2(position.x - (i % 8) * 1.5f, position.y - (i / 8) * 1.5f);
                    var args = new object[] { spawnPosition, null, oneType, enemies };
                    if (_createEnemies.IsStatic)
                    {
                        _createEnemies.Invoke(null, args);
                    }
                    else
                    {
                        _createEnemies.Invoke(wave, args);
                    }
                }
            }

            var after = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
            var spawned = 0;
            if (after != null)
            {
                foreach (var entity in after)
                {
                    if (entity == null || beforePointers.Contains(entity.Pointer))
                    {
                        continue;
                    }

                    try
                    {
                        entity.IsEnemy = true;
                    }
                    catch
                    {
                        // Already an enemy or mid-setup.
                    }

                    spawned++;
                }
            }

            LastSpawned = spawned;
            LastSpawnWave = Core.Wave.Data?.Wave ?? -1;
            VersusPlugin.Log.LogInfo(
                $"arena: spawned {spawned} opponent entities from {types.Count} troop types at {position}; " +
                $"totalEntities={after?.Length ?? 0}");
            return spawned;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"arena: spawn failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
            return 0;
        }
    }
}
