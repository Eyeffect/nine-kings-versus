using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NineKingsVersus;

/// <summary>
/// Custom combat simulation. It uses the game only as a renderer: troops are
/// created through the game's troop factory, but movement, targeting, damage,
/// death and the win condition are all driven here, independently of Wave.
/// </summary>
internal static class CustomArena
{
    private sealed class Fighter
    {
        public Core.Entities.Troop Troop;
        public bool Enemy;
        public double Hp;
        public double MaxHp;
        public double Damage;
        public double Speed;
        public double Range;
        public double AttackCooldown;
    }

    private static readonly List<Fighter> Fighters = new List<Fighter>();
    private static System.Reflection.MethodInfo _factory;
    private static Core.Kingdoms.KingdomArea _area;
    private static float _nextLog;

    public static bool Active => Fighters.Count > 0;
    public static int PlayerAlive => Fighters.Count(f => !f.Enemy);
    public static int EnemyAlive => Fighters.Count(f => f.Enemy);
    public static int PlayerInitial { get; private set; }
    public static int EnemyInitial { get; private set; }
    public static int Winner { get; private set; } = -1; // -1 none, 0 player, 1 enemy

    public static void StartBattle(Gameplay.GameSnapshot local, Gameplay.GameSnapshot opponent)
    {
        try
        {
            Stop();
            Winner = -1;

            if (_factory == null)
            {
                _factory = typeof(Core.Entities.Troop).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Contains("Troop_TroopSo_KingdomArea_Boolean_Boolean"));
            }

            _area = FindArea();
            if (_factory == null || _area == null)
            {
                VersusPlugin.Log.LogWarning("custom-arena: factory or area unavailable");
                return;
            }

            SpawnArmy(local, false, -9f);
            SpawnArmy(opponent, true, 9f);
            PlayerInitial = PlayerAlive;
            EnemyInitial = EnemyAlive;
            VersusPlugin.Log.LogInfo(
                $"custom-arena: battle started player={PlayerInitial} enemy={EnemyInitial}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"custom-arena: start failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public static void Stop()
    {
        foreach (var fighter in Fighters)
        {
            if (fighter.Troop != null)
            {
                UnityEngine.Object.Destroy(fighter.Troop.gameObject);
            }
        }

        Fighters.Clear();
        PlayerInitial = 0;
        EnemyInitial = 0;
        Winner = -1;
    }

    public static void Update(float deltaTime)
    {
        if (!Active)
        {
            return;
        }

        var player = Fighters.Where(f => !f.Enemy).ToList();
        var enemy = Fighters.Where(f => f.Enemy).ToList();

        StepSide(player, enemy, deltaTime, 1f);
        StepSide(enemy, player, deltaTime, -1f);

        if (Time.time >= _nextLog)
        {
            _nextLog = Time.time + 5f;
            var sampleA = Fighters.FirstOrDefault();
            var sampleB = Fighters.LastOrDefault();
            var posA = sampleA?.Troop != null ? sampleA.Troop.transform.position.ToString() : "dead";
            var posB = sampleB?.Troop != null ? sampleB.Troop.transform.position.ToString() : "dead";
            VersusPlugin.Log.LogInfo(
                $"custom-arena: player={PlayerAlive}/{PlayerInitial} enemy={EnemyAlive}/{EnemyInitial} " +
                $"winner={Winner} a={posA} b={posB} dt={deltaTime:F3}");
        }

        if (Winner < 0)
        {
            if (enemy.Count == 0)
            {
                Winner = 0;
                VersusPlugin.Log.LogInfo("custom-arena: player wins");
            }
            else if (player.Count == 0)
            {
                Winner = 1;
                VersusPlugin.Log.LogInfo("custom-arena: enemy wins");
            }
        }
    }

    private static void StepSide(List<Fighter> attackers, List<Fighter> defenders, float dt, float direction)
    {
        foreach (var attacker in attackers)
        {
            if (attacker.Troop == null || attacker.Hp <= 0)
            {
                continue;
            }

            var target = defenders.Where(d => d.Hp > 0 && d.Troop != null)
                .OrderBy(d => Vector3.Distance(attacker.Troop.transform.position, d.Troop.transform.position))
                .FirstOrDefault();
            if (target == null)
            {
                continue;
            }

            var position = attacker.Troop.transform.position;
            var targetPosition = target.Troop.transform.position;
            var distance = Vector3.Distance(position, targetPosition);
            if (distance > attacker.Range)
            {
                var step = Vector3.MoveTowards(position, targetPosition, (float)(attacker.Speed * dt));
                attacker.Troop.transform.position = step;
            }
            else
            {
                attacker.AttackCooldown -= dt;
                if (attacker.AttackCooldown <= 0f)
                {
                    attacker.AttackCooldown = 1f;
                    target.Hp -= attacker.Damage;
                    if (target.Hp <= 0)
                    {
                        VersusPlugin.Log.LogInfo($"custom-arena: {(target.Enemy ? "enemy" : "player")} troop died");
                        UnityEngine.Object.Destroy(target.Troop.gameObject);
                        target.Troop = null;
                    }
                }
            }

            _ = direction;
        }
    }

    private static void SpawnArmy(Gameplay.GameSnapshot snapshot, bool isEnemy, float startX)
    {
        var types = new List<Core.Itens.TroopSo>();
        if (snapshot?.Areas != null)
        {
            foreach (var area in snapshot.Areas)
            {
                if (area == null || string.IsNullOrEmpty(area.PlacedCardName) || area.PlacedEntityType != "Troop")
                {
                    continue;
                }

                var type = Core.Game.GetTroop(area.PlacedCardName);
                if (type == null)
                {
                    continue;
                }

                var count = Math.Max(1, Math.Min(3, area.TroopQuantity / 9));
                for (var i = 0; i < count; i++)
                {
                    types.Add(type);
                }
            }
        }

        for (var i = 0; i < types.Count; i++)
        {
            Core.Entities.Troop troop;
            try
            {
                var args = new object[] { types[i], _area, isEnemy, true };
                troop = (Core.Entities.Troop)(_factory.IsStatic
                    ? _factory.Invoke(null, args)
                    : _factory.Invoke(null, args));
            }
            catch
            {
                continue;
            }

            if (troop == null)
            {
                continue;
            }

            troop.transform.position = new Vector3(startX, (i - types.Count / 2f) * 1.4f, 0f);
            troop.enabled = false;
            Fighters.Add(new Fighter
            {
                Troop = troop,
                Enemy = isEnemy,
                Hp = 100,
                MaxHp = 100,
                Damage = 12,
                Speed = 2.2,
                Range = 1.2,
            });
        }
    }

    private static Core.Kingdoms.KingdomArea FindArea()
    {
        var melee = Core.Wave.PlayerMeleeTroops;
        if (melee != null && melee.Troops != null && melee.Troops.Count > 0)
        {
            var field = typeof(Core.Entities.Entity).GetField("m_Area",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var area = field?.GetValue(melee.Troops[0]) as Core.Kingdoms.KingdomArea;
            if (area != null)
            {
                return area;
            }
        }

        var areas = UnityEngine.Object.FindObjectsOfType<Core.Kingdoms.KingdomArea>();
        return areas != null && areas.Length > 0 ? areas[0] : null;
    }
}
