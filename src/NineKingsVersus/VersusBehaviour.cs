using System;
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
    private static Gameplay.GameSnapshot _cached;
    private static string _lastScene;
    private static bool _autoProbe;
    private static bool _hubProbePending;
    private static bool _attachFlowPending;
    private static bool _backupLoadPending;
    private static bool _attachDone;
    private static float _verifyAt = -1f;
    private static float _secondBattleAt = -1f;
    private static float _handTrimAt = -1f;
    private static float _diagAt = -1f;
    private static float _syncAt = -1f;
    private static int _lastSyncedTo = -1;
    private static int _arenaLocalWave = -1;
    private static int _lastNetStateVersion;
    private static int _handshakeWave = -1;
    private static bool _readySent;
    private static bool _goSent;
    private static bool _autoStarted;
    private static bool _resultHandled;
    private static int _localEliminatedWave = -1;
    private static float _combatSince = -1f;
    private static int _combatSinceWave = -1;
    private static int _waveResolvedWave = -1;
    private static float _nextWaveAt = -1f;
    private static float _mpScreenAt = -1f;
    private static bool _mpScreenShown;
    private static int _verifySamples;
    private float _autoAt = -1f;
    private static float _attachRetryAt = -1f;
    private float _heartbeatAt = -1f;

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
        LocalSessionProbe.PumpEnemyShowcase();
        LocalSessionProbe.PumpDiplomatView();
        LocalSessionProbe.PumpEscape();

        var probeUi = System.IO.Path.Combine(VersusPlugin.DataDir, "probe-ui-getters");
        if (System.IO.File.Exists(probeUi))
        {
            System.IO.File.Delete(probeUi);
            LocalSessionProbe.ProbeUiGetters();
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

        // Activate the native enemy-face bar once the Gameplay scene has settled.
        // GameplayMultiplayerManager.Start leaves it inactive in our loaded run, so
        // the enemy reveal never appears unless we enable and populate it ourselves.
        if (_lastScene == "Gameplay" && VersusNet.Connected && !_mpScreenShown && _mpScreenAt < 0f)
        {
            _mpScreenAt = Time.time + 2f;
        }

        if (_mpScreenAt > 0f && Time.time >= _mpScreenAt)
        {
            _mpScreenAt = -1f;
            _mpScreenShown = true;
            LocalSessionProbe.ShowMpScreen();
            // Show only the enemy-card reveal (EnemyShowcase), not the year-cutscene
            // that used to black-screen the start. PumpEnemyShowcase presses its
            // Accept button after 3s so the reveal cannot softlock the run.
            LocalSessionProbe.ShowEnemyReveal();
        }

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

        var forceShowcase = System.IO.Path.Combine(VersusPlugin.DataDir, "showcase");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(forceShowcase))
        {
            System.IO.File.Delete(forceShowcase);
            LocalSessionProbe.PlayYearShowcase();
        }

        var showMpScreen = System.IO.Path.Combine(VersusPlugin.DataDir, "show-mp-screen");
        if (_lastScene == "Gameplay" && System.IO.File.Exists(showMpScreen))
        {
            System.IO.File.Delete(showMpScreen);
            LocalSessionProbe.ShowMpScreen();
        }

        var skipIntro = System.IO.Path.Combine(VersusPlugin.DataDir, "skip-intro");
        if (System.IO.File.Exists(skipIntro))
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
                !System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "arena")))
            {
                VersusPlugin.Log.LogInfo("net: rebuilding opponent board from peer state");
                LocalSessionProbe.BuildEnemyBoard();
            }
        }

        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave > 0 &&
            System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "arena")) &&
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
            System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "arena")))
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
                    $"resultHandled={_resultHandled} eliminatedWave={_localEliminatedWave} " +
                    $"waveEnemies={Core.Wave.Enemies?.Count.ToString() ?? "null"} validTroops={Core.Wave.ValidTroops?.Count ?? -1}");
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"diag failed: {e.Message}");
            }
        }

        // The game re-issues rewards when StartWave is repeated; keep the hand from
        // flooding so combat remains playable in arena mode.
        if (_lastScene == "Gameplay" && Time.time >= _handTrimAt &&
            (System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "arena")) ||
             System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "custom-deck"))))
        {
            _handTrimAt = Time.time + 4f;
            try
            {
                var hand = GameplayInterface.GameplayUI.HandCards;
                if (hand != null && hand.CardsCount > 2)
                {
                    VersusPlugin.Log.LogInfo($"arena: trimming flooded hand from {hand.CardsCount} to 0");
                    hand.Clear();
                }
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"arena hand trim failed: {e.Message}");
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
            if (System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "auto-start")))
            {
                TryStartRun();
            }
        }

        // attach-flow: load the hub scene so MultiplayerManager.Instance exists, attach
        // the probe session there, then start a run through Core.Wave.StartNewGame with
        // its own isMultiplayer=true parameter (avoids flipping IsMultiplayer mid-run).
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
                var shortKing = (officialKing ?? "KingOfNothing").Replace("KingOf", string.Empty);
                LocalSessionProbe.StartMultiplayerRun(shortKing);
                LocalSessionProbe.ApplyChosenPerks();
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

        // Auto-starting the battle used to clear the hand the instant a wave began,
        // so the player could never place a card. Combat is now started by the game's
        // own Start Battle button; the ready/go state is still exchanged for sync.
        var autoVersus = System.IO.Path.Combine(VersusPlugin.DataDir, "auto-versus");
        if (_lastScene == "Gameplay" && Core.Wave.Data != null && Core.Wave.Data.Wave >= 0
            && System.IO.File.Exists(autoVersus) && VersusNet.Connected)
        {
            AutoVersusStep(Core.Wave.Data.Wave);
        }

        if (VersusNet.Connected && _lastScene == "Gameplay" && Core.Wave.Data != null
            && (System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "auto-versus")) ||
                System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "arena"))))
        {
            MatchResultStep();
        }

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
                VersusPlugin.Log.LogInfo($"scene changed: {scene} multiplayerManager={(hubManager == null ? "absent" : "present")}");
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
    /// Presses the game's own Start Battle button (clearing the hand first so the
    /// <=2-card gate is satisfied) and schedules the second press that actually
    /// begins combat.
    /// </summary>
    private static void BeginVersusBattle(string reason)
    {
        if (_resultHandled)
        {
            return;
        }

        try
        {
            var data = Core.Wave.Data;
            var hand = GameplayInterface.GameplayUI.HandCards;
            var view = UnityEngine.Object.FindObjectOfType<Core.GameplayUI_PlacingView>();
            VersusPlugin.Log.LogInfo($"{reason}: view={(view == null ? "null" : "found")} " +
                $"startState={view?.GetStartState().ToString() ?? "n/a"} wave={data?.Wave} cards={hand?.CardsCount.ToString() ?? "n/a"} " +
                $"running={data?.Running} combatOn={data?.CombatOn} waitingToBegin={data?.WaitingToBegin}");

            if (hand != null && hand.CardsCount > 0)
            {
                hand.Clear();
            }

            if (view == null)
            {
                VersusPlugin.Log.LogWarning($"{reason}: no placing view found");
                return;
            }

            var method = typeof(Core.GameplayUI_PlacingView).GetMethod("StartBattle",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            method?.Invoke(view, null);
            VersusPlugin.Log.LogInfo($"{reason}: StartBattle invoked (method={method != null}); " +
                $"running={data?.Running} combatOn={data?.CombatOn} waitingToBegin={data?.WaitingToBegin} cards={hand?.CardsCount.ToString() ?? "n/a"}");
            // The game's own notes show StartBattle must be pressed twice: the
            // first stages/spawns, the second actually engages combat. Keep the
            // gap short so the game cannot auto-complete the wave in between.
            _secondBattleAt = Time.time + 0.6f;
            ScheduleVerification(10);
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"{reason} failed: {e.GetType().Name}: {(e.InnerException ?? e).Message}");
        }
    }

    /// <summary>
    /// Host is the start authority: both peers announce readiness for the current
    /// wave, then the host sends go and both press Start Battle.
    /// </summary>
    private static void AutoVersusStep(int wave)
    {
        if (_resultHandled)
        {
            return;
        }

        var data = Core.Wave.Data;
        if (data == null || data.Running || data.CombatOn)
        {
            return;
        }

        if (_handshakeWave != wave)
        {
            _handshakeWave = wave;
            _readySent = false;
            _goSent = false;
            _autoStarted = false;
            VersusPlugin.Log.LogInfo($"auto-versus: new wave {wave}, mode={VersusNet.Mode}, peerReady={VersusNet.PeerReadyWave}");
        }

        if (!_readySent)
        {
            _readySent = true;
            VersusNet.SendReady(wave);
        }

        // The host gates on the peer's ready, then sends `go`. The join treats
        // `go` as authoritative and does not wait for a separate ready echo:
        // requiring it made the join miss the host's wave and fight one behind,
        // so the life comparison rewarded the idle side.
        bool shouldStart;
        if (VersusNet.Mode == "host")
        {
            if (VersusNet.PeerReadyWave != wave || VersusNet.StateVersion < 1)
            {
                return;
            }

            if (!_goSent)
            {
                _goSent = true;
                VersusNet.SendGo(wave);
            }

            shouldStart = _goSent;
        }
        else
        {
            // The join can load one wave ahead of the host (the attached run's
            // snapshot series advances on attach). Start on any go rather than
            // requiring an exact wave match; ResultPending freezes the wave once
            // combat engages, so both peers then settle on their current wave.
            shouldStart = VersusNet.GoWave > 0 && VersusNet.StateVersion > 0;
        }

        if (shouldStart && !_autoStarted)
        {
            _autoStarted = true;
            // Do not invoke StartBattle: the game's own button must stay usable so
            // the player can place cards before combat. This only marks readiness.
            VersusPlugin.Log.LogInfo($"auto-versus: both ready for wave {wave}; awaiting player Start Battle");
        }
    }

    /// <summary>
    /// Host-authoritative match result: the first life-count difference decides
    /// the match (the peer with fewer lives loses). The result is sent to both
    /// peers so they cannot declare separate victories.
    /// </summary>
    private static void MatchResultStep()
    {
        try
        {
            var myLives = VersusNet.LocalLives;
            var peerLives = VersusNet.PeerLives;
            var wave = Core.Wave.Data?.Wave ?? -1;

            // Arena result: the side whose opponent army was wiped out loses.
            var liveEnemies = 0;
            var entities = UnityEngine.Object.FindObjectsOfType<Core.Entities.Entity>();
            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    try
                    {
                        if (entity != null && entity.IsEnemy)
                        {
                            liveEnemies++;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (!_resultHandled && wave > 0 && OpponentDeck.LastWave == wave &&
                OpponentDeck.LastBattlers > 0 && liveEnemies <= 3 &&
                (Core.Wave.Data?.CombatOn ?? false) &&
                _localEliminatedWave != wave)
            {
                _localEliminatedWave = wave;
                VersusPlugin.Log.LogInfo($"arena: opponent army eliminated locally at wave {wave}");

                if (VersusNet.Mode == "host")
                {
                    _resultHandled = true;
                    VersusNet.SendResult(1);
                    ApplyMatchResult(1);
                    return;
                }

                VersusNet.SendEliminated(wave);
            }

            if (false && VersusNet.Mode == "host" && !_resultHandled && wave > 0 &&
                VersusNet.PeerEliminatedWave == wave)
            {
                _resultHandled = true;
                VersusPlugin.Log.LogInfo($"arena: peer eliminated at wave {wave}, host loses");
                VersusNet.SendResult(0);
                ApplyMatchResult(0);
                return;
            }

            var combatOn = Core.Wave.Data?.CombatOn ?? false;
            if (combatOn && _combatSinceWave != wave)
            {
                _combatSinceWave = wave;
                _combatSince = Time.time;
                VersusPlugin.Log.LogInfo($"wave-loop: combat engaged at wave {wave}");
            }

            // Resolve a wave after a grace period. The attached session's combat can
            // stall with no life change, so the host decides from the life counters
            // (falling back to snapshot kills, then a deterministic alternation) and
            // broadcasts the wave loser to both peers.
            if (VersusNet.Mode == "host" && !_resultHandled && wave > 0 &&
                _combatSince > 0f && Time.time - _combatSince >= 12f &&
                _waveResolvedWave != wave && VersusNet.StateVersion > 0)
            {
                var loser = DecideWaveLoser(myLives, peerLives, wave);
                _waveResolvedWave = wave;
                VersusNet.SendWaveWin(wave, loser);
                ApplyWaveWin(wave, loser);
                return;
            }

            // The join applies the host's wave decision for this wave.
            if (VersusNet.Mode == "join" && !_resultHandled && wave > 0 &&
                VersusNet.WaveWinWave == wave && _waveResolvedWave != wave)
            {
                _waveResolvedWave = wave;
                ApplyWaveWin(wave, VersusNet.WaveWinLoser);
                return;
            }

            // Do NOT force the wave forward: the game's own wave completion runs
            // the shop/rewards and rebuilds the placing view. Forcing SetWave here
            // skipped the shop and left no placing view (softlock). Waves advance
            // naturally; peers are kept together by the snapshot/board exchange.

            if (VersusNet.Mode == "join" && VersusNet.ResultLoser >= 0 && !_resultHandled)
            {
                ApplyMatchResult(VersusNet.ResultLoser);
            }
        }
        catch (Exception e)
        {
            VersusPlugin.Log.LogWarning($"match-result failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Picks the loser of one wave. Real life loss wins; otherwise compare the
    /// units each side killed in their snapshots, then fall back to a stable
    /// wave-based alternation. The host computes it and both peers apply it.
    /// </summary>
    private static int DecideWaveLoser(int myLives, int peerLives, int wave)
    {
        if (myLives >= 0 && peerLives >= 0 && myLives != peerLives)
        {
            return myLives < peerLives ? 0 : 1;
        }

        var localJson = LatestLocalSnapshot();
        if (!string.IsNullOrEmpty(localJson) && !string.IsNullOrEmpty(VersusNet.OpponentSnapshotJson))
        {
            var mine = SnapshotKills(localJson);
            var theirs = SnapshotKills(VersusNet.OpponentSnapshotJson);
            if (mine >= 0 && theirs >= 0 && mine != theirs)
            {
                return mine < theirs ? 0 : 1;
            }
        }

        // Stable tiebreak: host loses even waves, join loses odd waves.
        return wave % 2 == 0 ? 0 : 1;
    }

    private static int SnapshotKills(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("WaveData", out var waveData) &&
                waveData.TryGetProperty("UnitsKilled", out var killed))
            {
                return killed.GetInt32();
            }
        }
        catch
        {
        }

        return -1;
    }

    /// <summary>
    /// Applies a resolved wave on this peer: decrement the loser's life counter
    /// and either end the match or schedule the next wave.
    /// </summary>
    private static void ApplyWaveWin(int wave, int loser)
    {
        var host = VersusNet.Mode == "host";
        var myLives = VersusNet.LocalLives;
        var peerLives = VersusNet.PeerLives;

        if (loser == 0)
        {
            if (host) myLives--; else peerLives--;
        }
        else
        {
            if (host) peerLives--; else myLives--;
        }

        VersusNet.SetLocalLives(myLives);
        VersusNet.SetPeerLives(peerLives);
        VersusPlugin.Log.LogInfo(
            $"wave-loop: wave {wave} resolved, loser={(loser == 0 ? "host" : "join")} " +
            $"lives={myLives}/{peerLives} (local/peer)");

        if (myLives <= 0 || peerLives <= 0)
        {
            var matchLoser = myLives <= 0 ? (host ? 0 : 1) : (host ? 1 : 0);
            _resultHandled = true;
            VersusPlugin.Log.LogInfo($"wave-loop: match over, loser={(matchLoser == 0 ? "host" : "join")}");
            if (host)
            {
                VersusNet.SendResult(matchLoser);
            }

            ApplyMatchResult(matchLoser);
            return;
        }

        _nextWaveAt = Time.time + 4f;
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

    private static void ApplyMatchResult(int loser)
    {
        if (_endShown)
        {
            return;
        }

        _endShown = true;
        _resultHandled = true;
        var iAmLoser = (VersusNet.Mode == "host" && loser == 0) || (VersusNet.Mode == "join" && loser == 1);
        VersusPlugin.Log.LogInfo($"match-result: applying {(iAmLoser ? "defeat" : "victory")} on {VersusNet.Mode}");
        if (iAmLoser)
        {
            Core.Wave.Data?.SetRunning(false);
            RunCommand("SetLife 0");
            ShowOfficialEnd(GameplayInterface.GameplayUI_EndUI.EndUIType.Defeat);
        }
        else
        {
            RunCommand("SetLife 1");
            try
            {
                Core.Wave.Data?.SetRunning(false);
                Core.Wave.Instance?.StopSpawningEnemies();
                Core.Wave.Instance?.DespawnWaveEnemies();
            }
            catch (Exception e)
            {
                VersusPlugin.Log.LogWarning($"match-result: stop combat failed: {e.Message}");
            }

            ShowOfficialEnd(GameplayInterface.GameplayUI_EndUI.EndUIType.Victory);
        }
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

            // A match started from the native menu needs the same flags the tested
            // loopback flow used: auto-versus drives the ready/go handshake, the
            // synced Start Battle, and the shared result; multi-bot gives the
            // opponent king a real army/base. Without these the run loads but no
            // battle ever starts and no result is ever computed.
            ApplyMatchFlag("auto-versus");
            ApplyMatchFlag("multi-bot");
            ResetMatchState();

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
        _localEliminatedWave = -1;
        _handshakeWave = -1;
        _readySent = false;
        _goSent = false;
        _autoStarted = false;
        _lastSyncedTo = -1;
        _arenaLocalWave = -1;
        _pendingEndType = null;
        _endRetryAt = -1f;
        _endRetryCount = 0;
        _combatSince = -1f;
        _combatSinceWave = -1;
        _waveResolvedWave = -1;
        _nextWaveAt = -1f;
        _endShown = false;
        _mpScreenAt = -1f;
        _mpScreenShown = false;
        VersusNet.ResetMatchState();
    }

    public static bool ResultPending()
    {
        // Freeze only after the MATCH is decided. Individual waves advance in sync.
        return _resultHandled || VersusNet.ResultLoser >= 0 || VersusNet.PeerEliminatedWave > 0;
    }

    public static bool ClaimStartWave()
    {
        var active = System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "auto-versus")) ||
                     System.IO.File.Exists(System.IO.Path.Combine(VersusPlugin.DataDir, "arena"));
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
            return "Start Battle pressed (hand cleared)";
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
