using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace NineKingsVersus;

/// <summary>
/// Injected component. Provisional hotkeys while the mod is a probe:
///   F9  diagnose scene / console / difficulty assets / snapshot
///   F10 try GameplayManager.StartNewGame with the first difficulty asset
///   F7  capture the current board snapshot into memory
///   F8  inject the captured snapshot as the multiplayer enemy and refresh the map
/// Registered from <see cref="VersusPlugin.Load"/>.
/// </summary>
public sealed class VersusBehaviour : MonoBehaviour
{
    /// <summary>Single authoritative lifecycle for the current private match.</summary>
    public static readonly MatchState Match = new();

    private static Gameplay.GameSnapshot _cached;
    private static string _lastScene;
    private static bool _autoProbe;
    private static bool _hubProbePending;
    private static bool _attachFlowPending;
    private static bool _backupLoadPending;
    private static bool _attachDone;
    private static float _verifyAt = -1f;
    private static float _secondBattleAt = -1f;
    private static float _diagAt = -1f;
    private static float _syncAt = -1f;
    private static int _lastSyncedTo = -1;
    private static int _arenaLocalWave = -1;
    private static int _lastNetStateVersion;
    private static bool _resultHandled;

    // Stage 2: the native Start Battle call is held until every player is
    // ready and the host has sent go. Nothing clears the hand or forces combat.
    private static bool _startBattleHeld;
    private static int _barrierStartedWave = -1;
    private static int _battleReleasedWave = -1;
    private static int _roundAppliedWave = -1;
    private static int _lastObservedWave = -1;
    private static int _combatActiveWave = -1;
    private static int _waveEndReportedWave = -1;
    private static int _startWaveHeldWave = -1;
    private static int _lastNativeLifeWave = -1;
    private static string _lastNativeLifeReason;
    private static int _lastEnemyBaseHp = -1;

    private static int _verifySamples;
    private float _autoAt = -1f;
    private static bool _configStartDone;
    private static float _attachRetryAt = -1f;
    private float _heartbeatAt = -1f;
    private float _botArmyAt = -1f;
    private int _qaBattleWave = -1;

    public VersusBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    public static void Install()
    {
        ClassInjector.RegisterTypeInIl2Cpp<VersusBehaviour>();
        var go = new GameObject("NineKingsVersus");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<VersusBehaviour>();
        VersusPlugin.Log.LogInfo("versus probe ready: Ctrl+Alt+F9 diagnose, F7 capture, F8 inject");
    }

    private void Start()
    {
        _autoProbe = System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "auto-probe"));
        if (_autoProbe)
        {
            _autoAt = Time.time + 12f;
            _heartbeatAt = Time.time + 30f;
            VersusPlugin.Log.LogInfo("auto-probe enabled: diagnose in 12s");
        }
    }

    private void Update()
    {
        TrackScene();
        VersusNet.Tick();
        Stage2Pump();
        QaBotArmy();
        LocalSessionProbe.PumpDiplomatView();
        LocalSessionProbe.PumpEscape();

        var probeUi = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-ui-getters");
        if (System.IO.File.Exists(probeUi))
        {
            System.IO.File.Delete(probeUi);
            LocalSessionProbe.ProbeUiGetters();
        }

        var clickExit = System.IO.Path.Combine(VersusPlugin.DataDir, "click-exit");
        if (System.IO.File.Exists(clickExit))
        {
            System.IO.File.Delete(clickExit);
            LocalSessionProbe.ClickPauseButton("MenuBtn");
        }

        var clickPerks = System.IO.Path.Combine(VersusPlugin.DataDir, "click-perks");
        if (System.IO.File.Exists(clickPerks))
        {
            System.IO.File.Delete(clickPerks);
            LocalSessionProbe.ClickPauseButton("PerksBtn");
        }

        var clickOptions = System.IO.Path.Combine(VersusPlugin.DataDir, "click-options");
        if (System.IO.File.Exists(clickOptions))
        {
            System.IO.File.Delete(clickOptions);
            LocalSessionProbe.ClickPauseButton("OptionsBtn");
        }

        var clickResume = System.IO.Path.Combine(VersusPlugin.DataDir, "click-resume");
        if (System.IO.File.Exists(clickResume))
        {
            System.IO.File.Delete(clickResume);
            LocalSessionProbe.ClickPauseButton("ResumeBtn");
        }

        var facesCheck = System.IO.Path.Combine(VersusPlugin.DataDir, "faces-check");
        if (System.IO.File.Exists(facesCheck))
        {
            System.IO.File.Delete(facesCheck);
            LocalSessionProbe.CheckEnemyFaces();
        }

        var pauseTree = System.IO.Path.Combine(VersusPlugin.DataDir, "dump-pause-tree");
        if (System.IO.File.Exists(pauseTree))
        {
            System.IO.File.Delete(pauseTree);
            LocalSessionProbe.DumpPauseHierarchy();
        }

        var testTimescale = System.IO.Path.Combine(VersusPlugin.DataDir, "test-timescale");
        if (System.IO.File.Exists(testTimescale))
        {
            System.IO.File.Delete(testTimescale);
            UnityEngine.Time.timeScale = UnityEngine.Time.timeScale == 0f ? 1f : 0f;
            VersusPlugin.Log.LogInfo($"test-timescale: scale={UnityEngine.Time.timeScale}");
        }

        var nativeEscape = System.IO.Path.Combine(VersusPlugin.DataDir, "test-escape-native");
        if (System.IO.File.Exists(nativeEscape))
        {
            System.IO.File.Delete(nativeEscape);
            LocalSessionProbe.TryNativePause();
        }

        var forceEscape = System.IO.Path.Combine(VersusPlugin.DataDir, "test-escape");
        if (System.IO.File.Exists(forceEscape))
        {
            System.IO.File.Delete(forceEscape);
            LocalSessionProbe.TogglePause();
        }

        if (_pendingEndType.HasValue && _endRetryAt > 0f && Time.time >= _endRetryAt)
        {
            _endRetryAt = -1f;
            var pending = _pendingEndType.Value;
            ShowOfficialEnd(pending);
        }

        // custom arena disabled: returning to official Gameplay flow

        var dumpTroops = System.IO.Path.Combine(VersusPlugin.DataDir, "dump-troops");
        if (System.IO.File.Exists(dumpTroops))
        {
            System.IO.File.Delete(dumpTroops);
            TroopCatalog.Dump();
        }

        var dumpPerks = System.IO.Path.Combine(VersusPlugin.DataDir, "dump-perks");
        if (System.IO.File.Exists(dumpPerks))
        {
            System.IO.File.Delete(dumpPerks);
            LocalSessionProbe.DumpPerks();
        }

        var dumpFresh = System.IO.Path.Combine(VersusPlugin.DataDir, "dump-fresh");
        if (System.IO.File.Exists(dumpFresh))
        {
            System.IO.File.Delete(dumpFresh);
            LocalSessionProbe.DumpFreshSnapshot();
        }

        // The pre-match year/enemies splash is now the game's own YearShowcase
        // scene, loaded by Wave.StartNewGame before Gameplay. Nothing is painted
        // or forced here.

        var clickStart = System.IO.Path.Combine(VersusPlugin.DataDir, "click-start");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(clickStart))
        {
            System.IO.File.Delete(clickStart);
            LocalSessionProbe.ClickMainMenuStart();
        }

        var probeStartNewGame = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-startnewgame");
        if (System.IO.File.Exists(probeStartNewGame))
        {
            System.IO.File.Delete(probeStartNewGame);
            LocalSessionProbe.StartMultiplayerRun("Nothing");
        }

        var showMpScreen = System.IO.Path.Combine(VersusPlugin.DataDir, "show-mp-screen");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(showMpScreen))
        {
            System.IO.File.Delete(showMpScreen);
            LocalSessionProbe.ShowMpScreen();
        }

        if (VersusConfig.Current?.HasFlag("skip-intro") == true)
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (active == "SplashScreen" || active == "IntroductionVideo")
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
            }
        }

        var nativeVersus = System.IO.Path.Combine(VersusPlugin.DataDir, "native-versus-menu");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(nativeVersus))
        {
            System.IO.File.Delete(nativeVersus);
            LocalSessionProbe.ShowNativeVersusMenu();
        }

        var probePause = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-pausemenu");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(probePause))
        {
            System.IO.File.Delete(probePause);
            LocalSessionProbe.ProbePauseMenu();
        }

        var probeMpUi = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-mpui");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(probeMpUi))
        {
            System.IO.File.Delete(probeMpUi);
            LocalSessionProbe.ProbeMpUi();
        }

        var nativeMenu = System.IO.Path.Combine(VersusPlugin.DataDir, "native-menu");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(nativeMenu))
        {
            System.IO.File.Delete(nativeMenu);
            LocalSessionProbe.ShowNativeMenu();
        }

        var avatarBar = System.IO.Path.Combine(VersusPlugin.DataDir, "avatar-bar");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(avatarBar))
        {
            System.IO.File.Delete(avatarBar);
            LocalSessionProbe.BuildAvatarBar();
        }

        var populateFaces = System.IO.Path.Combine(VersusPlugin.DataDir, "populate-faces");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(populateFaces))
        {
            System.IO.File.Delete(populateFaces);
            LocalSessionProbe.PopulateFaces();
        }

        var multiPreview = System.IO.Path.Combine(VersusPlugin.DataDir, "multi-preview");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(multiPreview))
        {
            System.IO.File.Delete(multiPreview);
            LocalSessionProbe.ShowMultiplePreviews();
        }

        var showPreview = System.IO.Path.Combine(VersusPlugin.DataDir, "show-preview");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(showPreview))
        {
            System.IO.File.Delete(showPreview);
            LocalSessionProbe.ShowOpponentPreview();
        }

        var dumpAreas = System.IO.Path.Combine(VersusPlugin.DataDir, "dump-areas");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(dumpAreas))
        {
            System.IO.File.Delete(dumpAreas);
            try
            {
                var areas = UnityEngine.Object.FindObjectsOfType<Core.Kingdoms.KingdomArea>();
                VersusPlugin.Log.LogInfo($"dump-areas: total={areas?.Length ?? 0}");
                if (areas != null)
                {
                    foreach (var area in areas)
                    {
                        if (area == null)
                        {
                            continue;
                        }

                        var go = area.gameObject;
                        VersusPlugin.Log.LogInfo(
                            $"dump-areas: {area.name} pos={go.transform.position} active={go.activeInHierarchy}");
                    }
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"dump-areas failed: {e.Message}");
            }
        }

        var refreshMap = System.IO.Path.Combine(VersusPlugin.DataDir, "refresh-map");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(refreshMap))
        {
            System.IO.File.Delete(refreshMap);
            try
            {
                var map = Core.Wave.MultiplayerMap;
                if (map != null && !map.activeSelf)
                {
                    map.SetActive(true);
                    VersusPlugin.Log.LogInfo("refresh-map: MultiplayerMap activated");
                }

                var opponent = LocalSessionProbe.GetOpponentSnapshot();
                if (opponent != null)
                {
                    Core.Wave.CurrentMultiplayerEnemySnapshot = opponent;
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"refresh-map setup failed: {e.Message}");
            }

            InvokeRefreshMap();
        }

        var dumpMenu = System.IO.Path.Combine(VersusPlugin.DataDir, "dump-menu");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(dumpMenu))
        {
            System.IO.File.Delete(dumpMenu);
            try
            {
                var buttons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
                VersusPlugin.Log.LogInfo($"dump-menu: buttons={buttons?.Length ?? 0}");
                if (buttons != null)
                {
                    foreach (var button in buttons)
                    {
                        if (button == null)
                        {
                            continue;
                        }

                        var label = button.GetComponentInChildren<UnityEngine.UI.Text>();
                        VersusPlugin.Log.LogInfo(
                            $"dump-menu: button '{button.name}' label='{(label == null ? "" : label.text)}' " +
                            $"parent='{PathOf(button.transform)}' active={button.gameObject.activeInHierarchy}");
                    }
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"dump-menu failed: {e.Message}");
            }
        }

        if (_lastScene == "MainMenu")
        {
            VersusNativeMenu.TryInstallMainMenuButton();
        }

        var menuFlag = System.IO.Path.Combine(VersusPlugin.DataDir, "menu");
        if (System.IO.File.Exists(menuFlag))
        {
            System.IO.File.Delete(menuFlag);
            VersusNativeMenu.Toggle();
        }

        VersusNativeMenu.Refresh();

        // Rebuild the opponent's board when a fresh state arrives and the run is not
        // mid-combat (rebuilding during combat would despawn the fighting units).
        if (VersusNet.StateVersion != _lastNetStateVersion)
        {
            _lastNetStateVersion = VersusNet.StateVersion;
            var data = Core.Wave.Data;
            if (_lastScene == "Gameplay" && data != null && !data.Running &&
                VersusConfig.Current?.HasFlag("arena") != true)
            {
                VersusPlugin.Log.LogInfo("net: rebuilding opponent board from peer state");
                LocalSessionProbe.BuildEnemyBoard();
            }
        }

        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave > 0 &&
            VersusConfig.Current?.HasFlag("arena") == true &&
            CleanArena.LastWave != Core.Wave.Data.Wave && _arenaLocalWave != Core.Wave.Data.Wave)
        {
            var currentWave = Core.Wave.Data.Wave;
            _arenaLocalWave = currentWave;
            var opponent = LocalSessionProbe.GetOpponentSnapshot();
            if (opponent != null)
            {
                CleanArena.InstallWaveTroops(opponent);
                if (!VersusNet.Connected)
                {
                    BeginVersusBattle("arena-local");
                }
            }
            else
            {
                VersusPlugin.Log.LogWarning("arena-local: no opponent snapshot");
            }
        }

        if (VersusNet.Connected && _lastScene == "Gameplay" && Core.Wave.Data != null
            && Time.time >= _syncAt && !_resultHandled)
        {
            _syncAt = Time.time + 1.5f;
            var waveNow = Core.Wave.Data.Wave;
            if (VersusNet.Mode == "host")
            {
                VersusNet.SendHostWave(waveNow);
            }
            else if (VersusNet.HostWave > 0 && VersusNet.HostWave != waveNow && _lastSyncedTo != VersusNet.HostWave)
            {
                _lastSyncedTo = VersusNet.HostWave;
                VersusPlugin.Log.LogInfo($"sync: join SetWave from {waveNow} to host wave {_lastSyncedTo}");
                Core.Wave.SetWave(_lastSyncedTo, true, false, false);
            }
        }

        if (_lastScene == "Gameplay" && Time.time >= _diagAt &&
            VersusConfig.Current?.HasFlag("arena") == true)
        {
            _diagAt = Time.time + 5f;
            try
            {
                var data = Core.Wave.Data;
                var entities = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
                var enemies = 0;
                var players = 0;
                var multiplayer = 0;
                if (entities != null)
                {
                    foreach (var entity in entities)
                    {
                        try
                        {
                            if (entity == null)
                            {
                                continue;
                            }

                            if (entity.IsEnemy)
                            {
                                enemies++;
                            }
                            else
                            {
                                players++;
                            }

                            if (entity.IsMultiplayerEnemy)
                            {
                                multiplayer++;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                VersusPlugin.Log.LogInfo(
                    $"diag: wave={data?.Wave} running={data?.Running} combatOn={data?.CombatOn} " +
                    $"waitingToBegin={data?.WaitingToBegin} waitingForEnemies={data?.waitingForEnemiesToSpawn} " +
                    $"entities={entities?.Length ?? 0} players={players} enemies={enemies} multiplayer={multiplayer} " +
                    $"arenaSpawned={OpponentDeck.LastBattlers} arenaWave={OpponentDeck.LastWave} " +
                    $"lives={VersusNet.LocalLives}/{VersusNet.PeerLives} states={VersusNet.StateVersion} " +
                    $"resultHandled={_resultHandled} phase={Match.Phase} " +
                    $"waveEnemies={Core.Wave.Enemies?.Count.ToString() ?? "null"} validTroops={Core.Wave.ValidTroops?.Count ?? -1}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"diag failed: {e.Message}");
            }
        }

        var sessionProbe = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-session");
        if ((_lastScene == "MainMenu" || _lastScene == "MultiplayerHub") && System.IO.File.Exists(sessionProbe))
        {
            System.IO.File.Delete(sessionProbe);
            LocalSessionProbe.Run();
        }

        // Deferred: LoadScene may not activate the hub scene within this frame, so the
        // population probe waits until MultiplayerHub is the active scene.
        if (_hubProbePending && _lastScene == "MultiplayerHub")
        {
            _hubProbePending = false;
            LocalSessionProbe.Run();
        }

        // probe-hub: load the MultiplayerHub scene (index 9) from the main menu so
        // MultiplayerManager wakes up, then re-run the population probe there once
        // the scene is active.
        var hubProbe = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-hub");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(hubProbe))
        {
            System.IO.File.Delete(hubProbe);
            try
            {
                VersusPlugin.Log.LogInfo("probe-hub: loading MultiplayerHub scene (index 9), probe deferred until scene active");
                UnityEngine.SceneManagement.SceneManager.LoadScene(9);
                _hubProbePending = true;
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"probe-hub failed: {e.GetType().Name}: {e.Message}");
            }
        }

        if (_autoProbe && Time.time >= _heartbeatAt)        {
            _heartbeatAt = Time.time + 30f;
            VersusPlugin.Log.LogInfo($"heartbeat scene={_lastScene} t={Time.time:F0}");
        }

        if (_autoAt > 0f && Time.time >= _autoAt)
        {
            _autoAt = -1f;
            Diagnose("auto");
            ProbeCommands();
            if (VersusConfig.Current?.HasFlag("auto-start") == true)
            {
                TryStartRun();
            }
        }

        // attach-flow: load the hub scene so MultiplayerManager.Instance exists, attach
        // the probe session there, then start a run through Core.Wave.StartNewGame with
        // its own isMultiplayer=true parameter (avoids flipping IsMultiplayer mid-run).
        // Config-driven startup: a match can be launched entirely from config.json
        // (mode + autoStart). The old one-shot files still work as overrides.
        var config = VersusConfig.Current;
        if (_lastScene == "MainMenu" && !_configStartDone && config != null)
        {
            var configMode = config.mode == "join" ? "join" : config.mode == "host" ? "host" : null;
            if (config.autoStart && configMode != null)
            {
                _configStartDone = true;
                VersusPlugin.Log.LogInfo($"config: auto-starting versus match as {configMode}");
                StartVersusMatch(configMode);
            }
        }

        var menuStart = System.IO.Path.Combine(VersusPlugin.DataDir, "menu-start-host");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(menuStart))
        {
            System.IO.File.Delete(menuStart);
            StartVersusMatch("host");
        }

        var menuStartJoin = System.IO.Path.Combine(VersusPlugin.DataDir, "menu-start-join");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(menuStartJoin))
        {
            System.IO.File.Delete(menuStartJoin);
            StartVersusMatch("join");
        }

        var attachFlow = System.IO.Path.Combine(VersusPlugin.DataDir, "attach-flow");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(attachFlow))
        {
            System.IO.File.Delete(attachFlow);
            _attachFlowPending = true;
            VersusPlugin.Log.LogInfo("attach-flow: loading hub so MultiplayerManager wakes before run start");
            UnityEngine.SceneManagement.SceneManager.LoadScene(9);
        }
        if (_attachFlowPending && _lastScene == "MultiplayerHub")
        {
            _attachFlowPending = false;
            VersusPlugin.Log.LogInfo("attach-flow: hub active, attaching session and starting multiplayer run");
            if (LocalSessionProbe.AttachForMultiplayerStart(out var officialKing))
            {
                // Official path: Wave.StartNewGame(isMultiplayer=true). The NRE was a
                // null Nullable<int> seed; passing a real seed makes it work and the
                // game initializes all its own UI (avatar bar, splash, enemy base).
                if (VersusConfig.Current?.HasFlag("bot-army") == true)
                {
                    // QA only: LoadGame honours the snapshot's troop areas, so the
                    // local side has a real army. StartNewGame ignores them.
                    LocalSessionProbe.LoadSeededRun();
                }
                else
                {
                    var shortKing = (officialKing ?? "KingOfNothing").Replace("KingOf", string.Empty);
                    LocalSessionProbe.StartMultiplayerRun(shortKing);
                    LocalSessionProbe.ApplyChosenPerks();
                }
            }
            else
            {
                VersusPlugin.Log.LogWarning("attach-flow: session not attached, run not started");
            }
        }

        var startBattle = System.IO.Path.Combine(VersusPlugin.DataDir, "start-battle");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave >= 0
            && System.IO.File.Exists(startBattle))
        {
            System.IO.File.Delete(startBattle);
            BeginVersusBattle("start-battle");
        }

        // Stage 2: readiness is announced from WaveStart, the native Start Battle
        // call is held by a prefix until go, and the round result comes from the
        // game's own end-of-wave life report. No timer, no hand clearing, no parity.

        // wave-begin: use the hand UI's own Wave_Begin hook after satisfying the
        // hand-card gate (<=2 cards, 0 on final round).
        var waveBegin = System.IO.Path.Combine(VersusPlugin.DataDir, "wave-begin");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave >= 1
            && System.IO.File.Exists(waveBegin))
        {
            System.IO.File.Delete(waveBegin);
            try
            {
                var data = Core.Wave.Data;
                var hand = GameplayInterface.GameplayUI.HandCards;
                VersusPlugin.Log.LogInfo($"wave-begin: wave={data.Wave} cards={hand?.CardsCount.ToString() ?? "n/a"} " +
                    $"getStartState={hand?.GetStartState().ToString() ?? "n/a"} " +
                    $"running={data.Running} combatOn={data.CombatOn} waitingForEnemies={data.waitingForEnemiesToSpawn}");
                hand?.Clear();
                hand?.Wave_Begin();
                VersusPlugin.Log.LogInfo($"wave-begin: Wave_Begin invoked; running={data.Running} combatOn={data.CombatOn} " +
                    $"waitingForEnemies={data.waitingForEnemiesToSpawn} cards={hand?.CardsCount.ToString() ?? "n/a"}");
                ScheduleVerification(10);
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"wave-begin failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // begin-combat: satisfy the game's hand-card gate (<=2 cards, 0 on final round)
        // and then start combat through the game's own API.
        var beginCombat = System.IO.Path.Combine(VersusPlugin.DataDir, "begin-combat");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave >= 1
            && System.IO.File.Exists(beginCombat))
        {
            System.IO.File.Delete(beginCombat);
            try
            {
                var data = Core.Wave.Data;
                var hand = GameplayInterface.GameplayUI.HandCards;
                VersusPlugin.Log.LogInfo($"begin-combat: wave={data.Wave} cards={hand?.CardsCount.ToString() ?? "n/a"} " +
                    $"running={data.Running} combatOn={data.CombatOn} waitingForEnemies={data.waitingForEnemiesToSpawn} " +
                    $"snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")}");

                if (hand != null && hand.CardsCount > 0)
                {
                    hand.Clear();
                }

                data.SetWaitingToBegin(false);
                data.SetWaitingToOpenUI(false);
                data.SetRunning(true);
                Core.Wave.StartCombat(false);
                VersusPlugin.Log.LogInfo($"begin-combat: after running={data.Running} combatOn={data.CombatOn} " +
                    $"waitingForEnemies={data.waitingForEnemiesToSpawn}");
                ScheduleVerification(10);
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"begin-combat failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // empty-hand: the base game waits for the hand to be reduced (<=2 cards; 0 on
        // the final round) before combat begins. Clear it and see whether the staged
        // wave starts on its own.
        var emptyHand = System.IO.Path.Combine(VersusPlugin.DataDir, "empty-hand");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave >= 1
            && System.IO.File.Exists(emptyHand))
        {
            System.IO.File.Delete(emptyHand);
            try
            {
                var hand = GameplayInterface.GameplayUI.HandCards;
                VersusPlugin.Log.LogInfo($"empty-hand: before cards={hand?.CardsCount.ToString() ?? "n/a"} " +
                    $"running={Core.Wave.Data.Running} combatOn={Core.Wave.Data.CombatOn} " +
                    $"waitingForEnemies={Core.Wave.Data.waitingForEnemiesToSpawn}");
                hand?.Clear();
                VersusPlugin.Log.LogInfo($"empty-hand: after cards={hand?.CardsCount.ToString() ?? "n/a"}");
                ScheduleVerification(8);
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"empty-hand failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // build-enemy: recreate the opponent's kingdom from their snapshot using the
        // game's own SetMultiplayerCard / SetMultiplayerLife commands.
        var buildEnemy = System.IO.Path.Combine(VersusPlugin.DataDir, "build-enemy");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave >= 1
            && System.IO.File.Exists(buildEnemy))
        {
            System.IO.File.Delete(buildEnemy);
            try
            {
                var map = Core.Wave.MultiplayerMap;
                if (map != null && !map.activeSelf)
                {
                    map.SetActive(true);
                    VersusPlugin.Log.LogInfo("build-enemy: MultiplayerMap activated");
                }

                LocalSessionProbe.BuildEnemyBoard();
                ScheduleVerification(8);
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"build-enemy flag failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // spawn-probe: test the custom-arena army spawner with snapshot troops.
        var spawnProbe = System.IO.Path.Combine(VersusPlugin.DataDir, "spawn-probe");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(spawnProbe))
        {
            System.IO.File.Delete(spawnProbe);
            LocalSessionProbe.SpawnProbe();
        }

        // inspect-spawn: one-shot dump of spawn API types and writability.
        var inspectSpawn = System.IO.Path.Combine(VersusPlugin.DataDir, "inspect-spawn");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(inspectSpawn))
        {
            System.IO.File.Delete(inspectSpawn);
            LocalSessionProbe.InspectSpawnApi();
        }

        // load-backup: load an authentic multiplayer save (wave 32, king Spells) from
        // the read-only backup, with no fabricated session, to observe the game's own
        // multiplayer wave behaviour as a baseline.
        var loadBackup = System.IO.Path.Combine(VersusPlugin.DataDir, "load-backup");
        if (_lastScene == "MainMenu" && System.IO.File.Exists(loadBackup))
        {
            System.IO.File.Delete(loadBackup);
            _backupLoadPending = true;
            VersusPlugin.Log.LogInfo("load-backup: loading hub before authentic multiplayer save");
            UnityEngine.SceneManagement.SceneManager.LoadScene(9);
        }
        if (_backupLoadPending && _lastScene == "MultiplayerHub")
        {
            _backupLoadPending = false;
            VersusPlugin.Log.LogInfo("load-backup: hub active, loading authentic multiplayer save");
            LocalSessionProbe.LoadBackupRun();
        }

        // Verification only: once a Gameplay run is live, report session state once.
        if (!_attachDone && _lastScene == "Gameplay" && Time.time >= _attachRetryAt)
        {
            _attachRetryAt = Time.time + 5f;
            var session = MultiplayerSystems.MultiplayerManager.Session;
            VersusPlugin.Log.LogInfo(
                $"attach-flow watch: session={(session == null ? "null" : $"gauntlet={session.GauntletEnemies?.Count ?? -1} active={session.GauntletActive}")} " +
                $"runMultiplayer={(Core.Wave.Data == null ? "no-data" : Core.Wave.Data.IsMultiplayer.ToString())}");
            if (session != null && Core.Wave.Data != null && Core.Wave.Data.IsMultiplayer)
            {
                _attachDone = true;
                VersusPlugin.Log.LogInfo("attach-flow watch: multiplayer run live; waiting for wave start");
            }
        }

        // next-wave: advance the live multiplayer run through the game's own SetWave
        // flow (PrepareMultiplayerGauntlet -> SpawnWave -> StartCombat with the
        // opponent snapshot injected at those entry points), then sample for a while.
        var nextWave = System.IO.Path.Combine(VersusPlugin.DataDir, "next-wave");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null
            && Core.Wave.Data.Wave >= 1 && Core.Wave.Data.IsMultiplayer
            && System.IO.File.Exists(nextWave))
        {
            System.IO.File.Delete(nextWave);
            try
            {
                var data = Core.Wave.Data;
                var target = data.Wave + 1;
                VersusPlugin.Log.LogInfo(
                    $"next-wave: from wave={data.Wave} running={data.Running} combatOn={data.CombatOn} " +
                    $"snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")}");
                LocalSessionProbe.EnsureEnemySnapshot("next-wave");
                Core.Wave.SetWave(target, true, false, false);
                VersusPlugin.Log.LogInfo($"next-wave: SetWave({target}, skipShop=true, showUIs=false) invoked");
                ScheduleVerification(12);
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"next-wave failed: {e.GetType().Name}: {e.Message}");
            }
        }

        if (_secondBattleAt > 0f && Time.time >= _secondBattleAt)
        {
            _secondBattleAt = -1f;
            if (_resultHandled)
            {
                return;
            }

            try
            {
                var view = UnityEngine.Object.FindObjectOfType<Core.GameplayUI_PlacingView>();
                var data = Core.Wave.Data;
                var hand = GameplayInterface.GameplayUI.HandCards;
                VersusPlugin.Log.LogInfo($"start-battle2: cards={hand?.CardsCount.ToString() ?? "n/a"} " +
                    $"running={data?.Running} combatOn={data?.CombatOn} waitingToBegin={data?.WaitingToBegin} " +
                    $"startState={view?.GetStartState().ToString() ?? "n/a"}");
                if (hand != null && hand.CardsCount > 0)
                {
                    hand.Clear();
                }

                var method = typeof(Core.GameplayUI_PlacingView).GetMethod("StartBattle",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                method?.Invoke(view, null);
                data?.SetWaitingToBegin(false);
                data?.SetRunning(true);
                // StartBattle sets WaitingForEnemiesToSpawn=true to make the game
                // spawn the wave, but SpawnWave has already run in our attached run,
                // so nothing clears it and the combat coroutine waits forever.
                if (data != null)
                {
                    data.WaitingForEnemiesToSpawn = false;
                }

                Core.Wave.StartCombat(false);
                VersusPlugin.Log.LogInfo($"start-battle2: after running={data?.Running} combatOn={data?.CombatOn} " +
                    $"waitingToBegin={data?.WaitingToBegin} waitingForEnemies={data?.waitingForEnemiesToSpawn}");
                ScheduleVerification(8);
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"start-battle2 failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
            }
        }

        if (_verifyAt > 0f && Time.time >= _verifyAt)
        {
            _verifyAt = -1f;
            try
            {
                VersusPlugin.Log.LogInfo(
                    $"verify: wave={Core.Wave.Data?.Wave} running={Core.Wave.Data?.Running} waitingToBegin={Core.Wave.Data?.WaitingToBegin} " +
                    $"combatOn={Core.Wave.Data?.CombatOn} spawning={Core.Wave.Data?.SpawningEnemies} alliesSpawning={Core.Wave.Data?.AreasSpawningAllies} " +
                    $"snapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")} " +
                    $"map={(Core.Wave.MultiplayerMap == null ? "null" : Core.Wave.MultiplayerMap.activeSelf ? "active" : "inactive")} " +
                    $"enemyTroops={Core.Kingdoms.KingdomAreaManager.GetAreasWithTroops(true)?.Count ?? -1} " +
                    $"enemyBig={Core.Kingdoms.KingdomAreaManager.GetAreasWithAnythingPlaced(true)?.Count ?? -1} " +
                    $"playerTroops={Core.Kingdoms.KingdomAreaManager.GetAreasWithTroops(false)?.Count ?? -1} " +
                    $"handCards={(GameplayInterface.GameplayUI.HandCards == null ? "n/a" : GameplayInterface.GameplayUI.HandCards.CardsCount.ToString())} " +
                    $"waitingForEnemies={Core.Wave.Data?.waitingForEnemiesToSpawn}");

                var entities = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
                var enemyEntities = 0;
                var myEntities = 0;
                var multiplayerEnemies = 0;
                if (entities != null)
                {
                    foreach (var entity in entities)
                    {
                        try
                        {
                            if (entity.IsEnemy)
                            {
                                enemyEntities++;
                            }
                            else
                            {
                                myEntities++;
                            }

                            if (entity.IsMultiplayerEnemy)
                            {
                                multiplayerEnemies++;
                            }
                        }
                        catch
                        {
                            // Entity may be mid-destroy; skip.
                        }
                    }
                }

                VersusPlugin.Log.LogInfo(
                    $"verify entities: total={entities?.Length ?? 0} player={myEntities} enemy={enemyEntities} multiplayerEnemy={multiplayerEnemies} " +
                    $"waveTroops={Core.Wave.WaveTroops?.Length ?? -1} validTroops={Core.Wave.ValidTroops?.Count ?? -1}");

                if (_verifySamples > 0)
                {
                    _verifySamples--;
                    if (_verifySamples > 0)
                    {
                        _verifyAt = Time.time + 10f;
                    }
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"verify failed: {e.Message}");
            }
        }

        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        // The game itself binds bare function keys (F8 opens a folder), so probe
        // hotkeys require Ctrl+Alt to stay out of its way.
        var ctrlAlt = keyboard[UnityEngine.InputSystem.Key.LeftCtrl].isPressed
                      && keyboard[UnityEngine.InputSystem.Key.LeftAlt].isPressed;
        if (!ctrlAlt)
        {
            return;
        }

        if (keyboard[UnityEngine.InputSystem.Key.F11].wasPressedThisFrame)
        {
            VersusNativeMenu.Toggle();
            return;
        }

        if (keyboard[UnityEngine.InputSystem.Key.F9].wasPressedThisFrame)
        {
            Diagnose("F9");
        }
        else if (keyboard[UnityEngine.InputSystem.Key.F10].wasPressedThisFrame)
        {
            TryStartRun();
        }
        else if (keyboard[UnityEngine.InputSystem.Key.F7].wasPressedThisFrame)
        {
            Capture();
        }
        else if (keyboard[UnityEngine.InputSystem.Key.F8].wasPressedThisFrame)
        {
            VersusPlugin.Log.LogWarning("F8 injection disabled: local multiplayer session setup is not yet validated");
        }
        else if (keyboard[UnityEngine.InputSystem.Key.F6].wasPressedThisFrame)
        {
            ProbeCommands();
        }
    }

    private static void TrackScene()
    {
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                var hubManager = UnityEngine.Object.FindObjectOfType<MultiplayerSystems.MultiplayerManager>();
                VersusPlugin.Log.LogInfo($"scene changed: {scene} multiplayerManager={(hubManager == null ? "absent" : "present")} t={Time.realtimeSinceStartup:0.00}");
            }
        }
        catch
        {
            // SceneManager can be unavailable during teardown; ignore.
        }
    }

    private static void Diagnose(string source)
    {
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            VersusPlugin.Log.LogInfo($"[{source}] scene={scene}");

            var console = UnityEngine.Object.FindObjectOfType<Console.GameConsole>();
            VersusPlugin.Log.LogInfo($"[{source}] GameConsole={(console == null ? "none" : "present")}");

            var difficulties = Resources.FindObjectsOfTypeAll<Difficulty.DifficultyLevelSO>();
            VersusPlugin.Log.LogInfo($"[{source}] difficulty assets={difficulties?.Length ?? 0}");
            if (difficulties != null)
            {
                for (var i = 0; i < difficulties.Length && i < 12; i++)
                {
                    var d = difficulties[i];
                    string localized;
                    try { localized = d?.GetLocalizedName(); }
                    catch { localized = "?"; }
                    VersusPlugin.Log.LogInfo($"[{source}]   difficulty[{i}] asset='{d?.name}' name='{d?.Name}' localized='{localized}'");
                }
            }

            var manager = UnityEngine.Object.FindObjectOfType<Gameplay.GameplayManager>();
            VersusPlugin.Log.LogInfo($"[{source}] GameplayManager={(manager == null ? "none" : "present")}");
            VersusPlugin.Log.LogInfo($"[{source}] Wave.Instance={(Core.Wave.Instance == null ? "none" : "present")}");
            VersusPlugin.Log.LogInfo($"[{source}] Wave.multiplayerEnemySnapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")}");
            VersusPlugin.Log.LogInfo($"[{source}] cached snapshot={(_cached == null ? "null" : "set")}");

            ReportConsoleState();
            LogMultiplayerState(source);

            if (Core.Wave.Instance == null)
            {
                VersusPlugin.Log.LogInfo($"[{source}] no Wave instance, skipping snapshot capture");
                return;
            }

            var snapshot = Gameplay.GameSnapshot.CaptureSnapshot();
            SnapshotDump.Write(snapshot, source);
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"[{source}] diagnose failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void LogMultiplayerState(string source)
    {
        try
        {
            var wave = Core.Wave.Instance;
            if (wave == null)
            {
                VersusPlugin.Log.LogInfo($"[{source}] state: no Wave instance");
                return;
            }

            var map = Core.Wave.MultiplayerMap;
            VersusPlugin.Log.LogInfo(
                $"[{source}] state: enemyBase={(Core.Wave.MultiplayerEnemyBase == null ? "null" : "set")} " +
                $"map={(map == null ? "null" : map.activeSelf ? "active" : "inactive")} " +
                $"enemyWalls={(Core.Wave.MultiplayerEnemyWallsRepresentation == null ? "null" : "set")} " +
                $"previewWalls={(Core.Wave.MultiplayerPreviewWallsRepresentation == null ? "null" : "set")} " +
                $"lifeVisual={(Core.Wave.MultiplayerEnemyLifeVisual == null ? "null" : "set")}");

            var waveType = Core.Wave.GetCurrentWaveType();
            VersusPlugin.Log.LogInfo($"[{source}] state: waveType={waveType} enemySnapshot={(Core.Wave.CurrentMultiplayerEnemySnapshot == null ? "null" : "set")} runIsMultiplayer={(Core.Wave.Data == null ? "no-data" : Core.Wave.Data.IsMultiplayer.ToString())}");

            try
            {
                var playerTroops = Core.Kingdoms.KingdomAreaManager.GetAreasWithTroops(false);
                var enemyTroops = Core.Kingdoms.KingdomAreaManager.GetAreasWithTroops(true);
                var enemyBuildings = Core.Kingdoms.KingdomAreaManager.GetAreasWithBuildings(true, true);
                var enemyAnything = Core.Kingdoms.KingdomAreaManager.GetAreasWithAnythingPlaced(true);
                VersusPlugin.Log.LogInfo(
                    $"[{source}] areas: playerTroops={playerTroops?.Count ?? -1} enemyTroops={enemyTroops?.Count ?? -1} " +
                    $"enemyBuildings={enemyBuildings?.Count ?? -1} enemyAnything={enemyAnything?.Count ?? -1}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"[{source}] area counts failed: {e.GetType().Name}: {e.Message}");
            }

            try
            {
                var playerBase = Core.Wave.PlayerBase;
                var enemyBase = Core.Wave.MultiplayerEnemyBase;
                var cam = UnityEngine.Camera.main;
                VersusPlugin.Log.LogInfo(
                    $"[{source}] positions: player={(playerBase == null ? "null" : playerBase.transform.position.ToString())} " +
                    $"enemy={(enemyBase == null ? "null" : enemyBase.transform.position.ToString())} " +
                    $"camera={(cam == null ? "null" : cam.transform.position.ToString())}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"[{source}] positions failed: {e.GetType().Name}: {e.Message}");
            }

            var manager = UnityEngine.Object.FindObjectOfType<Gameplay.GameplayMultiplayerManager>();
            VersusPlugin.Log.LogInfo($"[{source}] state: GameplayMultiplayerManager={(manager == null ? "none" : "present")}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"[{source}] state probe failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void ReportConsoleState()
    {
        try
        {
            var type = typeof(Console.GameConsole);
            var onScreen = type.GetProperty("OnScreen", BindingFlags.Public | BindingFlags.Static);
            VersusPlugin.Log.LogInfo($"console.OnScreen={(onScreen == null ? "n/a" : onScreen.GetValue(null)?.ToString())}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"console state failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void TryStartRun()
    {
        try
        {
            var selected = Difficulty.DifficultySO.Instance?.GetSelectedDifficulty();
            VersusPlugin.Log.LogInfo($"F10: selected difficulty asset='{selected?.name}' name='{selected?.Name}'");

            var difficulties = Resources.FindObjectsOfTypeAll<Difficulty.DifficultyLevelSO>();
            var difficulty = selected ?? (difficulties != null && difficulties.Length > 0 ? difficulties[0] : null);
            if (difficulty == null)
            {
                VersusPlugin.Log.LogWarning("F10: no DifficultyLevelSO assets found");
                return;
            }

            var key = string.IsNullOrEmpty(difficulty.Name) ? difficulty.name : difficulty.Name;
            VersusPlugin.Log.LogInfo($"F10: trying StartNewGame with difficulty key '{key}' (asset '{difficulty.name}')");

            var kings = new Il2CppSystem.Collections.Generic.List<Core.CardOwnership>();
            Gameplay.GameplayManager.StartNewGame(key, Core.CardOwnership.KingOfNothing, kings);
            VersusPlugin.Log.LogInfo("F10: StartNewGame returned");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"F10: {e}");
        }
    }

    private static void ProbeCommands()
    {
        try
        {
            var result = Console.GameConsoleCommandRegistry.CreateCommands();
            var commands = result?.Commands;
            VersusPlugin.Log.LogInfo($"F6: commands={commands?.Count ?? 0} warnings={result?.Warnings?.Count ?? 0}");

            if (commands != null)
            {
                for (var i = 0; i < commands.Count; i++)
                {
                    var c = commands[i];
                    VersusPlugin.Log.LogInfo($"F6:   {c.GetName()} <- {c.GetType().Name}");
                }
            }

            if (result?.Warnings != null)
            {
                for (var i = 0; i < result.Warnings.Count; i++)
                {
                    VersusPlugin.Log.LogWarning($"F6: warning {result.Warnings[i]}");
                }
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"F6: {e}");
        }
    }

    private static void Capture()
    {
        try
        {
            if (Core.Wave.Instance == null)
            {
                VersusPlugin.Log.LogWarning("F7: no Wave instance (not in a run)");
                return;
            }

            _cached = Gameplay.GameSnapshot.CaptureSnapshot();
            if (_cached == null)
            {
                VersusPlugin.Log.LogWarning("F7: CaptureSnapshot returned null");
                return;
            }

            SnapshotDump.Write(_cached, "f7-capture");
            VersusPlugin.Log.LogInfo($"F7: cached snapshot wave={SafeWave(_cached)}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"F7: {e}");
        }
    }

    private static void Inject()
    {
        try
        {
            if (_cached == null)
            {
                VersusPlugin.Log.LogWarning("F8: nothing cached, press F7 first");
                return;
            }

            var prop = typeof(Core.Wave).GetProperty(
                "CurrentMultiplayerEnemySnapshot",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (prop == null || !prop.CanWrite)
            {
                VersusPlugin.Log.LogWarning($"F8: CurrentMultiplayerEnemySnapshot not writable (prop={(prop == null ? "missing" : "read-only")})");
            }
            else
            {
                prop.SetValue(null, _cached);
                VersusPlugin.Log.LogInfo($"F8: injected cached snapshot wave={SafeWave(_cached)}");
            }

            ShowEnemyBoard();
            LogMultiplayerState("F8");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogError($"F8: {e}");
        }
    }

    private static void ShowEnemyBoard()
    {
        try
        {
            var map = Core.Wave.MultiplayerMap;
            if (map != null && !map.activeSelf)
            {
                map.SetActive(true);
                VersusPlugin.Log.LogInfo("F8: MultiplayerMap activated");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"F8: activating MultiplayerMap failed: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            var enemyBase = Core.Wave.MultiplayerEnemyBase;
            if (enemyBase != null && !enemyBase.gameObject.activeSelf)
            {
                enemyBase.gameObject.SetActive(true);
                VersusPlugin.Log.LogInfo("F8: MultiplayerEnemyBase activated");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"F8: activating MultiplayerEnemyBase failed: {e.GetType().Name}: {e.Message}");
        }

        InvokeRefreshMap();
        TryEnterEnemyMapView();
    }

    private static void TryEnterEnemyMapView()
    {
        try
        {
            var manager = UnityEngine.Object.FindObjectOfType<Gameplay.GameplayMultiplayerManager>();
            if (manager == null)
            {
                VersusPlugin.Log.LogInfo("F8: no GameplayMultiplayerManager in scene");
                return;
            }

            var run = new MultiplayerSystems.MultiplayerRunData
            {
                playerId = 1,
                kingId = (int)Core.CardOwnership.KingOfSpells,
                gameVersion = _cached?.Version ?? "local",
                snapshot = new MultiplayerSystems.MultiplayerGameSnapshots(),
            };
            run.snapshot.Add(_cached);

            Core.Entities.KingSo king = null;
            var tryGet = typeof(Gameplay.GameplayMultiplayerManager).GetMethod(
                "TryGetKing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (tryGet != null)
            {
                var args = new object[] { run.kingId, null };
                var found = (bool)tryGet.Invoke(null, args);
                king = args[1] as Core.Entities.KingSo;
                VersusPlugin.Log.LogInfo($"F8: TryGetKing({run.kingId})={found} king={(king == null ? "null" : king.name)}");
            }
            else
            {
                VersusPlugin.Log.LogWarning("F8: TryGetKing not found");
            }

            var toggle = typeof(Gameplay.GameplayMultiplayerManager).GetMethod(
                "ToggleEnemyMapPreview", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (toggle != null)
            {
                try
                {
                    toggle.Invoke(manager, new object[] { run });
                    VersusPlugin.Log.LogInfo("F8: ToggleEnemyMapPreview invoked");
                    return;
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"F8: ToggleEnemyMapPreview failed: {e.GetType().Name}: {e.InnerException?.Message ?? e.Message}");
                }
            }
            else
            {
                VersusPlugin.Log.LogWarning("F8: ToggleEnemyMapPreview not found");
            }

            VersusPlugin.Log.LogInfo("F8: trying EnterEnemyMapView");

            var enter = typeof(Gameplay.GameplayMultiplayerManager).GetMethod(
                "EnterEnemyMapView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (enter == null)
            {
                VersusPlugin.Log.LogWarning("F8: EnterEnemyMapView not found");
                return;
            }

            enter.Invoke(manager, new object[] { run, king, _cached });
            VersusPlugin.Log.LogInfo("F8: EnterEnemyMapView invoked");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"F8: enemy map view failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void InvokeRefreshMap()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        System.Reflection.MethodInfo method = null;
        foreach (var candidate in typeof(Core.Wave).GetMethods(flags))
        {
            if (candidate.Name.Contains("RefreshMultiplayer"))
            {
                VersusPlugin.Log.LogInfo($"F8: found {candidate.Name} static={candidate.IsStatic} params={candidate.GetParameters().Length}");
                if (method == null && candidate.IsStatic)
                {
                    method = candidate;
                }
            }
        }

        if (method != null)
        {
            try
            {
                method.Invoke(null, method.GetParameters().Length == 0 ? null : new object[method.GetParameters().Length]);
                VersusPlugin.Log.LogInfo($"RefreshMultiplayerMap invoked ({method.Name})");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"RefreshMultiplayerMap failed: {e.GetType().Name}: {e.Message}");
            }
        }
        else
        {
            VersusPlugin.Log.LogWarning("RefreshMultiplayerMap not found");
        }

        // Gauntlet preparation requires a validated local session; never run it from this probe.
    }

    public static bool RunCommand(string input)
    {
        try
        {
            var console = UnityEngine.Object.FindObjectOfType<Console.GameConsole>();
            var commands = Console.GameConsoleCommandRegistry.CreateCommands()?.Commands;
            if (commands == null || input == null)
            {
                VersusPlugin.Log.LogWarning($"command '{input}': no command registry");
                return false;
            }

            var token = input.Split(' ')[0];
            Console.Command match = null;
            for (var i = 0; i < commands.Count; i++)
            {
                var name = commands[i]?.GetName();
                if (name == token || (name != null && name.StartsWith(token + " ")))
                {
                    match = commands[i];
                    break;
                }
            }

            if (match == null)
            {
                VersusPlugin.Log.LogWarning($"command '{input}': not found");
                return false;
            }

            var executed = match.Execute(input, console);
            VersusPlugin.Log.LogInfo($"command '{input}' executed={executed}");
            return executed;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"command '{input}' failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Probe-only helper for the legacy start-battle flag: press the game's own
    /// Start Battle button without touching the hand or forcing combat. The
    /// native StartCombat barrier still applies.
    /// </summary>
    private static void BeginVersusBattle(string reason)
    {
        if (_resultHandled)
        {
            return;
        }

        InvokeNativeStartBattle(reason);
        ScheduleVerification(10);
    }

    /// <summary>
    /// True while a versus match uses the native battle-start barrier.
    /// </summary>
    private static bool BarrierActive =>
        VersusConfig.Current?.HasFlag("auto-versus") == true &&
        VersusNet.Connected && !_resultHandled && Core.Wave.Data != null &&
        (_lastScene == "Gameplay" || _lastScene == "YearShowcase");

    /// <summary>
    /// Harmony prefix on Core.Wave.StartCombat. The player presses the native
    /// Start Battle button as usual; its UI/state work is done, but combat does
    /// not begin until every alive player is ready and the host sent go.
    /// </summary>
    public static bool MayStartCombat()
    {
        if (!BarrierActive)
        {
            return true;
        }

        var wave = Core.Wave.Data?.Wave ?? -1;
        if (wave < 0 || _battleReleasedWave == wave)
        {
            return true;
        }

        if (VersusNet.GoWave >= wave)
        {
            _battleReleasedWave = wave;
            VersusPlugin.Log.LogInfo($"barrier: go for wave {wave}; releasing native StartCombat");
            return true;
        }

        return false;
    }

    public static void OnCombatHeld()
    {
        if (_startBattleHeld)
        {
            return;
        }

        _startBattleHeld = true;
        var wave = Core.Wave.Data?.Wave ?? -1;
        VersusPlugin.Log.LogInfo($"barrier: Start Battle held at wave {wave}; waiting for go");
    }

    /// <summary>
    /// Called by the StartWave postfix: one barrier per wave, announced to the
    /// other players. The game keeps running its own rewards and shop.
    /// </summary>
    public static void OnWaveStarted(int wave)
    {
        if (!BarrierActive || wave < 0 || _barrierStartedWave == wave)
        {
            return;
        }

        _barrierStartedWave = wave;
        _startBattleHeld = false;
        _battleReleasedWave = -1;
        VersusNet.BeginWave(wave);
        Match.BeginRound(wave);
        VersusNet.SendReady(wave);
        VersusPlugin.Log.LogInfo($"barrier: wave {wave} started, ready sent " +
            $"(mode={VersusNet.Mode}, go={VersusNet.GoWave})");
    }

    /// <summary>
    /// Native life-loss observation. We never call SetLife, but we record that
    /// the game itself reported a breach so the round result is traceable.
    /// </summary>
    public static void OnNativeLifeLoss(string reason)
    {
        if (!BarrierActive)
        {
            return;
        }

        var wave = Core.Wave.Data?.Wave ?? -1;
        var lives = Core.PlayerLife.Instance?.HealthPoints ?? -1;
        _lastNativeLifeWave = wave;
        _lastNativeLifeReason = reason;
        VersusPlugin.Log.LogInfo($"native-life: {reason} at wave {wave}, lives={lives}");
    }

    public static void OnNativeEnemyBaseDamage(int healthPoints, bool lostLife)
    {
        if (!BarrierActive)
        {
            return;
        }

        _lastEnemyBaseHp = healthPoints;
        VersusPlugin.Log.LogInfo($"native-life: enemy base damage hp={healthPoints} lostLife={lostLife}");
    }

    /// <summary>
    /// Wave end is the game's own result event: by now it has applied any life
    /// loss. Each client reports the live life count; the host collects all
    /// reports before resolving the round. No timeout and no wave parity.
    /// </summary>
    public static void OnWaveEnded(int wave)
    {
        if (!BarrierActive || wave < 0 || _waveEndReportedWave == wave)
        {
            return;
        }

        _waveEndReportedWave = wave;
        var lives = Core.PlayerLife.Instance?.HealthPoints ?? -1;
        if (lives < 0)
        {
            lives = VersusNet.LocalLives;
        }

        VersusNet.SetLocalLives(lives);
        var enemyHp = ReadEnemyBaseHp();
        VersusNet.SendWaveEnd(wave, lives, enemyHp);
        var reason = _lastNativeLifeWave == wave ? _lastNativeLifeReason : "none";
        VersusPlugin.Log.LogInfo($"native-result: wave {wave} ended, lives={lives}, enemyHp={enemyHp}, lifeLoss={reason}");
    }

    /// <summary>
    /// In a multiplayer run the game writes base damage to the opponent's
    /// MultiplayerRunData.currentHP via ReceivedMultiplayerEnemyBaseDamage.
    /// Read that native value instead of inferring a result from timers or kills.
    /// </summary>
    private static int ReadEnemyBaseHp()
    {
        try
        {
            if (_lastEnemyBaseHp >= 0)
            {
                return _lastEnemyBaseHp;
            }

            var session = MultiplayerSystems.MultiplayerManager.Session;
            var runs = session?.GauntletEnemies;
            if (runs == null)
            {
                return -1;
            }

            var lowest = int.MaxValue;
            foreach (var run in runs)
            {
                if (run == null)
                {
                    continue;
                }

                var hp = run.currentHP;
                if (hp >= 0 && hp < lowest)
                {
                    lowest = hp;
                }
            }

            return lowest == int.MaxValue ? -1 : lowest;
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"native-result: enemy HP read failed: {e.Message}");
            return -1;
        }
    }

    /// <summary>
    /// QA-only bot: places the starting hand through the game's own card and
    /// area callbacks so a headless test run has troops to fight with. Off in
    /// every production configuration; never clears the hand or forces combat.
    /// </summary>
    private void QaBotArmy()
    {
        if (VersusConfig.Current?.HasFlag("bot-army") != true ||
            _lastScene != "Gameplay" || Core.Wave.Data == null)
        {
            return;
        }

        if (_botArmyAt < 0f)
        {
            _botArmyAt = Time.time + 1f;
        }

        if (Time.time < _botArmyAt || Core.Wave.Data.Running || Core.Wave.Data.CombatOn)
        {
            return;
        }

        _botArmyAt = Time.time + 2f;
        try
        {
            var hand = GameplayInterface.GameplayUI.HandCards;
            var cards = hand?.Cards;
            var areas = UnityEngine.Object.FindObjectsOfType<Core.Kingdoms.KingdomArea>();
            var placed = 0;
            if (cards != null)
            {
              foreach (var card in cards)
              {
                if (placed >= 3 || card?.Card?.IsTroop != true)
                {
                    continue;
                }

                Core.Kingdoms.KingdomArea target = null;
                foreach (var area in areas)
                {
                    if (area != null && !area.MultiplayerEnemyArea && area.Unlocked &&
                        !area.HasPlaced)
                    {
                        target = area;
                        break;
                    }
                }

                if (target == null)
                {
                    break;
                }

                // The hand card's private select callback is what a real click
                // fires; ControllerInteract alone does not mark it selected.
                try
                {
                    var select = typeof(Cards.HandCard).GetField("m_OnSelect",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var callback = select?.GetValue(card);
                    callback?.GetType().GetMethod("Invoke")?.Invoke(callback, new object[] { card });
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"qa-bot select failed: {e.Message}");
                }

                target.OnMouseDown?.Invoke();
                placed++;
              }
            }

            if (placed > 0)
            {
                VersusPlugin.Log.LogInfo($"qa-bot: placed {placed} cards; remaining={hand.CardsCount}");
            }

            var wave = Core.Wave.Data.Wave;
            if (wave >= 0 && _qaBattleWave != wave && VersusNet.GoWave >= wave &&
                !Core.Wave.Data.Running && !Core.Wave.Data.CombatOn)
            {
                _qaBattleWave = wave;
                // Do NOT call SpawnWave here: the game already spawns its own
                // wave army at StartWave. Doubling it made the opponent army
                // overwhelming and left hundreds of enemies alive after the
                // local castle fell, which stalled wave completion.

                // Keep a local army alive: once the castle is gone the
                // opponent troops have no target and combat never ends. QA only.
                try
                {
                    var reinforced = 0;
                    foreach (var area in UnityEngine.Object.FindObjectsOfType<Core.Kingdoms.KingdomArea>())
                    {
                        if (area == null || area.MultiplayerEnemyArea || !area.Unlocked)
                        {
                            continue;
                        }

                        area.AddTroops(4);
                        reinforced++;
                        if (reinforced >= 3)
                        {
                            break;
                        }
                    }

                    VersusPlugin.Log.LogInfo($"qa-bot: reinforced {reinforced} local areas for wave {wave}");
                }
                catch (Exception e)
                {
                    VersusPlugin.Log.LogWarning($"qa-bot reinforce failed: {e.Message}");
                }

                InvokeNativeStartBattle("qa-bot");
                // The attached run needs the game's documented second press to
                // clear the spawn wait. QA-only: never used outside bot-army.
                _secondBattleAt = Time.time + 0.6f;
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"qa-bot failed: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>Releases a held native Start Battle call once go is available.</summary>
    private static void Stage2Pump()
    {
        if (!BarrierActive)
        {
            return;
        }

        var wave = Core.Wave.Data?.Wave ?? -1;
        // The game sometimes advances the wave counter without calling EndWave
        // in an attached offline run. Read the native result at the wave
        // boundary from the game's own life counter instead: no timer, no parity,
        // just the value the game itself arrived at.
        if (wave >= 0 && wave != _lastObservedWave)
        {
            if (_lastObservedWave >= 0)
            {
                OnWaveEnded(_lastObservedWave);
            }

            _lastObservedWave = wave;
            OnWaveStarted(wave);
        }

        // Combat ending is the native result boundary. Attached runs sometimes
        // skip EndWave, so report the game's own life values here as well.
        var combatOn = Core.Wave.Data?.CombatOn ?? false;
        if (combatOn)
        {
            _combatActiveWave = wave;
        }
        else if (_combatActiveWave == wave && wave >= 0)
        {
            OnWaveEnded(wave);
            _combatActiveWave = -1;
        }

        if (wave >= 0 && _startBattleHeld && _battleReleasedWave != wave &&
            VersusNet.GoWave >= wave)
        {
            _battleReleasedWave = wave;
            _startBattleHeld = false;
            try
            {
                Core.Wave.StartCombat(false);
                VersusPlugin.Log.LogInfo($"go-release: resumed native StartCombat for wave {wave}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"go-release failed: {(e.InnerException ?? e).Message}");
            }
        }

        if (VersusNet.RoundWave > _roundAppliedWave && VersusNet.RoundWave >= 0)
        {
            ApplyRound(VersusNet.RoundWave, VersusNet.RoundLives);
        }

        // Release a StartWave call held by the lockstep barrier once the
        // previous round has been shared by every peer.
        if (_startWaveHeldWave > 0 && _roundAppliedWave >= _startWaveHeldWave - 1)
        {
            var held = _startWaveHeldWave;
            _startWaveHeldWave = -1;
            try
            {
                var method = typeof(Core.Wave).GetMethod("StartWave",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                method?.Invoke(null, null);
                VersusPlugin.Log.LogInfo($"wave-lock: released StartWave for wave {held}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"wave-lock release failed: {(e.InnerException ?? e).Message}");
            }
        }
    }

    /// <summary>
    /// Hold the native StartWave until the previous round's life table has been
    /// received from the host. This keeps peers in lockstep without a timer.
    /// </summary>
    public static bool MayStartWave(int wave)
    {
        if (!BarrierActive || wave <= 0 || _roundAppliedWave >= wave - 1)
        {
            return true;
        }

        if (_startWaveHeldWave != wave)
        {
            _startWaveHeldWave = wave;
            VersusPlugin.Log.LogInfo($"wave-lock: holding StartWave for wave {wave} " +
                $"(waiting for round {wave - 1}, applied={_roundAppliedWave})");
        }

        return false;
    }

    private static void InvokeNativeStartBattle(string reason)
    {
        try
        {
            var view = UnityEngine.Object.FindObjectOfType<Core.GameplayUI_PlacingView>();
            var method = typeof(Core.GameplayUI_PlacingView).GetMethod("StartBattle",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (view == null || method == null)
            {
                VersusPlugin.Log.LogWarning($"{reason}: native StartBattle not available");
                return;
            }

            method.Invoke(view, null);
            VersusPlugin.Log.LogInfo($"{reason}: native StartBattle invoked for wave {Core.Wave.Data?.Wave}");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"{reason} failed: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// Applies the host's life table for one round. Eliminations are decided by
    /// the game's own life counts; placement follows base-fall time (last to fall
    /// ranks higher), which for a simultaneous round means the order is stable.
    /// </summary>
    private static void ApplyRound(int wave, IReadOnlyDictionary<int, int> lives)
    {
        _roundAppliedWave = wave;
        if (lives == null || lives.Count == 0)
        {
            return;
        }

        var eliminated = new System.Collections.Generic.List<int>();
        foreach (var pair in lives)
        {
            if (pair.Value <= 0)
            {
                eliminated.Add(pair.Key);
            }
        }

        var lowestId = -1;
        var lowestLives = int.MaxValue;
        foreach (var pair in lives)
        {
            if (pair.Value < lowestLives)
            {
                lowestLives = pair.Value;
                lowestId = pair.Key;
            }
        }

        Match.ResolveRound(lowestId, false);
        foreach (var id in eliminated)
        {
            Match.RemovePlayer(id);
        }

        VersusPlugin.Log.LogInfo($"native-result: round {wave} applied, lives={FormatLives(lives)}, " +
            $"eliminated=[{string.Join(",", eliminated)}], alive={Match.AlivePlayers}, phase={Match.Phase}");

        var localId = VersusNet.LocalPlayerId;
        if (!lives.TryGetValue(localId, out var myLives))
        {
            return;
        }

        VersusNet.SetLocalLives(myLives);
        if (myLives <= 0)
        {
            ApplyMatchResult(false);
            return;
        }

        if (Match.AlivePlayers == 1)
        {
            ApplyMatchResult(true);
        }
    }

    private static string FormatLives(IReadOnlyDictionary<int, int> lives)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var pair in lives)
        {
            parts.Add($"p{pair.Key}:{pair.Value}");
        }
        return string.Join(",", parts);
    }

    /// <summary>A peer dropped: remove its army from later rounds on every client.</summary>
    public static void OnPeerLost(int playerId)
    {
        if (Match.Phase == MatchPhase.Idle || Match.Phase == MatchPhase.Finished)
        {
            return;
        }

        Match.RemovePlayer(playerId);
        VersusPlugin.Log.LogInfo($"match-state: peer {playerId} removed, alive={Match.AlivePlayers}");
        if (Match.AlivePlayers == 1 && !_resultHandled)
        {
            ApplyMatchResult(true);
        }
    }

    private static string LatestLocalSnapshot()
    {
        try
        {
            var files = Directory.GetFiles(VersusPlugin.DataDir, "*-wave-start-*.json");
            if (files.Length == 0)
            {
                files = Directory.GetFiles(VersusPlugin.DataDir, "*-capture-*.json");
            }

            Array.Sort(files, StringComparer.Ordinal);
            return files.Length > 0 ? File.ReadAllText(files[^1]) : null;
        }
        catch
        {
            return null;
        }
    }

    private static float SnapshotStrength(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("Areas", out var areas) || areas.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return 0f;
            }

            float total = 0f;
            foreach (var area in areas.EnumerateArray())
            {
                if (!area.TryGetProperty("PlacedEntityType", out var kind) || kind.GetString() != "Troop")
                {
                    continue;
                }

                var quantity = area.TryGetProperty("TroopQuantity", out var q) ? q.GetInt32() : 0;
                var strength = 0f;
                if (area.TryGetProperty("Stats", out var stats) &&
                    stats.TryGetProperty("Strength", out var s))
                {
                    strength = (float)s.GetDouble();
                }

                total += strength * quantity;
            }

            return total;
        }
        catch
        {
            return 0f;
        }
    }

    private static bool _endShown;

    /// <summary>
    /// Official end screen only. The game's own life loss already decided the
    /// result; this never calls SetLife, SetRunning or console commands.
    /// </summary>
    private static void ApplyMatchResult(bool victory)
    {
        if (_endShown)
        {
            return;
        }

        _endShown = true;
        _resultHandled = true;
        Match.Finish();
        VersusPlugin.Log.LogInfo($"match-result: applying {(victory ? "victory" : "defeat")} on {VersusNet.Mode}");
        ShowOfficialEnd(victory
            ? GameplayInterface.GameplayUI_EndUI.EndUIType.Victory
            : GameplayInterface.GameplayUI_EndUI.EndUIType.Defeat);
    }

    private static float _endRetryAt = -1f;
    private static int _endRetryCount;
    private static GameplayInterface.GameplayUI_EndUI.EndUIType? _pendingEndType;

    private static void ShowOfficialEnd(GameplayInterface.GameplayUI_EndUI.EndUIType type)
    {
        try
        {
            var method = typeof(Core.Wave).GetMethod("OnCompleteGame",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                VersusPlugin.Log.LogWarning("match-result: OnCompleteGame not found");
                return;
            }

            method.Invoke(null, new object[] { type });
            VersusPlugin.Log.LogInfo($"match-result: OnCompleteGame({type})");

            _pendingEndType = null;
        }
        catch (Exception e)
        {
            var message = (e.InnerException ?? e).Message;
            if (_endRetryCount < 3)
            {
                _pendingEndType = type;
                _endRetryAt = Time.time + 3f;
                _endRetryCount++;
                VersusPlugin.Log.LogWarning($"match-result: end screen delayed (attempt {_endRetryCount}): {message}");
            }
            else
            {
                VersusPlugin.Log.LogWarning($"match-result: OnCompleteGame failed: {message}");
            }
        }
    }


    /// <summary>
    /// Called by the StartWave prefix. In an auto-versus match only the first
    /// StartWave for a wave is allowed (the game otherwise re-issues rewards and
    /// floods the hand); after a match result no further waves may start.
    /// </summary>
    public static void StartVersusMatch(string mode)
    {
        try
        {
            var path = Path.Combine(VersusPlugin.DataDir, "net.json");
            var peer = "127.0.0.1";
            var port = 28764;
            if (File.Exists(path))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("peer", out var p))
                    {
                        peer = p.GetString() ?? peer;
                    }
                }
                catch
                {
                }
            }

            File.WriteAllText(path, $"{{\"mode\":\"{mode}\",\"peer\":\"{peer}\",\"port\":{port}}}");

            // Persist the match switches in config.json. The legacy flag files are
            // no longer required for a match to start.
            var config = VersusConfig.Current;
            if (config != null)
            {
                config.mode = mode;
                config.autoVersus = true;
                config.multiBot = true;
                config.attachFlow = true;
                config.Save();
            }
            ResetMatchState();
            Match.BeginMatch(2, mode == "host" ? 0 : 1);
            Match.EnterLobby();
            VersusPlugin.Log.LogInfo($"match-state: phase={Match.Phase} players={Match.PlayerCount}");

            _attachFlowPending = true;
            VersusPlugin.Log.LogInfo($"menu: starting versus match as {mode}");
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainMenu")
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(9);
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"menu start failed: {e.Message}");
        }
    }

    private static void ApplyMatchFlag(string name)
    {
        try
        {
            var path = Path.Combine(VersusPlugin.DataDir, name);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "1");
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"match flag {name} failed: {e.Message}");
        }
    }

    /// <summary>Clears per-match state so a rematch cannot inherit a decided result.</summary>
    public static void ResetMatchState()
    {
        _resultHandled = false;
        _lastSyncedTo = -1;
        _arenaLocalWave = -1;
        _pendingEndType = null;
        _endRetryAt = -1f;
        _endRetryCount = 0;
        _endShown = false;
        _startBattleHeld = false;
        _barrierStartedWave = -1;
        _battleReleasedWave = -1;
        _roundAppliedWave = -1;
        _lastObservedWave = -1;
        _combatActiveWave = -1;
        _waveEndReportedWave = -1;
        _startWaveHeldWave = -1;
        _lastNativeLifeWave = -1;
        _lastNativeLifeReason = null;
        _lastEnemyBaseHp = -1;

        VersusNet.ResetMatchState();
    }

    public static bool ResultPending()
    {
        // Only a decided match freezes the wave flow. Individual rounds are
        // resolved by the game's own CompleteWave and life loss.
        return _resultHandled;
    }

    public static bool ClaimStartWave()
    {
        var active = VersusConfig.Current?.HasFlag("auto-versus") == true ||
                     VersusConfig.Current?.HasFlag("arena") == true;
        if (!active)
        {
            return true;
        }

        if (_resultHandled)
        {
            VersusPlugin.Log.LogWarning("StartWave blocked: match result already decided");
            return false;
        }

        return true;
    }

    public static string MenuStartBattle()
    {
        try
        {
            BeginVersusBattle("menu");
            return "Start Battle pressed";
        }
        catch (Exception e)
        {
            return $"Failed: {e.Message}";
        }
    }

    public static string MenuBuildBoard()
    {
        try
        {
            LocalSessionProbe.BuildEnemyBoard();
            return "Opponent board rebuild attempted";
        }
        catch (Exception e)
        {
            return $"Failed: {e.Message}";
        }
    }

    private static string PathOf(Transform transform)
    {
        var parts = new System.Collections.Generic.List<string>();
        var current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    public static void ScheduleVerification(int samples = 6)
    {
        _verifyAt = Time.time + 8f;
        _verifySamples = samples;
    }

    private static void AdvanceWave()
    {
        try
        {
            var target = (Core.Wave.Data?.Wave ?? 15) + 1;
            VersusPlugin.Log.LogInfo($"advance: SetWave({target}, skipShop=true, showUIs=false)");
            Core.Wave.SetWave(target, true, false, false);
            VersusPlugin.Log.LogInfo("advance: SetWave invoked (awaitable started)");
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"advance failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static int SafeWave(Gameplay.GameSnapshot snapshot)
    {
        try
        {
            return snapshot.WaveData?.WaveNumber ?? -1;
        }
        catch
        {
            return -1;
        }
    }
}
