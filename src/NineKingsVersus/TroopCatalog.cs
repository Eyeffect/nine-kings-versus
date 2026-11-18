using System;
using System.Collections.Generic;
using System.IO;
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

            var entries = new List<Entry>();
            foreach (var asset in assets)
            {
                if (asset == null)
                {
                    continue;
                }

                var entry = new Entry { name = asset.name };
                Collect((object)asset, entry.values, 0);
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
        foreach (var field in type.GetFields(flags))
        {
            object value;
            try
            {
                value = field.GetValue(source);
            }
            catch
            {
                continue;
            }

            if (value == null)
            {
                continue;
            }

            if (value is double || value is float || value is int || value is long || value is short || value is bool)
            {
                output[field.Name] = Convert.ToDouble(value);
            }
            else if (value is string text)
            {
                output[field.Name + "_len"] = text.Length;
            }
            else if (depth < 2 && !value.GetType().IsPrimitive && !(value is UnityEngine.Object))
            {
                Collect(value, output, depth + 1);
            }
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
