using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NineKingsVersus;

[BepInPlugin(Guid, Name, Version)]
public sealed class VersusPlugin : BasePlugin
{
    public const string Guid = "ninakings.versus";
    public const string Name = "9 Kings Versus";
    public const string Version = "0.0.1";

    internal static new ManualLogSource Log;
    internal static string DataDir;

    public override void Load()
    {
        Log = base.Log;
        UnityEngine.Application.runInBackground = true;
        UnityEngine.Application.stackTraceLogType = UnityEngine.StackTraceLogType.Full;
        System.AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        DataDir = Path.Combine(Paths.BepInExRootPath, "versus");
        Directory.CreateDirectory(DataDir);

        Log.LogInfo($"{Name} {Version} loading (probe)");
        ProbeTypes();

        try
        {
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Patches.BlockSubmitPatch));
            Log.LogInfo("Online run submission blocked for this test copy");
            PatchOne(harmony, typeof(Patches.OfflineGatePatch));
            PatchOne(harmony, typeof(Patches.PrepareGauntletPatch));
            PatchOne(harmony, typeof(Patches.SpawnWavePatch));
            PatchOne(harmony, typeof(Patches.GetWaveSnapshotPatch));
            PatchOne(harmony, typeof(Patches.SetWavePatch));
            PatchOne(harmony, typeof(Patches.CompleteWavePatch));
            PatchOne(harmony, typeof(Patches.StartCombatPatch));
            PatchOne(harmony, typeof(Patches.CaptureSnapshotPatch));
            PatchOne(harmony, typeof(Patches.WaveStartPatch));
            PatchOne(harmony, typeof(Patches.WaveEndPatch));
            PatchOne(harmony, typeof(Patches.LoadGamePatch));
            PatchOne(harmony, typeof(Patches.GameplayMpStartPatch));
            PatchOne(harmony, typeof(Patches.PopulateEnemyFacesPatch));
            PatchOne(harmony, typeof(Patches.UserFaceSetupPatch));
            PatchOne(harmony, typeof(Patches.RefreshWaveKingPatch));
            PatchOne(harmony, typeof(Patches.CanOpenPauseMenuPatch));
            PatchOne(harmony, typeof(Patches.PauseShowOverridePatch));
            PatchOne(harmony, typeof(Patches.PauseHideGuardPatch));
            PatchOne(harmony, typeof(Patches.PerksBtnPatch));
            PatchOne(harmony, typeof(Patches.CompendiumBtnPatch));
            PatchOne(harmony, typeof(Patches.MissionBtnPatch));
            PatchOne(harmony, typeof(Patches.OptionsBtnPatch));
            PatchOne(harmony, typeof(Patches.PauseResumePatch));
            PatchOne(harmony, typeof(Patches.PauseMenuBtnPatch));

            Log.LogInfo("Harmony patches applied");
        }
        catch (Exception e)
        {
            Log.LogError($"Patching failed (game keeps running without the probe): {e}");
        }

        try
        {
            VersusNet.Init();
            VersusBehaviour.Install();
        }
        catch (Exception e)
        {
            Log.LogError($"Behaviour install failed: {e}");
        }
    }

    private static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
    {
        var e = args.Exception;
        if (e is System.NullReferenceException)
        {
            Log.LogWarning($"[NRE] {e.StackTrace}");
        }
    }

    private static void PatchOne(Harmony harmony, Type patchType)
    {
        try
        {
            harmony.PatchAll(patchType);
            Log.LogInfo($"patched {patchType.Name}");
        }
        catch (Exception e)
        {
            Log.LogWarning($"patch {patchType.Name} failed: {e.Message}");
        }
    }

    private static void ProbeTypes()
    {
        Report("Gameplay.GameSnapshot", typeof(Gameplay.GameSnapshot));
        Report("Gameplay.GameplayManager", typeof(Gameplay.GameplayManager));
        Report("Gameplay.GameplayMultiplayerManager", typeof(Gameplay.GameplayMultiplayerManager));
        Report("Core.Wave", typeof(Core.Wave));
        Report("MultiplayerSystems.MultiplayerManager", typeof(MultiplayerSystems.MultiplayerManager));
        Report("MultiplayerSystems.MultiplayerSession", typeof(MultiplayerSystems.MultiplayerSession));
        Report("MultiplayerSystems.MultiplayerRunData", typeof(MultiplayerSystems.MultiplayerRunData));
    }

    private static void Report(string label, Type type)
    {
        try
        {
            Log.LogInfo($"probe: {label} -> {(type == null ? "MISSING" : type.FullName)}");
        }
        catch (Exception e)
        {
            Log.LogWarning($"probe: {label} threw {e.GetType().Name}: {e.Message}");
        }
    }
}
