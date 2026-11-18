using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace NineKingsVersus;

/// <summary>
/// Self-contained 1v1 combat arena. The game engine is used only as a Unity
/// host: fighters are UGUI images, and movement, targeting, damage, death and
/// the win condition are entirely driven here. No Wave or troop AI is involved.
/// </summary>
internal static class CustomArena
{
    private sealed class Fighter
    {
        public RectTransform Rect;
        public Image Image;
        public bool Enemy;
        public float Hp;
        public float MaxHp;
        public float Damage;
        public float Speed;
        public float Range;
        public float Cooldown;
        public float AttackInterval;
        public float CritChance;
        public float CritMultiplier;
        public bool Ranged;
        public Color BaseColor;
    }

    private static readonly List<Fighter> Fighters = new List<Fighter>();
    private static GameObject _canvasRoot;
    private static RectTransform _panel;
    private static Text _resultLabel;
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
            EnsureCanvas();

            SpawnArmy(local, false, -420f);
            SpawnArmy(opponent, true, 420f);
            PlayerInitial = PlayerAlive;
            EnemyInitial = EnemyAlive;

            if (_resultLabel != null)
            {
                _resultLabel.text = "CUSTOM 1v1 ARENA";
            }

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
            if (fighter.Rect != null)
            {
                UnityEngine.Object.Destroy(fighter.Rect.gameObject);
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

        if (Winner < 0)
        {
            StepSide(player, enemy, deltaTime);
            StepSide(enemy, player, deltaTime);

            if (enemy.Count == 0)
            {
                Winner = 0;
            }
            else if (player.Count == 0)
            {
                Winner = 1;
            }

            if (Winner >= 0 && _resultLabel != null)
            {
                _resultLabel.text = Winner == 0 ? "VICTORY" : "DEFEAT";
                VersusPlugin.Log.LogInfo($"custom-arena: winner={(Winner == 0 ? "player" : "enemy")}");
            }
        }

        if (Time.time >= _nextLog)
        {
            _nextLog = Time.time + 5f;
            VersusPlugin.Log.LogInfo(
                $"custom-arena: player={PlayerAlive}/{PlayerInitial} enemy={EnemyAlive}/{EnemyInitial} " +
                $"winner={Winner} dt={deltaTime:F3}");
        }
    }

    private static void StepSide(List<Fighter> attackers, List<Fighter> defenders, float dt)
    {
        foreach (var attacker in attackers)
        {
            if (attacker.Rect == null || attacker.Hp <= 0)
            {
                continue;
            }

            var target = defenders
                .Where(d => d.Hp > 0 && d.Rect != null)
                .OrderBy(d => Vector2.Distance(attacker.Rect.anchoredPosition, d.Rect.anchoredPosition))
                .FirstOrDefault();
            if (target == null)
            {
                continue;
            }

            var delta = target.Rect.anchoredPosition - attacker.Rect.anchoredPosition;
            var distance = delta.magnitude;
            var desiredRange = attacker.Ranged ? attacker.Range * 0.8f : attacker.Range;
            if (distance > attacker.Range)
            {
                attacker.Rect.anchoredPosition += delta.normalized * (attacker.Speed * dt);
            }
            else if (attacker.Ranged && distance < desiredRange)
            {
                attacker.Rect.anchoredPosition -= delta.normalized * (attacker.Speed * dt);
            }
            else
            {
                attacker.Cooldown -= dt;
                if (attacker.Cooldown <= 0f)
                {
                    attacker.Cooldown = attacker.AttackInterval;
                    var crit = UnityEngine.Random.value < attacker.CritChance;
                    var hit = attacker.Damage * (crit ? attacker.CritMultiplier : 1f);
                    target.Hp -= (float)hit;
                    var ratio = Mathf.Clamp01(target.Hp / Mathf.Max(1f, target.MaxHp));
                    target.Image.color = Color.Lerp(new Color(0.25f, 0.25f, 0.25f, 1f), target.BaseColor, ratio);
                    if (target.Hp <= 0)
                    {
                        VersusPlugin.Log.LogInfo(
                            $"custom-arena: {(target.Enemy ? "enemy" : "player")} fighter destroyed");
                        UnityEngine.Object.Destroy(target.Rect.gameObject);
                        target.Rect = null;
                        target.Image = null;
                    }
                }
            }
        }
    }

    private static void SpawnArmy(Gameplay.GameSnapshot snapshot, bool enemy, float startX)
    {
        if (snapshot?.Areas == null)
        {
            return;
        }

        var index = 0;
        for (var areaIndex = 0; areaIndex < snapshot.Areas.Count; areaIndex++)
        {
            var area = snapshot.Areas[areaIndex];
            if (area == null || area.PlacedEntityType != "Troop" ||
                string.IsNullOrEmpty(area.PlacedCardName))
            {
                continue;
            }

            var stats = area.Stats;
            Dictionary<string, double> catalog = null;
            TroopCatalog.TryGet(area.PlacedCardName, out catalog);
            var hpFallback = Mathf.Max(20f, Stat(stats, "MaxHP", 50f) * (1f + 0.15f * (area.CardLevel - 1)));
            var strengthFallback = Stat(stats, "Strength", 12f) * 0.35f * (1f + 0.1f * (area.CardLevel - 1));
            var speedFallback = Stat(stats, "WalkSpeed", 1f) * 70f;
            var attackFallback = Stat(stats, "AttackSpeed", 0.5f);
            var critChanceFallback = Stat(stats, "CritChance", 0f);
            var critMultFallback = Stat(stats, "CritMultiplier", 1.5f);
            var projectilesFallback = Stat(stats, "Projectiles", 0f);

            var maxHp = Mathf.Max(20f, CatalogStat(catalog, hpFallback, "MaxHP"));
            var damage = Mathf.Max(3f, CatalogStat(catalog, strengthFallback, "AttackStrength", "Strength", "Damage"));
            var speed = Mathf.Clamp(CatalogStat(catalog, speedFallback, "WalkSpeed") * 70f, 25f, 180f);
            var attackDelay = CatalogStat(catalog, attackFallback * 60f, "AttackDelay");
            var attackInterval = Mathf.Clamp(attackDelay / 60f, 0.3f, 2.5f);
            var critChance = Mathf.Clamp01(CatalogStat(catalog, critChanceFallback, "CriticalChance"));
            var critMultiplier = Mathf.Max(1f, CatalogStat(catalog, critMultFallback, "CriticalMultiplier"));
            var ranged = CatalogStat(catalog, projectilesFallback, "ProjectileBased", "IsRanged", "Projectiles") > 0f;
            var attackRange = CatalogStat(catalog, 1f, "AttackRange") * 30f;
            var size = Mathf.Clamp(Stat(area, "EntitySize", 1f) * 16f, 14f, 34f);
            var count = Mathf.Clamp(area.TroopQuantity / 9, 1, 3);

            var sideColor = enemy
                ? new Color(0.9f, 0.25f, 0.2f, 1f)
                : new Color(0.25f, 0.55f, 1f, 1f);
            var color = Color.Lerp(sideColor, Color.white,
                Mathf.Repeat(area.PlacedCardName.GetHashCode() / 1000f, 0.35f));

            for (var n = 0; n < count; n++)
            {
                var go = new GameObject("Fighter");
                go.AddComponent<RectTransform>();
                go.transform.SetParent(_panel, false);
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(size, size);
                rect.anchoredPosition = new Vector2(startX, (index - 6f) * 28f);
                var image = go.AddComponent<Image>();
                image.color = color;
                Fighters.Add(new Fighter
                {
                    Rect = rect,
                    Image = image,
                    Enemy = enemy,
                    Hp = maxHp,
                    MaxHp = maxHp,
                    Damage = damage,
                    Speed = speed,
                    Range = ranged ? Mathf.Max(120f, attackRange) : Mathf.Max(26f, attackRange),
                    AttackInterval = attackInterval,
                    CritChance = critChance,
                    CritMultiplier = critMultiplier,
                    Ranged = ranged,
                    BaseColor = color,
                });
                index++;
            }
        }
    }

    private static float CatalogStat(Dictionary<string, double> values, float fallback, params string[] keywords)
    {
        if (values == null)
        {
            return fallback;
        }

        foreach (var keyword in keywords)
        {
            foreach (var pair in values)
            {
                if (string.Equals(pair.Key, keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return (float)pair.Value;
                }
            }
        }

        foreach (var keyword in keywords)
        {
            foreach (var pair in values)
            {
                if (pair.Key.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (float)pair.Value;
                }
            }
        }

        return fallback;
    }

    private static float Stat(object source, string name, float fallback)
    {
        if (source == null)
        {
            return fallback;
        }

        try
        {
            var type = source.GetType();
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
            var member = (object)type.GetProperty(name, flags) ?? type.GetField(name, flags);
            var value = member is System.Reflection.PropertyInfo property
                ? property.GetValue(source)
                : (member as System.Reflection.FieldInfo)?.GetValue(source);
            return value == null ? fallback : Convert.ToSingle(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static void EnsureCanvas()
    {
        if (_canvasRoot != null)
        {
            return;
        }

        _canvasRoot = new GameObject("VersusArenaCanvas");
        _canvasRoot.AddComponent<RectTransform>();
        var canvas = _canvasRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        _canvasRoot.AddComponent<CanvasScaler>();
        _canvasRoot.AddComponent<GraphicRaycaster>();
        UnityEngine.Object.DontDestroyOnLoad(_canvasRoot);

        var panelGo = new GameObject("ArenaPanel");
        panelGo.AddComponent<RectTransform>();
        panelGo.transform.SetParent(_canvasRoot.transform, false);
        _panel = panelGo.GetComponent<RectTransform>();
        _panel.anchorMin = new Vector2(0.5f, 0.5f);
        _panel.anchorMax = new Vector2(0.5f, 0.5f);
        _panel.pivot = new Vector2(0.5f, 0.5f);
        _panel.sizeDelta = new Vector2(1000f, 600f);
        var panelImage = panelGo.AddComponent<Image>();
        panelImage.color = new Color(0.03f, 0.04f, 0.07f, 0.85f);

        var labelGo = new GameObject("ResultLabel");
        labelGo.AddComponent<RectTransform>();
        labelGo.transform.SetParent(_panel, false);
        var labelRect = labelGo.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0.5f, 1f);
        labelRect.anchorMax = new Vector2(0.5f, 1f);
        labelRect.pivot = new Vector2(0.5f, 1f);
        labelRect.anchoredPosition = new Vector2(0f, -10f);
        labelRect.sizeDelta = new Vector2(400f, 40f);
        _resultLabel = labelGo.AddComponent<Text>();
        _resultLabel.text = "CUSTOM 1v1 ARENA";
        _resultLabel.fontSize = 24;
        _resultLabel.alignment = TextAnchor.MiddleCenter;
        _resultLabel.color = new Color(0.9f, 0.92f, 1f, 1f);
    }
}
