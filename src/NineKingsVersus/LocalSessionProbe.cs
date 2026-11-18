using System;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MultiplayerSystems;

namespace NineKingsVersus;

internal static class LocalSessionProbe
{
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
            var files = Directory.GetFiles(VersusPlugin.DataDir, "*-f7-capture-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                VersusPlugin.Log.LogWarning("attach-pre: no captured snapshot available");
                return false;
            }

            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(files[^1]));
            if (snapshot?.Kings == null || snapshot.WaveData == null)
            {
                VersusPlugin.Log.LogWarning("attach-pre: snapshot has no king or wave data");
                return false;
            }

            kingName = "KingOf" + snapshot.Kings.PlayerKing;
            if (!Enum.TryParse<Core.CardOwnership>(kingName, out var owner))
            {
                VersusPlugin.Log.LogWarning($"attach-pre: unmapped snapshot king {snapshot.Kings.PlayerKing}");
                return false;
            }

            var opponentKing = owner == Core.CardOwnership.KingOfSpells
                ? Core.CardOwnership.KingOfNothing
                : Core.CardOwnership.KingOfSpells;

            var localProfile = new PlayerProfileData { playerId = 900001, username = "Local probe" };
            var enemyProfile = new PlayerProfileData
            {
                playerId = 900003,
                username = "Local probe opponent",
                completedMissions = new Il2CppSystem.Collections.Generic.List<int>(),
                unlockedItems = new Il2CppSystem.Collections.Generic.List<int>(),
            };

            var localSnapshots = new MultiplayerGameSnapshots();
            localSnapshots.Add(snapshot);
            localSnapshots.FinalSnapshot = snapshot;
            var enemySnapshots = new MultiplayerGameSnapshots();
            enemySnapshots.Add(snapshot);
            enemySnapshots.FinalSnapshot = snapshot;

            // The probe run advances one wave after attachment; StartWave appears to look
            // up the enemy snapshot for the *current* wave, so also register a clone for
            // the next wave. This is what the real per-wave snapshot series provides.
            var nextWaveSnapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(
                Newtonsoft.Json.JsonConvert.SerializeObject(snapshot));
            if (nextWaveSnapshot?.WaveData != null)
            {
                nextWaveSnapshot.WaveData.WaveNumber = snapshot.WaveData.WaveNumber + 1;
                nextWaveSnapshot.WaveData.IsMultiplayer = true;
                localSnapshots.Add(nextWaveSnapshot);
                enemySnapshots.Add(nextWaveSnapshot);
                localSnapshots.FinalSnapshot = nextWaveSnapshot;
                enemySnapshots.FinalSnapshot = nextWaveSnapshot;
                VersusPlugin.Log.LogInfo($"attach-pre: snapshot series waves={snapshot.WaveData.WaveNumber},{nextWaveSnapshot.WaveData.WaveNumber}");
            }

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
            foreach (Core.CardOwnership king in Enum.GetValues(typeof(Core.CardOwnership)))
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

            var gauntlet = new Il2CppSystem.Collections.Generic.List<MultiplayerRunData>();
            gauntlet.Add(enemyRun);
            typeof(MultiplayerSession).GetProperty("GauntletEnemies",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(session, gauntlet);
            typeof(MultiplayerSession).GetProperty("GauntletActive",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(session, true);

            var previous = MultiplayerManager.Session?.Pointer ?? IntPtr.Zero;
            MultiplayerManager.Session = session;
            var attached = (MultiplayerManager.Session?.Pointer ?? IntPtr.Zero) == session.Pointer;

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
                args[4] = null;                  // seed (Nullable<int>)
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
            var files = Directory.GetFiles(VersusPlugin.DataDir, "*-f7-capture-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                VersusPlugin.Log.LogWarning("load-run: no captured snapshot available");
                return;
            }

            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Gameplay.GameSnapshot>(File.ReadAllText(files[^1]));
            if (snapshot == null)
            {
                VersusPlugin.Log.LogWarning("load-run: snapshot failed to deserialize");
                return;
            }

            VersusPlugin.Log.LogInfo(
                $"load-run: snapshot wave={snapshot.WaveData?.WaveNumber ?? -1} waveDataMultiplayer={snapshot.WaveData?.IsMultiplayer} " +
                $"session={(MultiplayerManager.Session == null ? "null" : "set")}");

            var singleLoad = File.Exists(Path.Combine(VersusPlugin.DataDir, "single-load"));
            if (snapshot.WaveData != null && !singleLoad)
            {
                snapshot.WaveData.IsMultiplayer = true;
                VersusPlugin.Log.LogInfo($"load-run: waveData.IsMultiplayer set to {snapshot.WaveData.IsMultiplayer}");
            }
            else
            {
                VersusPlugin.Log.LogInfo($"load-run: single-load flag; leaving waveData.IsMultiplayer={snapshot.WaveData?.IsMultiplayer}");
            }

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

            var localProfile = new PlayerProfileData { playerId = 900001, username = "Local probe" };
            var enemyProfile = new PlayerProfileData
            {
                playerId = 900003,
                username = "Local probe opponent",
                completedMissions = new Il2CppSystem.Collections.Generic.List<int>(),
                unlockedItems = new Il2CppSystem.Collections.Generic.List<int>(),
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
