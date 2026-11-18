using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace NineKingsVersus;

/// <summary>
/// Dumps every TroopSo asset from the running game into BepInEx/versus/troops.json.
/// The custom arena then uses these exact values instead of approximations.
/// </summary>
internal static class TroopCatalog
{
    private static readonly string Path = System.IO.Path.Combine(VersusPlugin.DataDir, "troops.json");

    private sealed class Entry
    {
        public string name { get; set; }
        public Dictionary<string, double> values { get; set; } = new Dictionary<string, double>();
    }

    public static void Dump()
    {
        try
        {
            var assets = Resources.FindObjectsOfTypeAll<Core.Itens.TroopSo>();
            if (assets == null || assets.Length == 0)
            {
                VersusPlugin.Log.LogWarning("troop catalog: no TroopSo assets found");
                return;
            }

            var sample = assets[0];
            var allFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                           System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            VersusPlugin.Log.LogInfo(
                $"troop catalog: sample={sample.GetType().FullName} " +
                $"props={sample.GetType().GetProperties(allFlags).Length} fields={sample.GetType().GetFields(allFlags).Length}");
            foreach (var p in sample.GetType().GetProperties(allFlags).Take(12))
            {
                VersusPlugin.Log.LogInfo($"troop catalog: prop {p.PropertyType.Name} {p.Name}");
            }

            var entries = new List<Entry>();
            foreach (var asset in assets)
            {
                if (asset == null)
                {
                    continue;
                }

                var entry = new Entry { name = asset.name };
                Collect((object)asset, entry.values, 0);
                CollectEffectMethod(asset, "GetWaveStartEffects", entry.values);
                CollectEffectMethod(asset, "GetWaveEndEffects", entry.values);
                entries.Add(entry);
            }

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path, json);
            VersusPlugin.Log.LogInfo($"troop catalog: wrote {entries.Count} troops to troops.json");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"troop catalog failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Collect(object source, Dictionary<string, double> output, int depth)
    {
        if (source == null || depth > 3)
        {
            return;
        }

        var type = source.GetType();
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "transform", "gameObject", "hideFlags", "name"
        };

        var current = source.GetType();
        while (current != null && current != typeof(object))
        {
            CollectType(source, current, output, depth, flags, skip);
            current = current.BaseType;
        }
    }

    private static void CollectType(object source, Type type, Dictionary<string, double> output,
        int depth, System.Reflection.BindingFlags flags, HashSet<string> skip)
    {
        var declared = flags | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(declared))
        {
            if (skip.Contains(field.Name))
            {
                continue;
            }

            object value;
            try
            {
                value = field.GetValue(source);
            }
            catch
            {
                continue;
            }

            AddValue(value, field.Name, output, depth);
        }

        foreach (var property in type.GetProperties(declared))
        {
            if (skip.Contains(property.Name) || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object value;
            try
            {
                value = property.GetValue(source);
            }
            catch
            {
                continue;
            }

            AddValue(value, property.Name, output, depth);
        }
    }

    private static void AddValue(object value, string key, Dictionary<string, double> output, int depth)
    {
        if (value == null)
        {
            return;
        }

        if (value is double || value is float || value is int || value is long || value is short || value is bool)
        {
            output[key] = Convert.ToDouble(value);
        }
        else if (value is string text)
        {
            output[key + "_len"] = text.Length;
        }
        else if (depth < 3 && value is System.Collections.IEnumerable enumerable && !(value is string))
        {
            CollectEnumerable(enumerable, key, output, depth);
        }
        else if (depth < 3 && !(value is string))
        {
            Collect(value, output, depth + 1);
        }
    }

    private static void CollectEffectMethod(object source, string methodName, Dictionary<string, double> output)
    {
        try
        {
            var method = source.GetType().GetMethod(methodName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (method == null)
            {
                return;
            }

            var result = method.Invoke(method.IsStatic ? null : source, null) as System.Collections.IEnumerable;
            if (result != null)
            {
                CollectEnumerable(result, methodName, output, 1);
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"troop catalog: {methodName} failed: {e.Message}");
        }
    }

    private static void CollectEnumerable(System.Collections.IEnumerable source, string key,
        Dictionary<string, double> output, int depth)
    {
        var index = 0;
        foreach (var item in source)
        {
            if (index >= 16)
            {
                break;
            }

            if (item == null)
            {
                index++;
                continue;
            }

            if (item is double || item is float || item is int || item is long || item is short || item is bool)
            {
                output[$"{key}_{index}"] = Convert.ToDouble(item);
            }
            else if (item is string text)
            {
                output[$"{key}_{index}_len"] = text.Length;
            }
            else
            {
                var nested = new Dictionary<string, double>();
                Collect(item, nested, depth + 1);
                foreach (var pair in nested)
                {
                    output[$"{key}_{index}_{pair.Key}"] = pair.Value;
                }
            }

            index++;
        }
    }

    public static bool TryGet(string troopName, out Dictionary<string, double> values)
    {
        values = null;
        try
        {
            if (!File.Exists(Path))
            {
                return false;
            }

            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path));
            if (entries == null)
            {
                return false;
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry.name, troopName, StringComparison.OrdinalIgnoreCase))
                {
                    values = entry.values;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
