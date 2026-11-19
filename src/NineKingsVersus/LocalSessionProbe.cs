using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using MultiplayerSystems;

namespace NineKingsVersus;

internal static class LocalSessionProbe
{
    public static Core.Entities.KingCollectionSo FindKings()
    {
        var found = UnityEngine.Object.FindObjectOfType<Core.Entities.KingCollectionSo>();
        if (found != null)
        {
            return found;
        }

        var all = UnityEngine.Resources.FindObjectsOfTypeAll<Core.Entities.KingCollectionSo>();
        return all != null && all.Length > 0 ? all[0] : null;
    }

    private sealed class PlayerChoices
    {
        public int kingId { get; set; } = -1;
        public int perkIndex { get; set; } = 1;
    }

    private static string ChoicesPath => Path.Combine(VersusPlugin.DataDir, "choices.json");

    public static void ReadChoices(out int kingId, out int perkIndex)
    {
        kingId = -1;
        perkIndex = 1;
        try
        {
            if (!File.Exists(ChoicesPath))
            {
                return;
            }

            var choices = System.Text.Json.JsonSerializer.Deserialize<PlayerChoices>(File.ReadAllText(ChoicesPath));
            if (choices == null)
            {
                return;
            }

            kingId = choices.kingId;
            perkIndex = choices.perkIndex;
        }
        catch
        {
        }
    }

    public static void WriteChoices(int kingId, int perkIndex)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                new PlayerChoices { kingId = kingId, perkIndex = perkIndex });
            File.WriteAllText(ChoicesPath, json);
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"choices write failed: {e.Message}");
        }
    }

    /// <summary>
    /// Applies the player's king/perk choices in the hub before building the run.
    /// </summary>
    private static void ApplyChoices(ref Core.CardOwnership owner)
    {
        ReadChoices(out var kingId, out var perkIndex);
        if (kingId < 0)
        {
            return;
        }

        var entry = FindKings()?.GetEntry((Core.CardOwnership)kingId);
        if (entry?.King == null)
        {
            VersusPlugin.Log.LogWarning($"choices: king {kingId} unavailable");
            return;
        }

        try
        {
            MultiplayerManager.Instance?.SelectKing(entry.King, true, true);
            owner = (Core.CardOwnership)kingId;
            VersusPlugin.Log.LogInfo(
                $"choices: king={entry.King.name} perkLoadout={perkIndex} (stored)");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"choices apply failed: {e.Message}");
        }
    }

    public static void Run()
    {
        try
        {
            var files = Directory.GetFiles(VersusPlugin.DataDir, "*-f7-capture-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
                throw new InvalidOperationException("No captured snapshot available");

            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(files[^1]));
            if (snapshot?.Kings == null || snapshot.WaveData == null)
                throw new InvalidOperationException("Snapshot has no king or wave data");

            var kingName = "KingOf" + snapshot.Kings.PlayerKing;
            if (!Enum.TryParse<Core.CardOwnership>(kingName, out var owner))
                throw new InvalidOperationException($"Unmapped snapshot king: {snapshot.Kings.PlayerKing}");

            var profile = new PlayerProfileData { playerId = 900001, username = "Local probe" };
            var snapshots = new MultiplayerGameSnapshots();
            snapshots.Add(snapshot);
            snapshots.FinalSnapshot = snapshot;
            var run = new MultiplayerRunData
            {
                runId = 900001,
                playerId = profile.playerId,
                kingId = (int)owner,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = snapshots,
                player = profile,
            };
            var data = new Il2CppReferenceArray<MultiplayerRunData>(1);
            data[0] = run;
            var session = new MultiplayerSession(data,
                new Il2CppStringArray(0), new Il2CppStringArray(0),
                new Il2CppStringArray(0), new Il2CppStringArray(0));

            var byKing = session.GetDataByPlayerKing(owner);
            var byPlayer = session.TryGetDataByPlayerId(profile.playerId, out var found);
            var wave = snapshots.GetWaveSnapshot(snapshot.WaveData.WaveNumber, true);
            VersusPlugin.Log.LogInfo($"Detached session: runs={session.Data?.Length} king={owner} kingLookup={byKing != null} playerLookup={byPlayer && found != null} waveLookup={wave != null}");
            VersusPlugin.Log.LogInfo($"Detached session: gauntletList={session.GauntletEnemies != null} snapshots={session.Snapshots != null}; NOT attached, combat unchanged");

            var liveSession = MultiplayerManager.Session;
            var livePointer = liveSession?.Pointer ?? IntPtr.Zero;
            VersusPlugin.Log.LogInfo($"Detached gauntlet: before count={session.GauntletEnemies?.Count ?? -1} active={session.GauntletActive}");
            VersusPlugin.Log.LogInfo($"Detached gauntlet: managerInstance={(MultiplayerManager.Instance == null ? "null" : "alive")} settings={(MultiplayerManager.Settings == null ? "null" : "alive")} settingsDefault={(MultiplayerSettings.Default == null ? "null" : "alive")}");
            try
            {
                var count = UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings;
                for (var i = 0; i < count; i++)
                {
                    var path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
                    VersusPlugin.Log.LogInfo($"Build scene[{i}]: {path}");
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"Build scene enumeration failed: {e.Message}");
            }

            var closureType = typeof(MultiplayerSession).GetNestedType("__c",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var closureSingleton = closureType?.GetProperty("__9",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            VersusPlugin.Log.LogInfo($"Detached gauntlet: closureType={closureType != null} closureSingleton={(closureSingleton == null ? "n/a" : closureSingleton.GetValue(null) != null ? "alive" : "null")}");
            VersusPlugin.Log.LogInfo($"Detached gauntlet: caches cardPool={(MultiplayerManager.SeasonCardPoolCache == null ? "null" : "alive")} cardFallbacks={(MultiplayerManager.SeasonCardFallbacksCache == null ? "null" : "alive")} decreePool={(MultiplayerManager.SeasonDecreePoolCache == null ? "null" : "alive")} decreeFallbacks={(MultiplayerManager.SeasonDecreeFallbacksCache == null ? "null" : "alive")} profile={(MultiplayerManager.PlayerProfileCache == null ? "null" : "alive")} hasTiers={MultiplayerManager.HasCachedTiers}");
            try
            {
                session.PopulateGauntletEnemies();
                VersusPlugin.Log.LogInfo($"Detached gauntlet: after count={session.GauntletEnemies?.Count ?? -1} active={session.GauntletActive}");
                if (session.GauntletEnemies != null)
                {
                    for (var i = 0; i < session.GauntletEnemies.Count; i++)
                    {
                        var enemy = session.GauntletEnemies[i];
                        var final = enemy?.snapshot?.GetFinalSnapshot();
                        VersusPlugin.Log.LogInfo($"Detached gauntlet enemy[{i}]: player={enemy?.playerId} king={enemy?.kingId} finalSnapshot={final != null} areas={final?.Areas?.Count ?? -1}");
                    }
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"Detached gauntlet population unavailable: {e}");
            }
            finally
            {
                var unchanged = (MultiplayerManager.Session?.Pointer ?? IntPtr.Zero) == livePointer;
                VersusPlugin.Log.LogInfo($"Detached gauntlet: liveSessionUnchanged={unchanged}");
            }

            var property = typeof(MultiplayerManager).GetProperty("Session",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            VersusPlugin.Log.LogInfo($"Session attachment surface: property={property != null} writable={property?.CanWrite} staticSetter={property?.SetMethod?.IsStatic}");

            if (MultiplayerManager.SeasonCardPoolCache != null && MultiplayerManager.SeasonDecreePoolCache != null)
            {
                VersusPlugin.Log.LogInfo("Cache seeding: pools already alive, skipping seed");
                return;
            }

            var emptyStrings = new Il2CppStringArray(0);
            MultiplayerManager.SeasonCardPoolCache = emptyStrings;
            MultiplayerManager.SeasonCardFallbacksCache = emptyStrings;
            MultiplayerManager.SeasonDecreePoolCache = emptyStrings;
            MultiplayerManager.SeasonDecreeFallbacksCache = emptyStrings;
            VersusPlugin.Log.LogInfo($"Cache seeding: cardPool={(MultiplayerManager.SeasonCardPoolCache == null ? "null" : "alive")} cardFallbacks={(MultiplayerManager.SeasonCardFallbacksCache == null ? "null" : "alive")} decreePool={(MultiplayerManager.SeasonDecreePoolCache == null ? "null" : "alive")} decreeFallbacks={(MultiplayerManager.SeasonDecreeFallbacksCache == null ? "null" : "alive")}");

            var comparatorType = typeof(MultiplayerSession).GetNestedType("__c",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var comparator = comparatorType?.GetMethod("_PopulateGauntletEnemies_b__16_0",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (comparator != null)
            {
                var singleton = comparatorType.GetProperty("__9",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null);
                try
                {
                    var result = comparator.Invoke(singleton, new object[] { run, run });
                    VersusPlugin.Log.LogInfo($"Comparator probe: compare(run, run)={result} (no exception -> run data is sort-safe)");
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"Comparator probe: compare(run, run) threw {e.InnerException?.GetType().Name ?? e.GetType().Name}: {e.InnerException?.Message ?? e.Message} (run data is the blocker)");
                }
            }
            else
            {
                VersusPlugin.Log.LogWarning("Comparator probe: comparator method not found");
            }

            var localSettings = PlayerSettingsData.CreateLocalSnapshot();
            VersusPlugin.Log.LogInfo($"Profile discriminator: CreateLocalSnapshot={(localSettings == null ? "null" : "alive")}");
            if (localSettings == null)
            {
                return;
            }

            var richProfile = new PlayerProfileData            {
                playerId = 900002,
                username = "Local probe",
                settings = localSettings,
            };
            var appliedSettings = MultiplayerManager.ApplyProfile(richProfile, false);
            richProfile.settings = appliedSettings ?? richProfile.settings;
            VersusPlugin.Log.LogInfo($"Profile discriminator: ApplyProfile={(appliedSettings == null ? "null" : "alive")} completedMissions={richProfile.completedMissions != null} unlockedItems={richProfile.unlockedItems != null}");
            richProfile.completedMissions = richProfile.completedMissions ?? new Il2CppSystem.Collections.Generic.List<int>();
            richProfile.unlockedItems = richProfile.unlockedItems ?? new Il2CppSystem.Collections.Generic.List<int>();
            VersusPlugin.Log.LogInfo($"Profile discriminator: lists allocated completedMissions={richProfile.completedMissions.Count} unlockedItems={richProfile.unlockedItems.Count}");

            var richSnapshots = new MultiplayerGameSnapshots();
            richSnapshots.Add(snapshot);
            richSnapshots.FinalSnapshot = snapshot;
            var richRun = new MultiplayerRunData
            {
                runId = 900002,
                playerId = richProfile.playerId,
                kingId = (int)owner,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = richSnapshots,
                player = richProfile,
            };
            var richData = new Il2CppReferenceArray<MultiplayerRunData>(1);
            richData[0] = richRun;
            var richSession = new MultiplayerSession(richData,
                new Il2CppStringArray(0), new Il2CppStringArray(0),
                new Il2CppStringArray(0), new Il2CppStringArray(0));

            var livePointer2 = MultiplayerManager.Session?.Pointer ?? IntPtr.Zero;
            try
            {
                richSession.PopulateGauntletEnemies();
                VersusPlugin.Log.LogInfo($"Rich profile: populated count={richSession.GauntletEnemies?.Count ?? -1} active={richSession.GauntletActive}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"Rich profile: population unavailable ({e}); profile completeness ruled out as blocker");
            }
            finally
            {
                VersusPlugin.Log.LogInfo($"Rich profile: liveSessionUnchanged={(MultiplayerManager.Session?.Pointer ?? IntPtr.Zero) == livePointer2}");
            }

            // Two-run discriminator: real sessions always carry >=2 runs (one per
            // player). If population assumes a second run, a single-run session NREs.
            var opponentSnapshots = new MultiplayerGameSnapshots();
            opponentSnapshots.Add(snapshot);
            opponentSnapshots.FinalSnapshot = snapshot;
            var opponentProfile = new PlayerProfileData
            {
                playerId = 900003,
                username = "Local probe opponent",
                completedMissions = new Il2CppSystem.Collections.Generic.List<int>(),
                unlockedItems = new Il2CppSystem.Collections.Generic.List<int>(),
            };
            var opponentKing = owner == Core.CardOwnership.KingOfSpells
                ? Core.CardOwnership.KingOfNothing
                : Core.CardOwnership.KingOfSpells;
            var opponentRun = new MultiplayerRunData
            {
                runId = 900003,
                playerId = opponentProfile.playerId,
                kingId = (int)opponentKing,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = opponentSnapshots,
                player = opponentProfile,
            };

            // Comparator on two different runs first: if it NREs, run-field content is the blocker.
            var comparatorType2 = typeof(MultiplayerSession).GetNestedType("__c",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var comparator2 = comparatorType2?.GetMethod("_PopulateGauntletEnemies_b__16_0",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (comparator2 != null)
            {
                var singleton2 = comparatorType2.GetProperty("__9",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null);
                try
                {
                    var cmp = comparator2.Invoke(singleton2, new object[] { run, opponentRun });
                    VersusPlugin.Log.LogInfo($"Two-run comparator: compare(run, opponent)={cmp}");
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"Two-run comparator threw: {e.InnerException?.Message ?? e.Message}");
                }
            }

            var pairData = new Il2CppReferenceArray<MultiplayerRunData>(2);
            pairData[0] = run;
            pairData[1] = opponentRun;
            var pairSession = new MultiplayerSession(pairData,
                new Il2CppStringArray(0), new Il2CppStringArray(0),
                new Il2CppStringArray(0), new Il2CppStringArray(0));

            var livePointer3 = MultiplayerManager.Session?.Pointer ?? IntPtr.Zero;
            try
            {
                pairSession.PopulateGauntletEnemies();
                var count = pairSession.GauntletEnemies?.Count ?? -1;
                VersusPlugin.Log.LogInfo($"Two-run session: populated count={count} active={pairSession.GauntletActive}");
                if (pairSession.GauntletEnemies != null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var enemy = pairSession.GauntletEnemies[i];
                        VersusPlugin.Log.LogInfo($"Two-run gauntlet enemy[{i}]: player={enemy?.playerId} king={enemy?.kingId} runId={enemy?.runId}");
                    }
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"Two-run session: population unavailable ({e})");
            }
            finally
            {
                VersusPlugin.Log.LogInfo($"Two-run session: liveSessionUnchanged={(MultiplayerManager.Session?.Pointer ?? IntPtr.Zero) == livePointer3}");
            }

            // Manual population: supply GauntletEnemies directly, bypassing the method
            // that NREs. Tells us whether the property is writable and what GauntletActive
            // derives from.
            var gauntletProp = typeof(MultiplayerSession).GetProperty("GauntletEnemies",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeProp = typeof(MultiplayerSession).GetProperty("GauntletActive",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            VersusPlugin.Log.LogInfo($"Manual population: gauntletEnemiesWritable={gauntletProp?.CanWrite} gauntletActiveWritable={activeProp?.CanWrite}");
            if (gauntletProp != null && gauntletProp.CanWrite)
            {
                var manual = new Il2CppSystem.Collections.Generic.List<MultiplayerRunData>();
                manual.Add(opponentRun);
                gauntletProp.SetValue(pairSession, manual);
                VersusPlugin.Log.LogInfo($"Manual population: count={(pairSession.GauntletEnemies?.Count ?? -1)} active={pairSession.GauntletActive}");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"Detached session probe failed: {e}");
        }
    }

    /// <summary>
    /// Builds and attaches the probe session before a run exists (Session is static,
    /// so no Wave instance is required). Used together with Core.Wave.StartNewGame's
    /// own isMultiplayer parameter instead of flipping IsMultiplayer mid-run.
    /// </summary>
    public static bool AttachForMultiplayerStart(out string kingName)
    {
        kingName = null;
        try
        {
            // Start from a fresh year-0 board, not the captured wave-15 run.
            Gameplay.GameSnapshot snapshot;
            try
            {
                snapshot = BuildFreshSnapshot();
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"attach-pre: fresh snapshot failed: {e.Message}");
                return false;
            }

            ReadChoices(out var chosenId, out _);
            var owner = chosenId >= 0 ? (Core.CardOwnership)chosenId : Core.CardOwnership.KingOfNothing;
            ApplyChoices(ref owner);
            kingName = owner.ToString();

            var opponentKing = owner == Core.CardOwnership.KingOfSpells
                ? Core.CardOwnership.KingOfNothing
                : Core.CardOwnership.KingOfSpells;

            // GameplayUI_UserFace.Setup dereferences run.player.settings. Without it
            // PopulateEnemyFaces throws and the avatar bar never renders.
            var localProfile = new PlayerProfileData
            {
                playerId = 900001,
                username = "Local probe",
                settings = PlayerSettingsData.CreateLocalSnapshot(),
            };
            var enemyProfile = new PlayerProfileData
            {
                playerId = 900003,
                username = "Local probe opponent",
                completedMissions = new Il2CppSystem.Collections.Generic.List<int>(),
                unlockedItems = new Il2CppSystem.Collections.Generic.List<int>(),
                settings = PlayerSettingsData.CreateLocalSnapshot(),
            };

            var localSnapshots = new MultiplayerGameSnapshots();
            localSnapshots.Add(snapshot);
            localSnapshots.FinalSnapshot = snapshot;
            var enemySnapshots = new MultiplayerGameSnapshots();
            enemySnapshots.Add(snapshot);
            enemySnapshots.FinalSnapshot = snapshot;

            // Register a per-wave snapshot series so GetWaveSnapshot resolves at any
            // wave the match reaches. The official session carries one snapshot per
            // year; without a long enough series SetWave stalls when the game advances.
            const int SeriesWaves = 64;
            for (var wave = snapshot.WaveData.WaveNumber + 1; wave <= SeriesWaves; wave++)
            {
                var waveSnapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(snapshot));
                if (waveSnapshot?.WaveData == null)
                {
                    continue;
                }

                waveSnapshot.WaveData.WaveNumber = wave;
                waveSnapshot.WaveData.IsMultiplayer = false;
                localSnapshots.Add(waveSnapshot);
                enemySnapshots.Add(waveSnapshot);
                localSnapshots.FinalSnapshot = waveSnapshot;
                enemySnapshots.FinalSnapshot = waveSnapshot;
            }

            VersusPlugin.Log.LogInfo($"attach-pre: snapshot series waves=0..{SeriesWaves}");

            var localRun = new MultiplayerRunData
            {
                runId = 900001,
                playerId = localProfile.playerId,
                kingId = (int)owner,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = localSnapshots,
                player = localProfile,
            };
            var enemyRun = new MultiplayerRunData
            {
                runId = 900003,
                playerId = enemyProfile.playerId,
                kingId = (int)opponentKing,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = enemySnapshots,
                player = enemyProfile,
            };

            var allRuns = new System.Collections.Generic.List<MultiplayerRunData> { localRun, enemyRun };
            // Only the nine real kings: PopulateEnemyFaces calls UserFace.Setup on
            // every session run and fake owners (No_King, All_Kings, Joker...) have
            // no KingCollectionEntry, which throws and kills the avatar bar.
            foreach (Core.CardOwnership king in new[]
            {
                Core.CardOwnership.KingOfNothing, Core.CardOwnership.KingOfSpells,
                Core.CardOwnership.KingOfBlood, Core.CardOwnership.KingOfGreed,
                Core.CardOwnership.KingOfProgress, Core.CardOwnership.KingOfStone,
                Core.CardOwnership.KingOfNomads, Core.CardOwnership.KingOfNature,
                Core.CardOwnership.KingOfTime,
            })
            {
                if (king == owner || king == opponentKing)
                {
                    continue;
                }

                allRuns.Add(new MultiplayerRunData
                {
                    runId = 900100 + (int)king,
                    playerId = 900100 + (int)king,
                    kingId = (int)king,
                    yearPlayed = snapshot.WaveData.WaveNumber,
                    currentHP = snapshot.WaveData.PlayerLifes,
                    gameVersion = snapshot.Version,
                    snapshot = enemySnapshots,
                    player = enemyProfile,
                });
            }

            var runs = new Il2CppReferenceArray<MultiplayerRunData>(allRuns.Count);
            for (var i = 0; i < allRuns.Count; i++)
            {
                runs[i] = allRuns[i];
            }

            var session = new MultiplayerSession(runs,
                new Il2CppStringArray(0), new Il2CppStringArray(0),
                new Il2CppStringArray(0), new Il2CppStringArray(0));

            // Every run except the local player is a gauntlet opponent. The game
            // rotates through them with RefreshWaveKing each wave; hard-coding one
            // enemy meant the same king every round.
            var gauntlet = new Il2CppSystem.Collections.Generic.List<MultiplayerRunData>();
            foreach (var run in allRuns)
            {
                if (run.runId != localRun.runId)
                {
                    gauntlet.Add(run);
                }
            }

            VersusPlugin.Log.LogInfo($"attach-pre: gauntlet populated with {gauntlet.Count} opposing kings");
            typeof(MultiplayerSession).GetProperty("GauntletEnemies",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(session, gauntlet);
            // Leave GauntletActive false: PrepareMultiplayerGauntlet early-returns when
            // it is already true, so RefreshWaveKing would never run and the same king
            // would be fought every round. The game sets it itself when it populates.
            typeof(MultiplayerSession).GetProperty("GauntletActive",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(session, false);

            var previous = MultiplayerManager.Session?.Pointer ?? IntPtr.Zero;
            MultiplayerManager.Session = session;
            var attached = (MultiplayerManager.Session?.Pointer ?? IntPtr.Zero) == session.Pointer;

            // Apply the player's chosen perk loadout to the session snapshot store.
            ApplyChosenPerks();

            // Keep the hub manager alive across the Gameplay scene load: StartWave's
            // multiplayer branch may dereference it, and the hub scene is unloaded when
            // the run scene loads.
            var manager = MultiplayerManager.Instance;
            if (manager != null && manager.gameObject != null)
            {
                UnityEngine.Object.DontDestroyOnLoad(manager.gameObject);
                VersusPlugin.Log.LogInfo($"attach-pre: MultiplayerManager marked DontDestroyOnLoad (scene={manager.gameObject.scene.name})");
            }

            VersusPlugin.Log.LogInfo(
                $"attach-pre: previous=0x{previous.ToInt64():X} new=0x{session.Pointer.ToInt64():X} attached={attached} " +
                $"gauntlet={session.GauntletEnemies?.Count ?? -1} active={session.GauntletActive} king={kingName} enemyKing={opponentKing}");
            return attached;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"attach-pre failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Invokes Core.Wave.StartNewGame (the 11-parameter overload that takes
    /// isMultiplayer) through reflection, because the interop signature uses
    /// Nullable<int> and IReadOnlyList types that are awkward to construct directly.
    /// </summary>
    public static void StartMultiplayerRun(string kingName)
    {
        try
        {
            var method = typeof(Core.Wave).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "StartNewGame" && m.GetParameters().Length == 11);
            if (method == null)
            {
                VersusPlugin.Log.LogWarning("start-mp: 11-parameter StartNewGame not found");
                return;
            }

            var parameters = method.GetParameters();
            var difficulty = Difficulty.DifficultySO.Instance?.GetSelectedDifficulty();
            var runDefinition = Core.RunDefinition.Default;

            // Seed the manager's season caches before a multiplayer start: the probe run
            // showed they are all null offline, which is a plausible NRE source inside
            // the isMultiplayer branch.
            if (MultiplayerSystems.MultiplayerManager.SeasonCardPoolCache == null)
            {
                var empty = new Il2CppStringArray(0);
                MultiplayerSystems.MultiplayerManager.SeasonCardPoolCache = empty;
                MultiplayerSystems.MultiplayerManager.SeasonCardFallbacksCache = empty;
                MultiplayerSystems.MultiplayerManager.SeasonDecreePoolCache = empty;
                MultiplayerSystems.MultiplayerManager.SeasonDecreeFallbacksCache = empty;
                VersusPlugin.Log.LogInfo("start-mp: season caches seeded (empty)");
            }

            VersusPlugin.Log.LogInfo(
                $"start-mp: difficulty={(difficulty == null ? "null" : difficulty.name)} king={kingName} " +
                $"runDefinition={(runDefinition == null ? "null" : "default")} managerInstance={(MultiplayerSystems.MultiplayerManager.Instance == null ? "null" : "alive")}");

            // isMultiplayer=true dereferences the hub manager (now alive) and requires
            // real IReadOnlyList instances; use Il2CppInterop's Cast to satisfy the
            // Il2CppSystem.Collections.Generic.IReadOnlyList<string> parameters.
            var emptyList = new Il2CppSystem.Collections.Generic.List<string>();
            var emptyReadOnly = emptyList.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>();

            var candidates = new (string scene, bool multiplayer)[]
            {
                ("Gameplay", true),
                ("", true),
                ("Gameplay", false),
            };

            foreach (var candidate in candidates)
            {
                var args = new object[parameters.Length];
                args[0] = kingName;              // playerKing
                args[1] = difficulty;            // difficulty
                args[2] = candidate.scene;       // sceneName
                args[3] = emptyReadOnly;         // customEnemies
                args[4] = new Il2CppSystem.Nullable<int>(12345); // null NREs in the wrapper
                args[5] = true;                  // instaLoad
                args[6] = emptyReadOnly;         // disabledKings
                args[7] = candidate.multiplayer; // isMultiplayer
                args[8] = false;                 // registerRunStart (offline profile untouched)
                args[9] = runDefinition;         // runDefinition
                args[10] = false;                // isQuest

                try
                {
                    method.Invoke(null, args);
                    VersusPlugin.Log.LogInfo($"start-mp: invoked OK scene='{candidate.scene}' isMultiplayer={candidate.multiplayer} registerRunStart=false");
                    return;
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    VersusPlugin.Log.LogWarning($"start-mp: scene='{candidate.scene}' isMultiplayer={candidate.multiplayer} failed: {inner.GetType().Name}: {inner.Message}");
                }
            }

            VersusPlugin.Log.LogError("start-mp: all argument variants failed");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"start-mp failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    private static readonly System.Collections.Generic.List<object> NativeMenuDelegates = new System.Collections.Generic.List<object>();

    /// <summary>
    /// Repurposes the game's native PauseMenu prefab into a versus control panel.
    /// Same buttons, sprites, font and input handling as the real game.
    /// </summary>
    public static void ShowNativeVersusMenu()
    {
        try
        {
            var pause = GameplayInterface.GameplayUI.PauseMenu;
            if (pause == null)
            {
                VersusPlugin.Log.LogWarning("native-versus: PauseMenu missing");
                return;
            }

            var buttons = pause.gameObject.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            if (buttons == null)
            {
                VersusPlugin.Log.LogWarning("native-versus: no buttons");
                return;
            }

            NativeMenuDelegates.Clear();
            var labels = new System.Collections.Generic.Dictionary<string, string>
            {
                { "ResumeBtn", "CLOSE" },
                { "PerksBtn", "HOST" },
                { "CompendiumBtn", "JOIN" },
                { "OptionsBtn", "START BATTLE" },
                { "ForfeitBtn", "REBUILD BOARD" },
            };

            var actions = new System.Collections.Generic.Dictionary<string, System.Action>
            {
                { "ResumeBtn", () => GameplayInterface.GameplayUI.ShowNextUI(0.3f) },
                { "PerksBtn", () => SetNetMode("host") },
                { "CompendiumBtn", () => SetNetMode("join") },
                { "OptionsBtn", () => VersusPlugin.Log.LogInfo($"native-versus: {VersusBehaviour.MenuStartBattle()}") },
                { "ForfeitBtn", () => VersusPlugin.Log.LogInfo($"native-versus: {VersusBehaviour.MenuBuildBoard()}") },
            };

            var changed = 0;
            foreach (var button in buttons)
            {
                if (button == null || !labels.TryGetValue(button.name, out var label))
                {
                    continue;
                }

                var text = button.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (text != null)
                {
                    text.text = label;
                }

                button.onClick.RemoveAllListeners();
                var action = actions[button.name];
                var listener = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new System.Action(() => action()));
                NativeMenuDelegates.Add(listener);
                button.onClick.AddListener(listener);
                changed++;
            }

            GameplayInterface.GameplayUI.ShowSingleUI(pause);
            VersusPlugin.Log.LogInfo($"native-versus: repurposed {changed} native buttons and showed PauseMenu");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"native-versus failed: {e.Message}");
        }
    }

    private static void SetNetMode(string mode)
    {
        var path = Path.Combine(VersusPlugin.DataDir, "net.json");
        var peer = "127.0.0.1";
        var port = 28764;
        if (File.Exists(path))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("peer", out var peerProp))
                {
                    peer = peerProp.GetString() ?? peer;
                }

                if (root.TryGetProperty("port", out var portProp))
                {
                    port = portProp.GetInt32();
                }
            }
            catch
            {
            }
        }

        File.WriteAllText(path, $"{{\"mode\":\"{mode}\",\"peer\":\"{peer}\",\"port\":{port}}}");
        VersusPlugin.Log.LogInfo($"native-versus: net mode set to {mode}");
    }

    /// <summary>
    /// Logs the native PauseMenu buttons so they can be repurposed for versus.
    /// </summary>
    public static void ProbePauseMenu()
    {
        try
        {
            var pause = GameplayInterface.GameplayUI.PauseMenu;
            if (pause == null)
            {
                VersusPlugin.Log.LogWarning("pausemenu: missing");
                return;
            }

            var buttons = pause.gameObject.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            VersusPlugin.Log.LogInfo($"pausemenu: buttons={buttons?.Length ?? 0}");
            if (buttons == null)
            {
                return;
            }

            foreach (var button in buttons)
            {
                if (button == null)
                {
                    continue;
                }

                var text = button.GetComponentInChildren<TMPro.TMP_Text>(true);
                VersusPlugin.Log.LogInfo(
                    $"pausemenu: button '{button.name}' text='{(text == null ? "" : text.text)}'");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"pausemenu probe failed: {e.Message}");
        }
    }

    /// <summary>
    /// Dumps the live MultiplayerScreen's serialized fields, then tries the real
    /// PopulateEnemyFaces so we can see which field is null.
    /// </summary>
    public static void ProbeMpUi()
    {
        try
        {
            var screen = GameplayInterface.GameplayUI.MultiplayerScreen;
            if (screen == null)
            {
                VersusPlugin.Log.LogWarning("mpui: screen missing");
                return;
            }

            var offsets = new System.Collections.Generic.Dictionary<string, int>
            {
                { "m_Holder", 0x30 },
                { "m_UserFaceTemplate", 0x38 },
                { "m_EnemyKingdomUI", 0x60 },
                { "m_Faces", 0x90 },
                { "m_PlayerNameByPlayerId", 0x98 },
                { "m_EnemyKingByPlayerId", 0xA0 },
                { "m_RunDataByPlayerId", 0xA8 },
                { "m_HolderRectTransform", 0xB8 },
                { "m_HolderLayoutGroup", 0xC8 },
                { "m_OnFaceClicked", 0x100 },
            };
            foreach (var pair in offsets)
            {
                var value = Marshal.ReadIntPtr(screen.Pointer, pair.Value);
                VersusPlugin.Log.LogInfo($"mpui: {pair.Key} @ +0x{pair.Value:X} = 0x{value.ToInt64():X}");
            }

            var session = MultiplayerSystems.MultiplayerManager.Session;
            if (session?.Data == null)
            {
                VersusPlugin.Log.LogWarning("mpui: no session");
                return;
            }

            System.Action<MultiplayerSystems.MultiplayerRunData> clicked = run => { };
            System.Func<bool> canInteract = () => true;
            var valid = new Il2CppReferenceArray<MultiplayerSystems.MultiplayerRunData>(session.Data.Length);
            var count = 0;
            for (var i = 0; i < session.Data.Length; i++)
            {
                var run = session.Data[i];
                if (run != null &&
                    MultiplayerSystems.MultiplayerManager.TryResolveKing(run.kingId, out var king) && king != null)
                {
                    valid[count++] = run;
                }
            }

            var filtered = new Il2CppReferenceArray<MultiplayerSystems.MultiplayerRunData>(count);
            for (var i = 0; i < count; i++)
            {
                filtered[i] = valid[i];
            }

            try
            {
                screen.PopulateEnemyFaces(filtered, clicked, canInteract);
                VersusPlugin.Log.LogInfo($"mpui: PopulateEnemyFaces OK with {count}/{session.Data.Length} runs");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"mpui: PopulateEnemyFaces failed: {(e.InnerException ?? e).Message}");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"mpui probe failed: {e.Message}");
        }
    }

    /// <summary>
    /// Shows the game's own multiplayer UI subcontroller through GameplayUI's
    /// native panel stack instead of a hand-built overlay.
    /// </summary>
    public static void ShowNativeMenu()
    {
        try
        {
            var screen = GameplayInterface.GameplayUI.MultiplayerScreen;
            if (screen == null)
            {
                VersusPlugin.Log.LogWarning("native-menu: MultiplayerScreen missing");
                return;
            }

            GameplayInterface.GameplayUI.ShowSingleUI(screen);
            VersusPlugin.Log.LogInfo("native-menu: ShowSingleUI(MultiplayerScreen)");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"native-menu failed: {e.Message}");
        }
    }

    /// <summary>
    /// Right-side opponent bar using the game's own king face sprites.
    /// </summary>
    public static void BuildAvatarBar()
    {
        try
        {
            var session = MultiplayerSystems.MultiplayerManager.Session;
            if (session?.Data == null)
            {
                VersusPlugin.Log.LogWarning("avatar: no session runs");
                return;
            }

            var root = new GameObject("VersusAvatarBar");
            root.AddComponent<RectTransform>();
            var canvas = root.AddComponent<UnityEngine.Canvas>();
            canvas.renderMode = UnityEngine.RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 850;
            root.AddComponent<UnityEngine.UI.CanvasScaler>();
            UnityEngine.Object.DontDestroyOnLoad(root);

            var panel = new GameObject("AvatarPanel");
            panel.AddComponent<RectTransform>();
            panel.transform.SetParent(root.transform, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(-12f, 0f);
            rect.sizeDelta = new Vector2(180f, 46f + session.Data.Length * 46f);
            var image = panel.AddComponent<UnityEngine.UI.Image>();
            image.color = new Color(0.04f, 0.05f, 0.08f, 0.85f);

            var fontSource = UnityEngine.Object.FindObjectOfType<TMPro.TMP_Text>();
            var font = fontSource != null ? fontSource.font : null;
            var localOwner = Core.Wave.PlayerKing?.Owner ?? Core.CardOwnership.KingOfNothing;

            var index = 0;
            for (var i = 0; i < session.Data.Length; i++)
            {
                var run = session.Data[i];
                if (run == null || (Core.CardOwnership)run.kingId == localOwner)
                {
                    continue;
                }

                var row = new GameObject("AvatarRow");
                row.AddComponent<RectTransform>();
                row.transform.SetParent(panel.transform, false);
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(0f, 1f);
                rowRect.pivot = new Vector2(0f, 1f);
                rowRect.anchoredPosition = new Vector2(8f, -8f - index * 46f);
                rowRect.sizeDelta = new Vector2(164f, 40f);

                var faceGo = new GameObject("Face");
                faceGo.AddComponent<RectTransform>();
                faceGo.transform.SetParent(row.transform, false);
                var faceRect = faceGo.GetComponent<RectTransform>();
                faceRect.anchorMin = Vector2.zero;
                faceRect.anchorMax = Vector2.zero;
                faceRect.pivot = Vector2.zero;
                faceRect.sizeDelta = new Vector2(36f, 36f);
                var faceImage = faceGo.AddComponent<UnityEngine.UI.Image>();
                faceImage.color = Color.white;
                if (MultiplayerSystems.MultiplayerManager.TryResolveKing(run.kingId, out var king) &&
                    king != null && king.Face != null)
                {
                    faceImage.sprite = king.Face;
                }

                var labelGo = new GameObject("Name");
                labelGo.AddComponent<RectTransform>();
                labelGo.transform.SetParent(row.transform, false);
                var labelRect = labelGo.GetComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0f, 0f);
                labelRect.anchorMax = new Vector2(0f, 0f);
                labelRect.pivot = new Vector2(0f, 0f);
                labelRect.anchoredPosition = new Vector2(42f, 4f);
                labelRect.sizeDelta = new Vector2(118f, 32f);
                var label = labelGo.AddComponent<TMPro.TextMeshProUGUI>();
                if (font != null)
                {
                    label.font = font;
                }

                label.fontSize = 15;
                label.text = string.IsNullOrEmpty(run.player?.username)
                    ? $"Bot {run.kingId}"
                    : run.player.username;
                label.color = new Color(0.92f, 0.92f, 1f, 1f);
                index++;
            }

            VersusPlugin.Log.LogInfo($"avatar: built right-side bar with {index} faces");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"avatar failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Populates the official right-side avatar bar with the runs in our LAN session.
    /// </summary>
    public static void PopulateFaces()
    {
        try
        {
            var screen = GameplayInterface.GameplayUI.MultiplayerScreen;
            var runs = MultiplayerSystems.MultiplayerManager.Session?.Data;
            if (screen == null || runs == null)
            {
                VersusPlugin.Log.LogWarning("faces: screen or session runs missing");
                return;
            }

            System.Action<MultiplayerSystems.MultiplayerRunData> onFaceClicked =
                run => VersusPlugin.Log.LogInfo($"face clicked: player={run?.playerId}");
            System.Func<bool> canInteract = () => true;

            var two = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MultiplayerSystems.MultiplayerRunData>(2);
            two[0] = runs[0];
            two[1] = runs[1];

            screen.Show();
            screen.PopulateEnemyFaces(two, onFaceClicked, canInteract);
            screen.UpdateFaces();
            VersusPlugin.Log.LogInfo($"faces: populated {runs.Length} opponent faces");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"faces failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Builds several opponent base previews by invoking Show per bot and cloning
    /// each generated root before the next call replaces it.
    /// </summary>
    public static void ShowMultiplePreviews()
    {
        try
        {
            var manager = UnityEngine.Object.FindObjectOfType<Gameplay.GameplayMultiplayerManager>();
            var previewField = typeof(Gameplay.GameplayMultiplayerManager).GetProperty("m_MapPreview",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var preview = previewField?.GetValue(manager);
            var session = MultiplayerSystems.MultiplayerManager.Session;
            if (preview == null || session?.Data == null)
            {
                VersusPlugin.Log.LogWarning("multi-preview: missing preview or session");
                return;
            }

            var show = preview.GetType().GetMethod("Show",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var currentProp = preview.GetType().GetProperty("m_CurrentPreview",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (show == null || currentProp == null)
            {
                VersusPlugin.Log.LogWarning("multi-preview: Show or m_CurrentPreview missing");
                return;
            }

            var wave = Core.Wave.Data?.Wave ?? -1;
            var clones = new UnityEngine.GameObject[0];
            var shown = 0;
            for (var i = 0; i < session.Data.Length; i++)
            {
                var run = session.Data[i];
                var snapshot = run?.snapshot?.GetWaveSnapshot(wave, true) ?? run?.snapshot?.FinalSnapshot;
                if (snapshot == null || !MultiplayerSystems.MultiplayerManager.TryResolveKing(run.kingId, out var king) || king == null)
                {
                    continue;
                }

                var result = show.Invoke(preview, new object[] { king, snapshot, false });
                if (result is bool ok && !ok)
                {
                    continue;
                }

                var root = currentProp.GetValue(preview) as UnityEngine.GameObject;
                if (root == null)
                {
                    continue;
                }

                var clone = UnityEngine.Object.Instantiate(root);
                clone.name = $"VersusBotBase_{run.kingId}";
                clone.transform.position = root.transform.position + new UnityEngine.Vector3(shown * 22f, 0f, 0f);
                System.Array.Resize(ref clones, clones.Length + 1);
                clones[clones.Length - 1] = clone;
                shown++;
            }

            VersusPlugin.Log.LogInfo($"multi-preview: built {shown} opponent base previews");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"multi-preview failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// Uses the game's multiplayer map-preview renderer to build a visual enemy
    /// base from the opponent snapshot. This is the missing base presentation.
    /// </summary>
    public static void ShowOpponentPreview()
    {
        try
        {
            var manager = UnityEngine.Object.FindObjectOfType<Gameplay.GameplayMultiplayerManager>();
            if (manager == null)
            {
                VersusPlugin.Log.LogWarning("preview: no GameplayMultiplayerManager");
                return;
            }

            var field = typeof(Gameplay.GameplayMultiplayerManager).GetProperty("m_MapPreview",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var preview = field?.GetValue(manager);
            if (preview == null)
            {
                VersusPlugin.Log.LogWarning("preview: no m_MapPreview property");
                return;
            }

            var show = preview.GetType().GetMethod("Show",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (show == null)
            {
                VersusPlugin.Log.LogWarning("preview: Show not found");
                return;
            }

            var king = Core.Wave.WaveKing?.Scriptable;
            var snapshot = GetOpponentSnapshot();
            if (king == null || snapshot == null)
            {
                VersusPlugin.Log.LogWarning("preview: missing king or snapshot");
                return;
            }

            var result = show.Invoke(preview, new object[] { king, snapshot, false });
            VersusPlugin.Log.LogInfo($"preview: Show(king={king.name}) returned {result}");

            try
            {
                var enter = typeof(Gameplay.GameplayMultiplayerManager).GetMethod("EnterEnemyMapView",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var session = MultiplayerSystems.MultiplayerManager.Session;
                var run = session?.GetDataByPlayerKing(king.Owner);
                if (enter != null && run != null)
                {
                    enter.Invoke(manager, new object[] { run, king, snapshot });
                    VersusPlugin.Log.LogInfo("preview: EnterEnemyMapView invoked");
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"preview EnterEnemyMapView failed: {e.Message}");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"preview failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// Returns the best available opponent snapshot: live LAN state first, then
    /// the attached session's gauntlet run, then the injected static snapshot.
    /// </summary>
    public static Gameplay.GameSnapshot GetOpponentSnapshot()
    {
        try
        {
            if (!string.IsNullOrEmpty(VersusNet.OpponentSnapshotJson))
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(VersusNet.OpponentSnapshotJson);
            }

            var session = MultiplayerManager.Session;
            var run = session?.GauntletEnemies != null && session.GauntletEnemies.Count > 0
                ? session.GauntletEnemies[0]
                : null;
            var wave = Core.Wave.Data?.Wave ?? -1;
            return run?.snapshot?.GetWaveSnapshot(wave, true)
                ?? run?.snapshot?.FinalSnapshot
                ?? Core.Wave.CurrentMultiplayerEnemySnapshot;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"opponent snapshot failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recreates the opponent's board on the multiplayer side using the game's own
    /// SetMultiplayerCard/SetMultiplayerLife console commands, one command per placed
    /// card in the opponent snapshot. This is the missing step that makes the enemy
    /// side a real kingdom instead of an empty faux board.
    /// </summary>
    public static void BuildEnemyBoard()
    {
        try
        {
            var data = Core.Wave.Data;
            if (data == null)
            {
                VersusPlugin.Log.LogWarning("build-enemy: no run data");
                return;
            }

            var session = MultiplayerManager.Session;
            var enemyRun = session?.GauntletEnemies != null && session.GauntletEnemies.Count > 0
                ? session.GauntletEnemies[0]
                : null;
            var snapshot = (!string.IsNullOrEmpty(VersusNet.OpponentSnapshotJson)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(VersusNet.OpponentSnapshotJson)
                    : null)
                ?? enemyRun?.snapshot?.GetWaveSnapshot(data.Wave, true)
                ?? enemyRun?.snapshot?.FinalSnapshot
                ?? Core.Wave.CurrentMultiplayerEnemySnapshot;
            if (snapshot?.Areas == null)
            {
                VersusPlugin.Log.LogWarning("build-enemy: no opponent snapshot areas");
                return;
            }

            var map = Core.Wave.MultiplayerMap;
            if (map != null && !map.activeSelf)
            {
                map.SetActive(true);
                VersusPlugin.Log.LogInfo("build-enemy: MultiplayerMap activated");
            }

            // The run stays single-player for playability, so the game never calls
            // RefreshMultiplayerMap itself. Invoke it directly so the opponent's
            // base/castle/walls are built from their snapshot.
            try
            {
                Core.Wave.CurrentMultiplayerEnemySnapshot = snapshot;
                var refresh = typeof(Core.Wave).GetMethod("RefreshMultiplayerMap",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                refresh?.Invoke(null, null);
                VersusPlugin.Log.LogInfo("build-enemy: RefreshMultiplayerMap invoked");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"build-enemy: RefreshMultiplayerMap failed: {e.Message}");
            }

            VersusPlugin.Log.LogInfo(
                $"build-enemy: wave={snapshot.WaveData?.WaveNumber} king={snapshot.Kings?.PlayerKing} " +
                $"areas={snapshot.Areas.Count} map={(map == null ? "null" : map.activeSelf ? "active" : "inactive")}");

            var placed = 0;
            foreach (var area in snapshot.Areas)
            {
                if (area == null || string.IsNullOrEmpty(area.PlacedCardName))
                {
                    continue;
                }

                var input = $"SetMultiplayerCard {area.XCoordinate} {area.YCoordinate} {area.PlacedCardName}";
                if (VersusBehaviour.RunCommand(input))
                {
                    placed++;
                }
            }

            var king = snapshot.Kings?.PlayerKing ?? "Spells";
            VersusBehaviour.RunCommand($"SetMultiplayerLife {king} {Math.Max(1, snapshot.WaveData?.PlayerLifes ?? 1)}");

            VersusPlugin.Log.LogInfo(
                $"build-enemy: placed={placed} enemyTroops={Core.Kingdoms.KingdomAreaManager.GetAreasWithTroops(true)?.Count ?? -1} " +
                $"enemyAnything={Core.Kingdoms.KingdomAreaManager.GetAreasWithAnythingPlaced(true)?.Count ?? -1} " +
                $"map={(Core.Wave.MultiplayerMap == null ? "null" : Core.Wave.MultiplayerMap.activeSelf ? "active" : "inactive")}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"build-enemy failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Probe the enemy-spawn API with troops resolved from a snapshot. Goal of the
    /// arena milestone: spawn the opponent's army into a single scene.
    /// </summary>
    public static void SpawnProbe()
    {
        try
        {
            var wave = Core.Wave.Instance;
            if (wave == null)
            {
                VersusPlugin.Log.LogWarning("spawn-probe: no wave instance");
                return;
            }

            var files = Directory.GetFiles(VersusPlugin.DataDir, "*-f7-capture-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                VersusPlugin.Log.LogWarning("spawn-probe: no snapshot");
                return;
            }

            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(files[^1]));
            var types = new Il2CppSystem.Collections.Generic.List<Core.Itens.TroopSo>();
            if (snapshot?.Areas != null)
            {
                foreach (var area in snapshot.Areas)
                {
                    if (area == null || string.IsNullOrEmpty(area.PlacedCardName) || area.PlacedEntityType != "Troop")
                    {
                        continue;
                    }

                    var troop = Core.Game.GetTroop(area.PlacedCardName);
                    if (troop != null)
                    {
                        types.Add(troop);
                        VersusPlugin.Log.LogInfo($"spawn-probe: resolved '{area.PlacedCardName}' quantity={area.TroopQuantity}");
                    }
                    else
                    {
                        VersusPlugin.Log.LogWarning($"spawn-probe: unresolved troop '{area.PlacedCardName}'");
                    }
                }
            }

            VersusPlugin.Log.LogInfo($"spawn-probe: resolvedTypes={types.Count}");

            var method = typeof(Core.Wave).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "CreateEnemies");
            if (method == null)
            {
                VersusPlugin.Log.LogWarning("spawn-probe: CreateEnemies not found");
                return;
            }

            foreach (var p in method.GetParameters())
            {
                VersusPlugin.Log.LogInfo($"spawn-probe: param {p.Position} {p.ParameterType.FullName} {p.Name}");
            }

            var enemies = new Il2CppSystem.Collections.Generic.List<Core.Entities.Troop>();
            var baseGo = Core.Wave.MultiplayerEnemyBase;
            var position = baseGo == null ? UnityEngine.Vector2.zero : new UnityEngine.Vector2(baseGo.transform.position.x, baseGo.transform.position.y);
            var args = new object[] { position, null, types, enemies };
            var before = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>()?.Length ?? 0;
            if (method.IsStatic)
            {
                method.Invoke(null, args);
            }
            else
            {
                method.Invoke(wave, args);
            }

            var after = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>()?.Length ?? 0;
            VersusPlugin.Log.LogInfo($"spawn-probe: CreateEnemies invoked; entities {before} -> {after}, enemies={enemies.Count}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"spawn-probe failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// One-shot reflection dump of the enemy-spawn API so we can feed the opponent's
    /// troop composition through the game's own spawning path.
    /// </summary>
    public static void InspectSpawnApi()
    {
        try
        {
            foreach (var name in new[] { "SpawnBattlers", "CreateEnemies", "SetupEnemy", "GetTroop", "GetEnemyCounts" })
            {
                foreach (var method in typeof(Core.Wave).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                {
                    if (method.Name != name)
                    {
                        continue;
                    }

                    var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    VersusPlugin.Log.LogInfo($"spawn-api: {method.DeclaringType?.Name}.{name}({parameters}) -> {method.ReturnType.Name}");
                }
            }

            foreach (var name in new[] { "WaveTroops", "ValidTroops", "Enemies", "EnemyRangedTroops", "EnemyMeleeTroops" })
            {
                var property = typeof(Core.Wave).GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                VersusPlugin.Log.LogInfo($"spawn-api: property {name} writable={property?.CanWrite} type={property?.PropertyType.Name}");
            }

            var enemyFlag = typeof(Core.Entities.Entity).GetProperty("IsMultiplayerEnemy",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            VersusPlugin.Log.LogInfo($"spawn-api: Entity.IsMultiplayerEnemy writable={enemyFlag?.CanWrite}");

            var troops = Core.Wave.WaveTroops;
            VersusPlugin.Log.LogInfo($"spawn-api: WaveTroops count={troops?.Length ?? -1} first={(troops != null && troops.Length > 0 ? troops[0]?.name : "none")}");

            var resolved = Core.Game.GetTroop("SuperSoldier");
            VersusPlugin.Log.LogInfo($"spawn-api: Game.GetTroop('SuperSoldier')={(resolved == null ? "null" : resolved.name)}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"spawn-api inspection failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string FreshKingName(Core.CardOwnership owner)
    {
        var name = owner.ToString();
        if (name.StartsWith("KingOf"))
        {
            return name.Substring(6);
        }

        if (name == "JackOfChaos")
        {
            return "Chaos";
        }

        return name;
    }

    /// <summary>
    /// Builds a genuine fresh run snapshot (empty 25-area board, one level-1
    /// Citadel, 3 lives, starting hand, chosen king) from the latest captured
    /// snapshot. The game has no offline StartNewGame path, so this is how a
    /// match begins at year 0 instead of inheriting a built-up wave-15 board.
    /// </summary>
    public static Gameplay.GameSnapshot BuildFreshSnapshot()
    {
        var files = Directory.GetFiles(VersusPlugin.DataDir, "*-f7-capture-*.json");
        if (files.Length == 0)
        {
            files = Directory.GetFiles(VersusPlugin.DataDir, "*-capture-*.json");
        }

        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            throw new InvalidOperationException("no capture available to build a fresh snapshot");
        }

        var baseSnapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(files[^1]));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(baseSnapshot);
        var fresh = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(json);
        if (fresh?.WaveData == null || fresh.Areas == null || fresh.Kings == null)
        {
            throw new InvalidOperationException("captured snapshot lacks wave/king/area data");
        }

        ReadChoices(out var kingId, out _);
        var owner = kingId >= 0 ? (Core.CardOwnership)kingId : Core.CardOwnership.KingOfNothing;
        var enemy = owner == Core.CardOwnership.KingOfSpells
            ? Core.CardOwnership.KingOfNothing
            : Core.CardOwnership.KingOfSpells;

        fresh.WaveData.WaveNumber = 0;
        // Official multiplayer starts with 5 lives, not the 3 of a normal run.
        fresh.WaveData.PlayerLifes = 5;
        // Keep the run's own wave logic single-player: IsMultiplayer=true makes the
        // game expect a server-driven gauntlet and breaks card play, rewards and the
        // shop in an attached offline session. The opponent is injected separately
        // via SetMultiplayerCard/SetMultiplayerLife (the path that actually plays).
        fresh.WaveData.IsMultiplayer = false;
        fresh.WaveData.UnitsKilled = 0;
        fresh.WaveData.UnitsTrained = 0;

        fresh.Kings.PlayerKing = FreshKingName(owner);
        // A match starts with three active enemy kings. They rotate between battles
        // and a new one is chosen when one is defeated (SwitchEnemyKings / Diplomat).
        fresh.Kings.EnemyKings.Clear();
        fresh.Kings.EnemyKings.Add(FreshKingName(enemy));
        foreach (var extra in new[]
        {
            Core.CardOwnership.KingOfSpells, Core.CardOwnership.KingOfNomads,
            Core.CardOwnership.KingOfStone, Core.CardOwnership.KingOfNature,
            Core.CardOwnership.KingOfBlood, Core.CardOwnership.KingOfGreed,
        })
        {
            if (fresh.Kings.EnemyKings.Count >= 3)
            {
                break;
            }

            if (extra != owner && extra != enemy)
            {
                fresh.Kings.EnemyKings.Add(FreshKingName(extra));
            }
        }

        fresh.Kings.DisabledKings.Clear();

        foreach (var area in fresh.Areas)
        {
            var isCastle = area.PlacedEntityType == "Castle";
            // A fresh kingdom has only the standard 3x3 plots unlocked around the
            // Castle at (-1,1); the wave-15 capture had all 25 unlocked.
            area.IsUnlocked = area.XCoordinate >= -1 && area.XCoordinate <= 1 &&
                              area.YCoordinate >= -1 && area.YCoordinate <= 1;
            area.ForceLocked = false;
            area.PlotHoleEnabled = false;
            area.IsTreasureEnabled = false;
            area.ShownLevelOffset = 0;
            area.MaxCardLevel = 3;
            area.TroopQuantity = 0;
            area.EntitySize = 1f;
            area.TotalDamage = 0d;
            area.Spells?.Clear();
            area.CustomProperties?.Clear();
            area.Stats ??= new Gameplay.GameSnapshot.StatsSnapshot();

            if (isCastle)
            {
                area.PlacedCardName = "Citadel";
                area.PlacedEntityType = "Castle";
                area.CardLevel = 1;
                area.Stats.MaxHP = 250d;
                area.Stats.Strength = 15d;
                area.Stats.WalkSpeed = 0d;
                area.Stats.AttackSpeed = 0.6666666666666666d;
                area.Stats.StunDuration = 0d;
                area.Stats.StunDurationBoost = 0d;
                area.Stats.SecondaryAttackSpeed = 0d;
                area.Stats.SecondaryAttackSpeedBoost = 0d;
                area.Stats.CritMultiplier = 2d;
                area.Stats.CritChance = 0d;
                area.Stats.Projectiles = 0;
                area.Stats.EffectStacks = 1;
            }
            else
            {
                area.PlacedCardName = string.Empty;
                area.PlacedEntityType = string.Empty;
                area.CardLevel = 0;
                area.Stats.MaxHP = 0d;
                area.Stats.Strength = 0d;
                area.Stats.WalkSpeed = 0d;
                area.Stats.AttackSpeed = 0d;
                area.Stats.StunDuration = 0d;
                area.Stats.StunDurationBoost = 0d;
                area.Stats.SecondaryAttackSpeed = 0d;
                area.Stats.SecondaryAttackSpeedBoost = 0d;
                area.Stats.CritMultiplier = 0d;
                area.Stats.CritChance = 0d;
                area.Stats.Projectiles = 0;
                area.Stats.EffectStacks = 0;
            }
        }

        if (fresh.HandCards?.Cards != null)
        {
            fresh.HandCards.Cards.Clear();
            fresh.HandCards.Cards.Add("Library");
            fresh.HandCards.Cards.Add("Warlock");
            fresh.HandCards.Cards.Add("Offering");
        }

        fresh.Policies?.Policies?.Clear();
        if (fresh.Blessing != null)
        {
            fresh.Blessing.Name = string.Empty;
            fresh.Blessing.Areas?.Clear();
        }

        if (fresh.GraceBlessing != null)
        {
            fresh.GraceBlessing.Name = string.Empty;
            fresh.GraceBlessing.Areas?.Clear();
        }

        fresh.Traps?.Clear();

        var troops = 0;
        var buildings = 0;
        foreach (var area in fresh.Areas)
        {
            if (area.PlacedEntityType == "Troop") troops++;
            if (area.PlacedEntityType == "Building") buildings++;
        }

        VersusPlugin.Log.LogInfo(
            $"fresh: built from {Path.GetFileName(files[^1])} king={fresh.Kings.PlayerKing} " +
            $"enemy={fresh.Kings.EnemyKings[0]} wave={fresh.WaveData.WaveNumber} lives={fresh.WaveData.PlayerLifes} " +
            $"areas={fresh.Areas.Count} troops={troops} buildings={buildings}");
        return fresh;
    }

    /// <summary>
    /// Activates the game's native "Multiplayer Screen" (present but inactive in
    /// the Gameplay scene) and populates it with the gauntlet runs. This is the
    /// official enemy-face bar shown at the start of a multiplayer match.
    /// </summary>
    public static void ShowMpScreen()
    {
        try
        {
            var screens = UnityEngine.Resources.FindObjectsOfTypeAll<GameplayInterface.GameplayUI_MultiplayerScreen>();
            if (screens == null || screens.Length == 0)
            {
                VersusPlugin.Log.LogWarning("mp-screen: no Multiplayer Screen found");
                return;
            }

            var screen = GameplayInterface.GameplayUI.MultiplayerScreen ?? screens[0];
            screen.gameObject.SetActive(true);
            var runs = MultiplayerManager.Session?.GauntletEnemies;
            if (runs == null || runs.Count == 0)
            {
                VersusPlugin.Log.LogWarning("mp-screen: no gauntlet runs");
                return;
            }

            var array = new Il2CppReferenceArray<MultiplayerRunData>(runs.Count);
            for (var i = 0; i < runs.Count; i++)
            {
                array[i] = runs[i];
            }

            System.Action<MultiplayerRunData> onClick = run =>
                VersusPlugin.Log.LogInfo($"mp-screen: face clicked run={run?.runId}");
            System.Func<bool> canInteract = () => true;
            var ilOnClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MultiplayerRunData>>(onClick);
            var ilCanInteract = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<bool>>(canInteract);

            var type = typeof(GameplayInterface.GameplayUI_MultiplayerScreen);
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
            type.GetMethod("Show", flags)?.Invoke(screen, null);
            var method = type.GetMethod("PopulateEnemyFaces", flags);
            method?.Invoke(screen, new object[] { array, ilOnClick, ilCanInteract });
            type.GetMethod("UpdateFaces", flags)?.Invoke(screen, null);
            VersusPlugin.Log.LogInfo($"mp-screen: populated {runs.Count} enemy faces (Show+UpdateFaces)");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"mp-screen failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    public static void ClickMainMenuStart()
    {
        try
        {
            var start = UnityEngine.GameObject.Find("MainMenuUI/Holder/Main/MainButtonHolder/StartBtn");
            if (start == null)
            {
                VersusPlugin.Log.LogWarning("click-start: StartBtn not found");
                return;
            }

            var button = start.GetComponent<UnityEngine.UI.Button>();
            if (button == null)
            {
                VersusPlugin.Log.LogWarning("click-start: Button component missing");
                return;
            }

            button.onClick.Invoke();
            VersusPlugin.Log.LogInfo("click-start: StartBtn onClick invoked");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"click-start failed: {e.Message}");
        }
    }

    private static float _showcaseAcceptAt = -1f;
    private static float _diplomatContinueAt = -1f;

    /// <summary>
    /// Defeating a multiplayer king queues a DiplomatView "declare war" modal
    /// (m_QueuedDefeatedMultiplayerKings / Screen01.ContinueBtn). If it is never
    /// clicked the game softlocks and Escape stops working. Press Continue after a
    /// short delay so the message is visible but gameplay resumes.
    /// </summary>
    public static void PumpDiplomatView()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameplayInterface.GameplayUI_DiplomatView>();
            var view = all != null && all.Length > 0 ? all[0] : null;
            if (view == null || !view.gameObject.activeInHierarchy)
            {
                _diplomatContinueAt = -1f;
                return;
            }

            if (_diplomatContinueAt < 0f)
            {
                _diplomatContinueAt = UnityEngine.Time.time + 2f;
                return;
            }

            if (UnityEngine.Time.time < _diplomatContinueAt)
            {
                return;
            }

            _diplomatContinueAt = -1f;
            var field = typeof(GameplayInterface.GameplayUI_DiplomatView).GetField("screen01",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var screen = field?.GetValue(view);
            var btnField = screen?.GetType().GetField("ContinueBtn",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var button = btnField?.GetValue(screen) as UnityEngine.UI.Button;
            if (button != null)
            {
                button.onClick.Invoke();
                VersusPlugin.Log.LogInfo("diplomat: pressed Screen01 Continue button");
            }
            else
            {
                VersusPlugin.Log.LogWarning("diplomat: Continue button not found");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"diplomat pump failed: {e.Message}");
        }
    }

    /// <summary>
    /// GameplayUI_EnemyShowcase is a modal ("enemy kings declaring war") with an
    /// Accept button. Showing it without clicking Accept softlocks the run and blocks
    /// Escape. Let it display briefly, then press its own Accept button.
    /// </summary>
    public static void PumpEnemyShowcase()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameplayInterface.GameplayUI_EnemyShowcase>();
            var showcase = all != null && all.Length > 0 ? all[0] : null;
            if (showcase == null || !showcase.gameObject.activeInHierarchy)
            {
                _showcaseAcceptAt = -1f;
                return;
            }

            if (_showcaseAcceptAt < 0f)
            {
                _showcaseAcceptAt = UnityEngine.Time.time + 3f;
                return;
            }

            if (UnityEngine.Time.time < _showcaseAcceptAt)
            {
                return;
            }

            _showcaseAcceptAt = -1f;
            var field = typeof(GameplayInterface.GameplayUI_EnemyShowcase).GetField("_acceptBtn",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var button = field?.GetValue(showcase) as UnityEngine.UI.Button;
            if (button != null)
            {
                button.onClick.Invoke();
                VersusPlugin.Log.LogInfo("showcase: pressed EnemyShowcase Accept button");
            }
            else
            {
                VersusPlugin.Log.LogWarning("showcase: EnemyShowcase Accept button not found");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"showcase pump failed: {e.Message}");
        }
    }

    private static bool _escHeld;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// The game's OpenPauseMenu InputAction is blocked in our attached run (its
    /// CanOpenPauseMenu check fails). Handle Escape directly and call the native
    /// pause menu's own toggle so the real pause UI still works.
    /// </summary>
    public static void PumpEscape()
    {
        try
        {
            // Single source of truth: the OS key state with a clean press/release edge.
            // Using Unity's Input System edge as well double-toggled on one key press.
            var pressed = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (!pressed)
            {
                _escHeld = false;
                return;
            }

            if (_escHeld)
            {
                return;
            }

            _escHeld = true;
            TogglePause();
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"escape key failed: {e.Message}");
        }
    }

    private static void ActivateAll(UnityEngine.Transform t)
    {
        if (t == null)
        {
            return;
        }

        if (!t.gameObject.activeSelf)
        {
            t.gameObject.SetActive(true);
        }

        for (var i = 0; i < t.childCount; i++)
        {
            ActivateAll(t.GetChild(i));
        }
    }

    private static void DumpPauseFields(Core.GameplayUI_PauseMenu menu)
    {
        try
        {
            var fields = typeof(Core.GameplayUI_PauseMenu).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            foreach (var f in fields)
            {
                var v = f.GetValue(menu);
                if (v == null || (v is UnityEngine.Object u && u == null))
                {
                    VersusPlugin.Log.LogWarning($"pause-field NULL: {f.Name} ({f.FieldType.Name})");
                }
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"pause field dump failed: {e.Message}");
        }
    }

    private static string PathOf(UnityEngine.Transform t)
    {
        var parts = new System.Collections.Generic.List<string>();
        while (t != null)
        {
            parts.Add(t.name);
            t = t.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static UnityEngine.GameObject _pauseOverlay;
    private static bool _paused;
    private static bool _pauseInitialized;

    private static void EnsurePauseOverlay()
    {
        if (_pauseOverlay != null)
        {
            return;
        }

        var root = new UnityEngine.GameObject("VersusPause");
        root.AddComponent<UnityEngine.RectTransform>();
        var canvas = root.AddComponent<UnityEngine.Canvas>();
        canvas.renderMode = UnityEngine.RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 2000;
        root.AddComponent<UnityEngine.UI.CanvasScaler>();
        root.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        UnityEngine.Object.DontDestroyOnLoad(root);

        var panel = new UnityEngine.GameObject("Panel");
        panel.AddComponent<UnityEngine.RectTransform>();
        panel.transform.SetParent(root.transform, false);
        var rect = panel.GetComponent<UnityEngine.RectTransform>();
        rect.anchorMin = new UnityEngine.Vector2(0.5f, 0.5f);
        rect.anchorMax = new UnityEngine.Vector2(0.5f, 0.5f);
        rect.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
        rect.sizeDelta = new UnityEngine.Vector2(420f, 260f);
        var image = panel.AddComponent<UnityEngine.UI.Image>();
        image.color = new UnityEngine.Color(0.06f, 0.07f, 0.1f, 0.96f);

        TMPro.TMP_Text title = null;
        var existing = UnityEngine.Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>();
        var font = existing != null && existing.Length > 0 ? existing[0].font : null;

        var label = new UnityEngine.GameObject("Title");
        label.AddComponent<UnityEngine.RectTransform>();
        label.transform.SetParent(panel.transform, false);
        var lr = label.GetComponent<UnityEngine.RectTransform>();
        lr.anchorMin = new UnityEngine.Vector2(0f, 1f);
        lr.anchorMax = new UnityEngine.Vector2(1f, 1f);
        lr.pivot = new UnityEngine.Vector2(0.5f, 1f);
        lr.anchoredPosition = new UnityEngine.Vector2(0f, -20f);
        lr.sizeDelta = new UnityEngine.Vector2(0f, 50f);
        title = label.AddComponent<TMPro.TextMeshProUGUI>();
        title.text = "PAUSED";
        title.alignment = TMPro.TextAlignmentOptions.Center;
        title.fontSize = 34;
        title.color = UnityEngine.Color.white;
        if (font != null) title.font = font;

        AddPauseButton(panel.transform, "RESUME", -70f, font, () => TogglePause());
        AddPauseButton(panel.transform, "QUIT TO MENU", -140f, font, () =>
        {
            TogglePause();
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        });

        root.SetActive(false);
        _pauseOverlay = root;
    }

    private static void AddPauseButton(UnityEngine.Transform parent, string caption, float y,
        TMPro.TMP_FontAsset font, System.Action onClick)
    {
        var go = new UnityEngine.GameObject(caption);
        go.AddComponent<UnityEngine.RectTransform>();
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<UnityEngine.RectTransform>();
        rect.anchorMin = new UnityEngine.Vector2(0.5f, 1f);
        rect.anchorMax = new UnityEngine.Vector2(0.5f, 1f);
        rect.pivot = new UnityEngine.Vector2(0.5f, 1f);
        rect.anchoredPosition = new UnityEngine.Vector2(0f, y);
        rect.sizeDelta = new UnityEngine.Vector2(320f, 56f);
        var img = go.AddComponent<UnityEngine.UI.Image>();
        img.color = new UnityEngine.Color(0.16f, 0.19f, 0.26f, 1f);
        var btn = go.AddComponent<UnityEngine.UI.Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(
            new System.Action(() => onClick())));

        var label = new UnityEngine.GameObject("Label");
        label.AddComponent<UnityEngine.RectTransform>();
        label.transform.SetParent(go.transform, false);
        var lr = label.GetComponent<UnityEngine.RectTransform>();
        lr.anchorMin = UnityEngine.Vector2.zero;
        lr.anchorMax = UnityEngine.Vector2.one;
        lr.offsetMin = UnityEngine.Vector2.zero;
        lr.offsetMax = UnityEngine.Vector2.zero;
        var text = label.AddComponent<TMPro.TextMeshProUGUI>();
        text.text = caption;
        text.alignment = TMPro.TextAlignmentOptions.Center;
        text.fontSize = 22;
        text.color = UnityEngine.Color.white;
        if (font != null) text.font = font;
    }

    public static bool Paused => _paused;

    public static void TogglePause()
    {
        var opening = !_paused;
        // Only invoke the native Show path when opening. Calling it while resuming
        // re-activated the panel right before we hid it, causing a visible flicker.
        if (opening)
        {
            TryNativePause();
        }

        var all = UnityEngine.Resources.FindObjectsOfTypeAll<Core.GameplayUI_PauseMenu>();
        var menu = all != null && all.Length > 0 ? all[0] : null;
        var stateProp = typeof(Core.GameplayUI_PauseMenu).GetProperty("PauseMenuOnScreen",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        var on = _paused;
        if (menu != null && opening)
        {
            // Show() throws before activating the button hierarchy. Activate the
            // main panel and its children only - activating every child also brings
            // up the Options/Forfeit/Quit panels layered on top.
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
            foreach (var panelName in new[] { "m_OptionsPanel", "m_ForfeitPanel", "m_QuitPanel", "m_ReallyQuitPanel" })
            {
                var field = typeof(Core.GameplayUI_PauseMenu).GetField(panelName, flags);
                (field?.GetValue(menu) as UnityEngine.GameObject)?.SetActive(false);
            }

            // The main container "Holder" (m_HolderGroup) is what Show() normally
            // activates; without it the GamePaused text and buttons stay hidden.
            var holderField = typeof(Core.GameplayUI_PauseMenu).GetField("m_HolderGroup", flags);
            var holderGroup = holderField?.GetValue(menu) as UnityEngine.CanvasGroup;
            if (holderGroup != null)
            {
                holderGroup.gameObject.SetActive(true);
                holderGroup.alpha = 1f;
                holderGroup.interactable = true;
                holderGroup.blocksRaycasts = true;
            }

            var mainField = typeof(Core.GameplayUI_PauseMenu).GetField("m_MainPanel", flags);
            var mainPanel = mainField?.GetValue(menu) as UnityEngine.GameObject;
            if (mainPanel != null)
            {
                ActivateAll(mainPanel.transform);
            }

            menu.gameObject.SetActive(true);

            var groups = menu.GetComponentsInChildren<UnityEngine.CanvasGroup>(true);
            if (groups != null)
            {
                foreach (var g in groups)
                {
                    g.alpha = 1f;
                    g.interactable = true;
                    g.blocksRaycasts = true;
                }
            }

            var bg = menu.GetComponent<UnityEngine.UI.Image>();
            if (bg != null) bg.enabled = true;
            VersusPlugin.Log.LogInfo($"escape: native pause forced active={menu.gameObject.activeInHierarchy}");
        }
        else if (menu != null)
        {
            menu.gameObject.SetActive(false);
        }

        _paused = !on;
        stateProp?.SetValue(null, _paused);
        UnityEngine.Time.timeScale = on ? 1f : 0f;
        VersusPlugin.Log.LogInfo($"escape: pause toggled on={_paused}");
    }

    public static void ClickPauseButton(string name)
    {
        try
        {
            var menus = UnityEngine.Resources.FindObjectsOfTypeAll<Core.GameplayUI_PauseMenu>();
            var menu = menus != null && menus.Length > 0 ? menus[0] : null;
            if (menu == null)
            {
                VersusPlugin.Log.LogWarning($"pause-click: menu not found ({name})");
                return;
            }

            var buttons = menu.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            foreach (var b in buttons)
            {
                if (b.name == name)
                {
                    b.onClick.Invoke();
                    VersusPlugin.Log.LogInfo($"pause-click: invoked {name}");
                    return;
                }
            }

            VersusPlugin.Log.LogWarning($"pause-click: {name} not found");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"pause-click failed: {e.Message}");
        }
    }

    public static void CheckEnemyFaces()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Core.GameplayUI_PauseMenu>();
            var menu = all != null && all.Length > 0 ? all[0] : null;
            if (menu == null)
            {
                VersusPlugin.Log.LogWarning("faces-check: menu not found");
                return;
            }

            var holder = menu.transform.Find("Canvas/Holder/Main/VerticalLayout/EnemiesShowcase/EnemyFacesHolder");
            var images = holder != null ? holder.GetComponentsInChildren<UnityEngine.UI.Image>(true) : null;
            var withSprite = 0;
            if (images != null)
            {
                foreach (var img in images)
                {
                    if (img.sprite != null) withSprite++;
                }
            }

            VersusPlugin.Log.LogInfo($"faces-check: holder={(holder == null ? "null" : "set")} images={images?.Length ?? -1} withSprite={withSprite}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"faces-check failed: {e.Message}");
        }
    }

    public static void DumpPauseHierarchy()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Core.GameplayUI_PauseMenu>();
            var menu = all != null && all.Length > 0 ? all[0] : null;
            if (menu == null)
            {
                VersusPlugin.Log.LogWarning("pause-tree: menu not found");
                return;
            }

            LogPauseTree(menu.transform, 0);
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"pause-tree failed: {e.Message}");
        }
    }

    private static void LogPauseTree(UnityEngine.Transform t, int depth)
    {
        if (t == null || depth > 10)
        {
            return;
        }

        var button = t.GetComponent<UnityEngine.UI.Button>();
        var text = t.GetComponent<TMPro.TMP_Text>();
        var group = t.GetComponent<UnityEngine.CanvasGroup>();
        VersusPlugin.Log.LogInfo(
            $"pause-tree: {"  ".PadRight(depth * 2)}{t.name} active={t.gameObject.activeSelf} " +
            $"btn={(button != null ? "yes" : "-")} text={(text == null ? "-" : text.text)} " +
            $"alpha={(group == null ? "-" : group.alpha.ToString("F2"))}");
        for (var i = 0; i < t.childCount; i++)
        {
            LogPauseTree(t.GetChild(i), depth + 1);
        }
    }

    public static void ProbeUiGetters()
    {
        try
        {
            var hubPerks = UnityEngine.Resources.FindObjectsOfTypeAll<MultiplayerHubInterface.MultiplayerHubPerksUI>();
            VersusPlugin.Log.LogInfo($"ui-getter: HubPerksUI count={hubPerks?.Length ?? -1}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"ui-getter: HubPerksUI type unknown ({e.Message})");
        }

        var names = new[]
        {
            "Compendium", "PerkShowcase", "MissionScreen", "EnemyShowcase",
            "PlacingView", "BattleBar", "HandCards", "PauseMenu", "Year99Warning",
            "FinalWave", "EndUI", "MultiplayerScreen",
        };
        foreach (var name in names)
        {
            try
            {
                var prop = typeof(GameplayInterface.GameplayUI).GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                var value = prop?.GetValue(null);
                VersusPlugin.Log.LogInfo($"ui-getter: {name} = {(value == null ? "NULL" : "set")}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"ui-getter: {name} THREW {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
            }
        }
    }

    public static void TryNativePause()
    {
        try
        {
            // PauseMenu.Show() dereferences MultiplayerHubPerksUI.Instance (the hub
            // perks screen is not present in the Gameplay scene). Create a minimal
            // instance so that check no longer throws.
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Core.GameplayUI_PauseMenu>();
            var menu = all != null && all.Length > 0 ? all[0] : null;
            if (menu == null)
            {
                VersusPlugin.Log.LogWarning("native-pause: menu not found");
                return;
            }

            // Initialize() only needs to run once - re-running it on every toggle is
            // what caused the visible hitch when opening the menu.
            if (!_pauseInitialized)
            {
                _pauseInitialized = true;
                var init = typeof(Core.GameplayUI_PauseMenu).GetMethod("Initialize",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                try
                {
                    init?.Invoke(menu, null);
                }
                catch (Exception ie)
                {
                    VersusPlugin.Log.LogWarning($"native-pause: Initialize threw {(ie.InnerException ?? ie).Message}");
                }
            }

            // Call the base UISubController.Show() directly: the override's
            // hub-specific checks NRE offline, but the base handles panel activation.
            var baseShow = typeof(Core.GameplayUI_PauseMenu).BaseType?.GetMethod("Show",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            baseShow?.Invoke(menu, null);
            VersusPlugin.Log.LogInfo($"native-pause: base Show done active={menu.gameObject.activeInHierarchy}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"native-pause threw: {(e.InnerException ?? e).Message}");
        }
    }

    public static void TogglePauseNative()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<Core.GameplayUI_PauseMenu>();
            var menu = all != null && all.Length > 0 ? all[0] : null;
            if (menu == null)
            {
                VersusPlugin.Log.LogWarning("escape: pause menu not found");
                return;
            }

            // SwitchVisibility -> Show() NREs in the attached run (a null tier
            // collection), so toggle the panel/state directly. The menu's own
            // Resume/Forfeit/Menu buttons keep their native listeners.
            var stateProp = typeof(Core.GameplayUI_PauseMenu).GetProperty("PauseMenuOnScreen",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            var on = stateProp != null && stateProp.GetValue(null) is bool b && b;
            if (!on)
            {
                DumpPauseFields(menu);
            }

            var path = PathOf(menu.transform);
            var parentActive = menu.transform.parent != null && menu.transform.parent.gameObject.activeInHierarchy;
            VersusPlugin.Log.LogInfo($"escape: menu path={path} self={menu.gameObject.activeSelf} parentActive={parentActive} on={on}");

            // Make sure the whole hierarchy is active before showing.
            var t = menu.transform;
            while (t != null)
            {
                if (!t.gameObject.activeSelf)
                {
                    t.gameObject.SetActive(true);
                }

                t = t.parent;
            }

            menu.gameObject.SetActive(!on);

            if (!on)
            {
                var groups = menu.GetComponentsInChildren<UnityEngine.CanvasGroup>(true);
                if (groups != null)
                {
                    foreach (var g in groups)
                    {
                        g.alpha = 1f;
                        g.interactable = true;
                        g.blocksRaycasts = true;
                    }
                }

                var mainField = typeof(Core.GameplayUI_PauseMenu).GetField("m_MainPanel",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var mainPanel = mainField?.GetValue(menu) as UnityEngine.GameObject;
                if (mainPanel != null)
                {
                    mainPanel.SetActive(true);
                }
            }

            stateProp?.SetValue(null, !on);
            UnityEngine.Time.timeScale = on ? 1f : 0f;
            VersusPlugin.Log.LogInfo($"escape: pause toggled on={!on} active={menu.gameObject.activeInHierarchy}");
        }
        catch (Exception e)
        {
            var inner = e.InnerException ?? e;
            VersusPlugin.Log.LogWarning($"escape failed: {inner.GetType().Name}: {inner.Message}\n{inner}");
        }
    }

    /// <summary>
    /// Displays the three enemy-king cards (GameplayUI_EnemyShowcase) as the match
    /// intro. PumpEnemyShowcase dismisses it after a short delay so it never blocks
    /// input. No year/cutscene panel is involved.
    /// </summary>
    public static void ShowEnemyReveal()
    {
        try
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameplayInterface.GameplayUI_EnemyShowcase>();
            var showcase = all != null && all.Length > 0 ? all[0] : null;
            if (showcase == null)
            {
                VersusPlugin.Log.LogWarning("reveal: EnemyShowcase not found");
                return;
            }

            var type = typeof(GameplayInterface.GameplayUI_EnemyShowcase);
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
            type.GetMethod("Initialize", flags)?.Invoke(showcase, null);
            type.GetMethod("Show", flags)?.Invoke(showcase, null);
            VersusPlugin.Log.LogInfo("reveal: EnemyShowcase Initialize+Show invoked");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"reveal failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    public static void PlayYearShowcase()
    {
        try
        {
            var panels = UnityEngine.Resources.FindObjectsOfTypeAll<GameplayInterface.GameplayUI_YearShowcasePanel>();
            if (panels == null || panels.Length == 0)
            {
                VersusPlugin.Log.LogWarning("showcase: no YearShowcasePanel found");
                return;
            }

            var panel = panels[0];
            panel.gameObject.SetActive(true);
            var method = typeof(GameplayInterface.GameplayUI_YearShowcasePanel).GetMethod("ShowcaseYear",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            method?.Invoke(panel, null);
            VersusPlugin.Log.LogInfo($"showcase: ShowcaseYear invoked panel={panel.name}");

            var allShowcases = UnityEngine.Resources.FindObjectsOfTypeAll<GameplayInterface.GameplayUI_EnemyShowcase>();
            var enemyShowcase = allShowcases != null && allShowcases.Length > 0 ? allShowcases[0] : null;
            if (enemyShowcase != null)
            {
                var esType = enemyShowcase.GetType();
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance;
                esType.GetMethod("Initialize", flags)?.Invoke(enemyShowcase, null);
                esType.GetMethod("Show", flags)?.Invoke(enemyShowcase, null);
                VersusPlugin.Log.LogInfo("showcase: EnemyShowcase Initialize+Show invoked");
            }
            else
            {
                VersusPlugin.Log.LogWarning("showcase: EnemyShowcase missing");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"showcase failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    public static void DumpFreshSnapshot()
    {
        try
        {
            var fresh = new Gameplay.GameSnapshot();
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(fresh);
            VersusPlugin.Log.LogInfo($"dump-fresh: bytes={json.Length} wave={fresh.WaveData?.WaveNumber} " +
                $"lives={fresh.WaveData?.PlayerLifes} areas={fresh.Areas?.Count} " +
                $"playerKing={fresh.Kings?.PlayerKing} enemyKings={fresh.Kings?.EnemyKings?.Count}");
            var path = Path.Combine(VersusPlugin.DataDir, "fresh-template.json");
            File.WriteAllText(path, json);
            VersusPlugin.Log.LogInfo($"dump-fresh: wrote {path}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"dump-fresh failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public static void DumpPerks()
    {
        try
        {
            var unlocked = KingManagement.KingManager.unlockedPerks;
            VersusPlugin.Log.LogInfo($"dump-perks: unlocked={unlocked?.Count ?? -1}");
            if (unlocked != null)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var term in unlocked)
                {
                    names.Add(term);
                }

                VersusPlugin.Log.LogInfo($"dump-perks: terms=[{string.Join(",", names)}]");
            }

            LocalSessionProbe.ReadChoices(out var kingId, out var _);
            var owner = kingId >= 0 ? (Core.CardOwnership)kingId : Core.CardOwnership.KingOfNothing;
            var active = KingManagement.KingManager.GetAllKingActivePerks(owner);
            VersusPlugin.Log.LogInfo($"dump-perks: owner={owner} active={active?.Count ?? -1}");
            if (active != null)
            {
                foreach (var slot in active)
                {
                    VersusPlugin.Log.LogInfo($"dump-perks: slot term={slot.perkTerm} level={slot.level} x={slot.x} y={slot.y}");
                }
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"dump-perks failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Applies the chosen king's active perks to the attached session's snapshot
    /// store. Decompiled MultiplayerManager.StartGame.MoveNext does exactly this
    /// after Wave.StartNewGame: GetAllKingActivePerks(owner) -> SetPerks(...).
    /// </summary>
    public static void ApplyChosenPerks()
    {
        try
        {
            ReadChoices(out var kingId, out var perkIndex);
            var owner = kingId >= 0 ? (Core.CardOwnership)kingId : Core.CardOwnership.KingOfNothing;
            var session = MultiplayerManager.Session;
            var snapshots = session?.Snapshots;
            if (snapshots == null)
            {
                VersusPlugin.Log.LogWarning("perks: no session snapshots to apply to");
                return;
            }

            // The official flow reads active perks from KingManager. Add the chosen
            // loadout's perks first (GetAllKingActivePerks returns none otherwise),
            // then hand them to the session's snapshot store exactly as the game's
            // StartGame.MoveNext does.
            var unlocked = KingManagement.KingManager.unlockedPerks;
            if (unlocked != null && unlocked.Count > 0)
            {
                var start = (Math.Max(1, perkIndex) - 1) * 3;
                for (var i = 0; i < 3 && start + i < unlocked.Count; i++)
                {
                    var settings = new PerkSystem.PerkSettings
                    {
                        Name = unlocked[start + i],
                        Level = 1,
                    };
                    KingManagement.KingManager.AddPerkSettings(owner, settings);
                }
            }

            var perks = KingManagement.KingManager.GetAllKingActivePerks(owner);
            var count = perks?.Count ?? 0;
            var enumerable = perks?.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PerkSystem.PerkSlotSettings>>();
            snapshots.SetPerks(enumerable);
            VersusPlugin.Log.LogInfo($"perks: applied {count} active perks for {owner} (loadout={perkIndex})");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"perks apply failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// Loads an authentic multiplayer save (read-only copy of the pre-test backup)
    /// without a fabricated session, so we can observe the game's own multiplayer
    /// wave behaviour as a baseline.
    /// </summary>
    public static void LoadBackupRun()
    {
        try
        {
            var pathFile = Path.Combine(VersusPlugin.DataDir, "backup-path.txt");
            if (!File.Exists(pathFile))
            {
                VersusPlugin.Log.LogWarning("backup-run: backup-path.txt missing");
                return;
            }

            var backupPath = File.ReadAllText(pathFile).Trim();
            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(backupPath));
            if (snapshot == null)
            {
                VersusPlugin.Log.LogWarning("backup-run: failed to deserialize");
                return;
            }

            VersusPlugin.Log.LogInfo(
                $"backup-run: wave={snapshot.WaveData?.WaveNumber} multiplayer={snapshot.WaveData?.IsMultiplayer} " +
                $"king={snapshot.Kings?.PlayerKing} areas={snapshot.Areas?.Count} " +
                $"session={(MultiplayerManager.Session == null ? "null" : "set")}");

            if (snapshot.WaveData != null)
            {
                snapshot.WaveData.IsMultiplayer = true;
            }

            var loaded = Core.Wave.LoadGame(snapshot, true, "Gameplay", false);
            VersusPlugin.Log.LogInfo($"backup-run: LoadGame returned {loaded}; registerRunStart=false");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"backup-run failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Loads the captured wave-15 run through Core.Wave.LoadGame with the snapshot's
    /// IsMultiplayer flag set. The run therefore starts multiplayer with the attached
    /// session, instead of flipping IsMultiplayer mid-run (which softlocked before).
    /// Falls back to a single-player load to validate the load path if the
    /// multiplayer load throws.
    /// </summary>
    public static void LoadProbeRun()
    {
        try
        {
            var snapshot = BuildFreshSnapshot();
            var troopAreas = 0;
            var buildingAreas = 0;
            if (snapshot.Areas != null)
            {
                foreach (var area in snapshot.Areas)
                {
                    if (area.PlacedEntityType == "Troop") troopAreas++;
                    if (area.PlacedEntityType == "Building") buildingAreas++;
                }
            }

            VersusPlugin.Log.LogInfo(
                $"load-run: snapshot wave={snapshot.WaveData?.WaveNumber ?? -1} waveDataMultiplayer={snapshot.WaveData?.IsMultiplayer} " +
                $"session={(MultiplayerManager.Session == null ? "null" : "set")} " +
                $"troops={troopAreas} buildings={buildingAreas}");

            try
            {
                var loaded = Core.Wave.LoadGame(snapshot, true, "Gameplay", false);
                VersusPlugin.Log.LogInfo($"load-run: LoadGame(instaLoad=true, scene=Gameplay) returned {loaded}; registerRunStart=false");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"load-run: LoadGame failed: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"load-run failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Supplies Core.Wave.CurrentMultiplayerEnemySnapshot from the attached session's
    /// gauntlet run for the current wave. This is where live versus data enters the
    /// wave pipeline: the game keeps the property null when it cannot resolve a
    /// gauntlet enemy itself (offline, no matchmaking), and SpawnWave then falls back
    /// to a normal single-player wave. Safe to call repeatedly; no-ops when a
    /// snapshot is already present or the run is not multiplayer.
    /// </summary>
    public static void EnsureEnemySnapshot(string reason)
    {
        try
        {
            if (Core.Wave.CurrentMultiplayerEnemySnapshot != null)
            {
                return;
            }

            var data = Core.Wave.Data;
            if (data == null || !data.IsMultiplayer || data.Wave < 1)
            {
                return;
            }

            var session = MultiplayerManager.Session;
            if (session == null)
            {
                return;
            }

            MultiplayerRunData enemyRun = null;
            if (session.GauntletEnemies != null && session.GauntletEnemies.Count > 0)
            {
                enemyRun = session.GauntletEnemies[0];
            }
            else if (session.Data != null)
            {
                for (var i = 0; i < session.Data.Length; i++)
                {
                    if (session.Data[i] != null)
                    {
                        enemyRun = session.Data[i];
                        break;
                    }
                }
            }

            if (enemyRun?.snapshot == null)
            {
                VersusPlugin.Log.LogWarning($"ensure-snapshot({reason}): no enemy run snapshot available");
                return;
            }

            var snapshot = enemyRun.snapshot.GetWaveSnapshot(data.Wave, true) ?? enemyRun.snapshot.FinalSnapshot;
            if (snapshot == null)
            {
                VersusPlugin.Log.LogWarning($"ensure-snapshot({reason}): no snapshot for wave {data.Wave}");
                return;
            }

            Core.Wave.CurrentMultiplayerEnemySnapshot = snapshot;
            VersusPlugin.Log.LogInfo(
                $"ensure-snapshot({reason}): set wave={snapshot.WaveData?.WaveNumber ?? -1} requested={data.Wave} " +
                $"enemyPlayer={enemyRun.playerId} enemyKing={enemyRun.kingId} " +
                $"areas={snapshot.Areas?.Count ?? -1}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"ensure-snapshot({reason}) failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Live attachment step: build a two-run session from the captured snapshot,
    /// supply GauntletEnemies manually (PopulateGauntletEnemies NREs offline), then
    /// attach it to MultiplayerManager.Session and switch the run to multiplayer so
    /// the next wave start goes through the gauntlet path. Called repeatedly from
    /// VersusBehaviour until it succeeds (wave instance must exist).
    /// </summary>
    public static bool TryAttach()
    {
        try
        {
            if (Core.Wave.Instance == null)
            {
                return false;
            }

            var data = Core.Wave.Data;
            if (data == null)
            {
                return false;
            }

            var files = Directory.GetFiles(VersusPlugin.DataDir, "*-f7-capture-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                VersusPlugin.Log.LogWarning("attach: no captured snapshot available");
                return false;
            }

            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(files[^1]));
            if (snapshot?.Kings == null || snapshot.WaveData == null)
            {
                VersusPlugin.Log.LogWarning("attach: snapshot has no king or wave data");
                return false;
            }

            var kingName = "KingOf" + snapshot.Kings.PlayerKing;
            if (!Enum.TryParse<Core.CardOwnership>(kingName, out var owner))
            {
                VersusPlugin.Log.LogWarning($"attach: unmapped snapshot king {snapshot.Kings.PlayerKing}");
                return false;
            }

            var opponentKing = owner == Core.CardOwnership.KingOfSpells
                ? Core.CardOwnership.KingOfNothing
                : Core.CardOwnership.KingOfSpells;

            // GameplayUI_UserFace.Setup dereferences run.player.settings. Without it
            // PopulateEnemyFaces throws and the avatar bar never renders.
            var localProfile = new PlayerProfileData
            {
                playerId = 900001,
                username = "Local probe",
                settings = PlayerSettingsData.CreateLocalSnapshot(),
            };
            var enemyProfile = new PlayerProfileData
            {
                playerId = 900003,
                username = "Local probe opponent",
                completedMissions = new Il2CppSystem.Collections.Generic.List<int>(),
                unlockedItems = new Il2CppSystem.Collections.Generic.List<int>(),
                settings = PlayerSettingsData.CreateLocalSnapshot(),
            };

            var localSnapshots = new MultiplayerGameSnapshots();
            localSnapshots.Add(snapshot);
            localSnapshots.FinalSnapshot = snapshot;
            var enemySnapshots = new MultiplayerGameSnapshots();
            enemySnapshots.Add(snapshot);
            enemySnapshots.FinalSnapshot = snapshot;

            var localRun = new MultiplayerRunData
            {
                runId = 900001,
                playerId = localProfile.playerId,
                kingId = (int)owner,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = localSnapshots,
                player = localProfile,
            };
            var enemyRun = new MultiplayerRunData
            {
                runId = 900003,
                playerId = enemyProfile.playerId,
                kingId = (int)opponentKing,
                yearPlayed = snapshot.WaveData.WaveNumber,
                currentHP = snapshot.WaveData.PlayerLifes,
                gameVersion = snapshot.Version,
                snapshot = enemySnapshots,
                player = enemyProfile,
            };

            var runs = new Il2CppReferenceArray<MultiplayerRunData>(2);
            runs[0] = localRun;
            runs[1] = enemyRun;
            var session = new MultiplayerSession(runs,
                new Il2CppStringArray(0), new Il2CppStringArray(0),
                new Il2CppStringArray(0), new Il2CppStringArray(0));

            var gauntlet = new Il2CppSystem.Collections.Generic.List<MultiplayerRunData>();
            gauntlet.Add(enemyRun);
            var gauntletProp = typeof(MultiplayerSession).GetProperty("GauntletEnemies",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var activeProp = typeof(MultiplayerSession).GetProperty("GauntletActive",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            gauntletProp?.SetValue(session, gauntlet);
            activeProp?.SetValue(session, true);

            var previous = MultiplayerManager.Session?.Pointer ?? IntPtr.Zero;
            MultiplayerManager.Session = session;
            var attached = (MultiplayerManager.Session?.Pointer ?? IntPtr.Zero) == session.Pointer;

            VersusPlugin.Log.LogInfo(
                $"attach: previousSession=0x{previous.ToInt64():X} newSession=0x{session.Pointer.ToInt64():X} attached={attached} " +
                $"gauntletCount={session.GauntletEnemies?.Count ?? -1} gauntletActive={session.GauntletActive} " +
                $"wave={Core.Wave.Instance != null}");

            if (!attached)
            {
                VersusPlugin.Log.LogWarning("attach: Session setter did not stick; not flipping IsMultiplayer");
                return false;
            }

            VersusPlugin.Log.LogInfo($"attach: before flip IsMultiplayer={data.IsMultiplayer} runWave={snapshot.WaveData.WaveNumber} localKing={owner} enemyKing={opponentKing}");
            data.IsMultiplayer = true;
            VersusPlugin.Log.LogInfo($"attach: after flip IsMultiplayer={data.IsMultiplayer}; next wave start should use gauntlet path");
            return true;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"attach failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }
}
