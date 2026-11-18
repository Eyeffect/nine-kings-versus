using System;
using System.IO;
using HarmonyLib;

namespace NineKingsVersus;

internal static class Patches
{
    [HarmonyPatch(typeof(Gameplay.GameSnapshot), nameof(Gameplay.GameSnapshot.CaptureSnapshot))]
    internal static class CaptureSnapshotPatch
    {
        private static void Postfix(Gameplay.GameSnapshot __result)
        {
            try
            {
                SnapshotDump.Write(__result, "capture");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"CaptureSnapshot dump failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Gameplay.GameplayManager), nameof(Gameplay.GameplayManager.LoadGame))]
    internal static class LoadGamePatch
    {
        private static void Prefix(Gameplay.GameSnapshot snapshot)
        {
            try
            {
                var wave = snapshot?.WaveData?.WaveNumber ?? -1;
                VersusPlugin.Log.LogInfo($"LoadGame: version={snapshot?.Version} wave={wave}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"LoadGame prefix failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Core.Wave), nameof(Core.Wave.StartWave))]
    internal static class WaveStartPatch
    {
        private static int _arenaSpawnedWave = -1;

        private static bool Prefix()
        {
            try
            {
                if (!VersusBehaviour.ClaimStartWave())
                {
                    return false;
                }

                LocalSessionProbe.EnsureEnemySnapshot("StartWave");
                var hasEnemy = Core.Wave.CurrentMultiplayerEnemySnapshot != null;
                var data = Core.Wave.Data;
                VersusPlugin.Log.LogInfo(
                    $"StartWave: multiplayerEnemySnapshot={hasEnemy} wave={data?.Wave} runMultiplayer={data?.IsMultiplayer} " +
                    $"map={(Core.Wave.MultiplayerMap == null ? "null" : Core.Wave.MultiplayerMap.activeSelf ? "active" : "inactive")}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"StartWave prefix failed: {e.Message}");
            }

            return true;
        }

        private static void Postfix()
        {
            try
            {
                var session = MultiplayerSystems.MultiplayerManager.Session;
                var runData = Core.Wave.Data;
                VersusPlugin.Log.LogInfo(
                    $"StartWave end: session={(session == null ? "null" : "set")} gauntlet={session?.GauntletEnemies?.Count ?? -1} " +
                    $"active={session?.GauntletActive.ToString() ?? "n/a"} runMultiplayer={(runData == null ? "no-data" : runData.IsMultiplayer.ToString())} " +
                    $"enemySnapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")} " +
                    $"map={(Core.Wave.MultiplayerMap == null ? "null" : Core.Wave.MultiplayerMap.activeSelf ? "active" : "inactive")} " +
                    $"enemyBase={(Core.Wave.MultiplayerEnemyBase == null ? "null" : "set")} " +
                    $"enemyTroops={Core.Kingdoms.KingdomAreaManager.GetAreasWithTroops(true)?.Count ?? -1} " +
                    $"enemyAnything={Core.Kingdoms.KingdomAreaManager.GetAreasWithAnythingPlaced(true)?.Count ?? -1}");

                var wave = runData?.Wave ?? -1;
                if (File.Exists(Path.Combine(VersusPlugin.DataDir, "arena")) && wave > 0 && _arenaSpawnedWave != wave)
                {
                    _arenaSpawnedWave = wave;
                    var opponent = LocalSessionProbe.GetOpponentSnapshot();
                    if (opponent != null)
                    {
                        // custom army spawn disabled: using WaveTroops injection instead
                    }
                    else
                    {
                        VersusPlugin.Log.LogWarning("arena: no opponent snapshot at wave start");
                    }
                }

                VersusBehaviour.ScheduleVerification();
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"StartWave postfix probe failed: {e.Message}");
            }
            try
            {
                SnapshotDump.Write(Gameplay.GameSnapshot.CaptureSnapshot(), "wave-start");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"StartWave snapshot dump failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Core.Wave), nameof(Core.Wave.EndWave), new Type[] { })]
    internal static class WaveEndPatch
    {
        private static void Postfix()
        {
            try
            {
                SnapshotDump.Write(Gameplay.GameSnapshot.CaptureSnapshot(), "wave-end");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"EndWave snapshot dump failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Core.Wave), nameof(Core.Wave.PrepareMultiplayerGauntlet))]
    internal static class PrepareGauntletPatch
    {
        private static void Prefix()
        {
            try
            {
                var session = MultiplayerSystems.MultiplayerManager.Session;
                var data = Core.Wave.Data;
                var profile = MultiplayerSystems.MultiplayerManager.PlayerProfileCache;
                var settings = MultiplayerSystems.MultiplayerManager.Settings;
                var sessionInfo = "no session";
                if (session != null)
                {
                    var ids = new System.Text.StringBuilder();
                    if (session.Data != null)
                    {
                        for (var i = 0; i < session.Data.Length; i++)
                        {
                            var run = session.Data[i];
                            ids.Append($"[{i}:run={run?.runId},player={run?.playerId},king={run?.kingId},year={run?.yearPlayed},snaps={run?.snapshot?.snapshots?.Count}]");
                        }
                    }

                    sessionInfo = $"data={ids} byKing={(session.GetDataByPlayerKing(Core.CardOwnership.KingOfSpells) == null ? "null" : "found")} " +
                        $"localSnaps={session.Snapshots?.snapshots?.Count ?? -1}";
                }

                VersusPlugin.Log.LogInfo(
                    $"PrepareGauntlet in: wave={data?.Wave} runMultiplayer={data?.IsMultiplayer} " +
                    $"profileId={profile?.playerId.ToString() ?? "null"} profileName={settings?.Username ?? "null"} " +
                    $"managerKing={(MultiplayerSystems.MultiplayerManager.Instance?.CurrentSelectedKing == null ? "null" : "set")} " +
                    $"session={sessionInfo} " +
                    $"gauntlet={session?.GauntletEnemies?.Count ?? -1} " +
                    $"active={session?.GauntletActive.ToString() ?? "n/a"} snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"PrepareGauntlet prefix failed: {e.Message}");
            }
        }

        private static void Postfix()
        {
            try
            {
                LocalSessionProbe.EnsureEnemySnapshot("PrepareGauntlet");
                VersusPlugin.Log.LogInfo(
                    $"PrepareGauntlet out: snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")} " +
                    $"wave={(Core.Wave.CurrentMultiplayerEnemySnapshot?.WaveData?.WaveNumber ?? -1)}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"PrepareGauntlet postfix failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Core.Wave), nameof(Core.Wave.SpawnWave))]
    internal static class SpawnWavePatch
    {
        private static void Prefix()
        {
            try
            {
                LocalSessionProbe.EnsureEnemySnapshot("SpawnWave");
                if (File.Exists(Path.Combine(VersusPlugin.DataDir, "custom-deck")))
                {
                    OpponentDeck.Install(LocalSessionProbe.GetOpponentSnapshot());
                }

                if (File.Exists(Path.Combine(VersusPlugin.DataDir, "arena")))
                {
                    // Official flow: keep the game's own WaveTroops and enemy spawning.
                }

                var data = Core.Wave.Data;
                var session = MultiplayerSystems.MultiplayerManager.Session;
                VersusPlugin.Log.LogInfo(
                    $"SpawnWave in: wave={data?.Wave} runMultiplayer={data?.IsMultiplayer} " +
                    $"snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")} " +
                    $"gauntlet={session?.GauntletEnemies?.Count ?? -1} active={session?.GauntletActive.ToString() ?? "n/a"}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"SpawnWave prefix failed: {e.Message}");
            }
        }

        private static void Postfix()
        {
            try
            {
                var entities = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
                var enemy = 0;
                var multiplayerEnemy = 0;
                if (entities != null)
                {
                    foreach (var entity in entities)
                    {
                        try
                        {
                            if (entity.IsEnemy)
                            {
                                enemy++;
                            }

                            if (entity.IsMultiplayerEnemy)
                            {
                                multiplayerEnemy++;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                VersusPlugin.Log.LogInfo(
                    $"SpawnWave out: entities={entities?.Length ?? 0} enemy={enemy} multiplayerEnemy={multiplayerEnemy} " +
                    $"waveTroops={Core.Wave.WaveTroops?.Length ?? -1} validTroops={Core.Wave.ValidTroops?.Count ?? -1}");

                var wave = Core.Wave.Data?.Wave ?? -1;
                if (File.Exists(Path.Combine(VersusPlugin.DataDir, "arena")) && wave > 0 && CleanArena.LastWave != wave)
                {
                    var opponent = LocalSessionProbe.GetOpponentSnapshot();
                    if (opponent != null)
                    {
                        // custom army spawn disabled: using WaveTroops injection instead
                    }
                    else
                    {
                        VersusPlugin.Log.LogWarning("clean-arena: no opponent snapshot at SpawnWave");
                    }
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"SpawnWave postfix failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Core.Wave), nameof(Core.Wave.StartCombat))]
    internal static class StartCombatPatch
    {
        private static void Prefix(bool instant)
        {
            try
            {
                var data = Core.Wave.Data;
                VersusPlugin.Log.LogInfo(
                    $"StartCombat in: instant={instant} wave={data?.Wave} runMultiplayer={data?.IsMultiplayer} " +
                    $"snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"StartCombat prefix failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MultiplayerSystems.MultiplayerGameSnapshots), nameof(MultiplayerSystems.MultiplayerGameSnapshots.GetWaveSnapshot))]
    internal static class GetWaveSnapshotPatch
    {
        private static void Postfix(int waveNumber, bool fetchClosestPreviousSnapshot, Gameplay.GameSnapshot __result)
        {
            try
            {
                VersusPlugin.Log.LogInfo(
                    $"GetWaveSnapshot({waveNumber}, closestPrev={fetchClosestPreviousSnapshot}) -> " +
                    $"{(__result == null ? "null" : $"wave {__result.WaveData?.WaveNumber}")}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"GetWaveSnapshot postfix failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MultiplayerSystems.MultiplayerManager), nameof(MultiplayerSystems.MultiplayerManager.SubmitMultiplayerRun))]
    internal static class BlockSubmitPatch
    {
        private static bool Prefix(Core.CardOwnership king)
        {
            VersusPlugin.Log.LogWarning($"SubmitMultiplayerRun blocked (king={king}) to protect online profile");
            return false;
        }
    }

    /// <summary>
    /// Keeps the probe fully offline: the network layer's own connectivity gate
    /// always answers "no internet", so every async fetch (profile/tiers/season
    /// cache, login, matchmaking) early-outs exactly like on an unplugged PC
    /// instead of reaching the service.
    /// </summary>
    [HarmonyPatch(typeof(MultiplayerSystems.Multiplayer), nameof(MultiplayerSystems.Multiplayer.IsConnectedToInternet))]
    internal static class OfflineGatePatch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }
}

internal static class SnapshotDump
{
    private static int _counter;
    private static DateTime _lastSent = DateTime.MinValue;

    public static void Write(Gameplay.GameSnapshot snapshot, string reason)
    {
        if (snapshot == null)
        {
            return;
        }

        try
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(snapshot);
            if (string.IsNullOrEmpty(json))
            {
                VersusPlugin.Log.LogWarning($"snapshot dump ({reason}) produced no JSON");
                return;
            }

            var wave = -1;
            try
            {
                wave = snapshot.WaveData?.WaveNumber ?? -1;
            }
            catch
            {
                // WaveData can be missing on partially built snapshots; the JSON still helps.
            }

            var lives = snapshot.WaveData?.PlayerLifes ?? -1;
            VersusNet.SetLocalLives(lives);

            if (VersusNet.Connected && wave >= 1 &&
                (reason == "wave-start" || reason == "capture") &&
                (DateTime.UtcNow - _lastSent) > TimeSpan.FromSeconds(2))
            {
                _lastSent = DateTime.UtcNow;
                VersusNet.SetLocalLives(lives);
                VersusNet.SendState(wave, json, lives);
            }

            var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-w{wave:D3}-{reason}-{++_counter}.json";
            var path = Path.Combine(VersusPlugin.DataDir, name);
            File.WriteAllText(path, json);
            VersusPlugin.Log.LogInfo($"snapshot dump ({reason}) wave={wave} bytes={json.Length} -> {name}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"snapshot dump ({reason}) failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
