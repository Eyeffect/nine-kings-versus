# Findings: why the async snapshot path cannot deliver live 1v1

Game version: 9 Kings 1.0.3.1 (BepInEx 6 IL2CPP). All tests used disposable local
copies of the game; no shared install, ranked mode, or real save was modified.

## What the shipped multiplayer actually is

The shipped mode is asynchronous PvP. A run is matched against saved kingdoms of
other players; there is no friend selection and no simultaneous play. The save
format is a plain JSON snapshot (`GameSnapshot`) with a nested
`WaveDataSnapshot` that carries `IsMultiplayer` and a `Waves` history. The game
keeps per-wave snapshots in `MultiplayerGameSnapshots` and exposes
`GetWaveSnapshot(wave, fetchClosestPreviousSnapshot)`.

`MultiplayerSession` holds the run array, the season pools, `Snapshots`,
`GauntletEnemies`, `GauntletLossPlayerIds` and `GauntletActive`.
`Core.Wave` exposes `CurrentMultiplayerEnemySnapshot`, `PrepareMultiplayerGauntlet()`,
`RefreshMultiplayerMap()` and `GetWallsRepresentation(bool multiplayerEnemy)`.

## Session construction (detached probes)

- A two-run `MultiplayerSession` can be constructed and attached to
  `MultiplayerManager.Session` without touching the live session.
- `PopulateGauntletEnemies()` reliably throws NRE offline. The run count, profile
  completeness, comparator and season caches were ruled out. Both
  `GauntletEnemies` and `GauntletActive` are writable, so the gauntlet list can
  be supplied manually.
- `PrepareMultiplayerGauntlet()` does not select an enemy snapshot offline. In
  instrumented runs it called `GetWaveSnapshot` zero times and left
  `CurrentMultiplayerEnemySnapshot` null. Instead it appended to
  `session.Snapshots` as the local run progressed — i.e. it is a recording path,
  not an opponent-selection path.
- `Core.Wave.StartNewGame` (the 11-parameter overload with `isMultiplayer`) NREs
  offline even with `isMultiplayer=false`, a live hub manager and seeded caches.
  Abandoned.
- `Core.Wave.LoadGame(snapshot, instaLoad: true, "Gameplay", registerRunStart: false)`
  works. With `WaveData.IsMultiplayer = true` the run loads and the game switches
  to the multiplayer presentation (camera pulls back); with `false` it behaves
  exactly like single player.

## Wave and combat observations

- The game's "start wave" gate is the hand: the hand must be reduced to two cards
  (zero on the final round). `GameplayUI_HandCards.Clear()` satisfies it and
  `waitingForEnemiesToSpawn` flips false. `GameplayUI_PlacingView.StartBattle()`
  is the button handler; in this setup it must be pressed twice — the first
  press stages/spawns, the second begins combat.
- With `WaveData.IsMultiplayer = false`, the double press produces a complete
  battle: entity counts fall, the wave advances, and rewards are granted.
- With `WaveData.IsMultiplayer = true`, `CurrentMultiplayerEnemySnapshot` can be
  injected at the wave entry points and is consumed (the prefix sees it set),
  but no enemy army spawns into the arena. The game stalls with a handful of
  enemies and the run does not progress normally.
- `MultiplayerRemoteMapPreview.Show(king, snapshot, enemyIsDead)` builds only a
  visual fake board for the preview screen; it is not the combat arena.
- `SetMultiplayerCard {X} {Y} {Card Name}` and `SetMultiplayerLife {King} {Life}`
  do create enemy-side entities (`IsMultiplayerEnemy = true`) and set the map
  active, and `enemyTroops`/`enemyAnything` counts increase. But no recognizable
  base is rendered and no opposing army fights from it. Both sides end up
  rushing an empty base.

## LAN prototype behaviour

A host/join transport was built and verified with two real processes:

- Snapshots are exchanged as length-prefixed JSON; `ready`/`go` messages start
  the same wave on both sides; the host sends an authoritative `result` when
  life counts diverge. All three paths were verified.
- Despite in-memory `IsMultiplayerEnemy` entities on both peers, players could
  not see each other's bases. When the run used single-player wave logic, the
  fight ended when local wave enemies reached zero, so both copies reported a
  win for the same fight.
- Repeated `StartWave` calls can re-issue rewards and flood the hand (18 cards
  observed). Suppressing those calls breaks the wave flow, so this is unresolved.

## Arena milestone progress

A custom arena spawner was added (`ArenaSpawner.cs`): opponent troop cards are
resolved with `Core.Game.GetTroop(name)` and spawned through the game's
`Wave.SpawnBattlers` path, then registered in `Wave.Enemies` and
`Wave.EnemyMeleeTroops`. About 25 opponent entities per side spawn from a
snapshot (75 spawn attempts succeed ~1 in 3). Normal wave enemies are despawned
first. `Troop.SpawnInBattle` was also tried but NREs with the available area.

Two-process tests with distinct kingdoms now produce a single shared result:
the host destroys the join army and both peers apply host victory / join defeat.
The life-based result path is disabled in arena mode. Spawn position now uses
the right-side formation transform rather than the base centre, and the hand is
periodically trimmed to prevent reward flooding.

Still unresolved: spawned troops do not reliably target each other; they can
beeline for the base, and the base slowly kills them. Faction masks/AI setup
still need work.

## Final diagnostics (with in-game menu build)

Added a 5-second `diag:` log and native UGUI menu (main-menu VERSUS button, plus Ctrl+Alt+F11).
Observed on two synced processes:

- Waves can be synced (host/join both reached wave 17/18 after adding host-wave `sync` messages), but combat rarely enters `running=true/combatOn=true` for long.
- `arenaSpawned=15 arenaWave=15` stayed stale across later waves: the custom army spawn only happened at the initial wave. Later enemy entities came from SetMultiplayerCard board rebuilds, not the arena spawner.
- Lives stayed 2/2 on both peers, so the life-based result never fired. Army elimination also never reached zero (54 enemy entities persisted), so the arena result never fired either.
- Troops still do not reliably target each other; they accumulate on the opponent side and the local game's own victory/defeat logic remains independent.

Conclusion: the mod can load runs, exchange snapshots, sync waves, spawn some opponent troops, and draw a native menu, but it cannot yet produce a correct, synchronized 1v1 fight with a single winner. The remaining work needs a proper faction/AI setup and a result hook that replaces the game's local victory logic, which requires deeper engine work than the async-gauntlet reuse path allows.

## Handoff state (stopped testing)

Further iterations still showed the same failures:

- Enemy entities accumulate instead of fighting each other (254/239 enemies in the final diag, with `multiplayer=0`).
- Lives converged to 1/1, so the life authority could not decide a winner, and army elimination never reached zero.
- The winner's multiplayer victory UI cannot be entered offline: TryJumpToMultiplayerVictoryWave and the enemy-death screen were invoked but no victory screen appeared. The loser does reach game over via SetLife 0.
- The troop-count bar shows local wave data, not the custom spawned army, so it is misleading.

Decision: stop iterating on this prototype. The LAN transport, native in-game menu, per-wave snapshot sync, arena army spawner, and diagnostics work as isolated pieces, but the game's faction/AI and victory flow cannot be driven reliably from the async-PvP surface offline. A real live 1v1 needs an engine-level rewrite (custom combat scene and result UI) rather than reusing the shipped gauntlet.

## Decompiled game flow (Ghidra, second pass)

- `GameplayUI_PlacingView.StartBattle` (0x18067D8D0) does **not** spawn enemies directly. It sets `GameplayData.WaitingForEnemiesToSpawn = true`, clears several counters, calls `InvokePreBeginEvents()`, then `ProcessStartEffects(Action onFinish)` on the object at `GameplayData + 0x1A8`, and stores the wave properties. The callback finishes the transition — this explains why two presses were needed in the probe flow.
- `Wave.SpawnWave` (0x18062A770) obtains the wave king, calls `GetWaveCombatDeck(king)`, then `deck.GetBattlers()`, then private `CreateEnemies(position, onComplete, enemyTypes, enemies)`. The enemy army is the king's combat deck, not snapshot area counts.
- `Wave.GetWaveCombatDeck` (0x1806211A0) uses `KingSo + 0x1F8` as the custom deck when `HasCustomDeck` is true; otherwise it falls back to `KingCollection.GetEntry(king.Owner)` and that entry's deck at +0x1F8.
- `DeckSo.CreateRuntimeCopy` (0x1806C40E0) returns null unless `cards` and all three starter cards are non-null; the source deck's `StartCards_SlotXX` arrays supply them.
- `MultiplayerGameSnapshots.GetWaveSnapshot` (0x1808094C0) searches the snapshot list by wave number, with a separate comparator path when `fetchClosestPreviousSnapshot` is true.
- `Core.Wave.OnCompleteGame(EndUIType)` is the official end-screen entry; Victory=0, VictoryYear99=1, Defeat=2.

## Two-process validation of opponent decks

Ran both copies hidden with flags: attach-flow, single-load, auto-versus, custom-deck.
Both peers exchanged snapshots, installed a runtime opponent deck on the wave king and all enemy kings (8 battlers each), and spawned **45 entities flagged `IsMultiplayerEnemy=True` on each side** through the game's own enemy path.

Result: host lost (`1930` vs `2674` strength), join won. Both called `Core.Wave.OnCompleteGame` with Defeat/Victory respectively. The join's first call hit a LocalLow settings-file sharing conflict; the delayed retry succeeded (`OnCompleteGame(Victory)` logged).

This is the first end-to-end two-process run where each side fights an army built from the other side's kingdom, using the game's own troop spawning and AI.

Caveats: winner is still chosen by kingdom strength rather than combat outcome; after the end screen the battle is frozen (45 enemies remain alive), which is expected once a result is applied.

## Combat-derived result

Disabled the kingdom-strength shortcut so the winner comes from the fight. Elimination detection now keys off `OpponentDeck.LastWave/LastBattlers` instead of the old arena spawner.

Two-process run: both sides spawned opponent-deck armies (8 multiplayer enemies on host, 18 on join), combat reduced the host's army to 8 while its player units fell from 137 to 86. The host lost a life, and the existing life-divergence authority fired: host Defeat, join Victory, both `OnCompleteGame` called.

Life loss is the game's own combat outcome for a breached base, so this is a real combat-derived result rather than a static comparison. Army-elimination detection remains available but did not trigger because neither army reached zero before the life was lost.

## Opponent base rendering (decompiled, not solved)

`Wave.RefreshMultiplayerMap` (0x180627AA0) starts an async state machine (`Wave.<RefreshMultiplayerMap>d__232.MoveNext`, 0x180646630). The state machine:
- waits for despawn particles to finish,
- calls `MultiplayerManager.Session.GetDataByPlayerKing(king.Owner)`,
- calls that run's `snapshot.GetWaveSnapshot(wave, true)` and `wave+1`,
- calls the local helpers `LoadAreas`, `SetAreaCard`, `SetAreaStats`, `SetTroopQuantity`,
- uses `GetWallsRepresentation(true)` (the enemy wall representation at `GameplayData + 0x70`).

`GameplayUI_PlacingView.ApplyMultiplayerPreviewSnapshot` is only the preview UI, not the in-battle base.

We added a `refresh-map` flag that activates `Wave.MultiplayerMap`, sets `CurrentMultiplayerEnemySnapshot`, and invokes `RefreshMultiplayerMap`. In testing the map became active but enemy-side areas stayed empty — likely because the session's run king does not match the current wave king, or because the state machine needs additional frames/state. Base rendering remains unresolved.

Also added a `skip-intro` flag that jumps SplashScreen/IntroductionVideo straight to MainMenu, cutting test startup time.

## Base rendering requires real multiplayer mode

`refresh-map` test with `single-load` (WaveData.IsMultiplayer=false): map activated, but enemy areas stayed at 0.
Same test without `single-load` (WaveData.IsMultiplayer=true): `enemyTroops=1`, `enemyBig=2` after `RefreshMultiplayerMap` — the official enemy base/areas are created.

So the base path works, but only when the run's `WaveData.IsMultiplayer` is true. Combined with the opponent-deck hook, the remaining integration is to run true multiplayer mode with both the deck replacement and `RefreshMultiplayerMap`, and ensure combat still progresses (earlier true-multiplayer probes stalled without the deck hook).

Also expanded the fake session's run array to include a run for every CardOwnership value, because `RefreshMultiplayerMap` resolves the opponent via `Session.GetDataByPlayerKing(king.Owner)`.

## True multiplayer integration (validated)

Two-process run with flags: attach-flow, skip-intro, auto-versus, custom-deck, refresh-map (no single-load, so `WaveData.IsMultiplayer = true`).

Both peers:
- installed the 8-card opponent deck on the wave king and all enemy kings,
- invoked `RefreshMultiplayerMap` and produced enemy-side areas (`enemyTroops=3`, `enemyBig=4` on host),
- spawned opponent troops marked `IsMultiplayerEnemy=True` (9 on host, 18 on join),
- reached a single shared result from life loss (host Defeat, join Victory) with official `OnCompleteGame` end screens.

This is the closest validated configuration to the official game: real Gameplay scene, enemy base areas, opponent army through the game's own deck/CreateEnemies path, and one winner. Combat remains somewhat unstable in true multiplayer mode (entity counts fluctuate and waves can advance while the result is pending), but the full chain now runs end to end.

## Result freeze

Decompiled `Wave.<SetWave>d__218.MoveNext` (0x1806473F0): it calls `SetFixWave`, `HandCards`, `StartWave`, and `CompleteWave(callUIs, giveWaveMoney, applyConstructionPitGrant)`. So wave advancement runs through `SetWave` → `CompleteWave` → `StartWave`.

Added Harmony bool prefixes on `Core.Wave.SetWave` and `Core.Wave.CompleteWave` that skip the original when `VersusBehaviour.ResultPending()` is true (result handled, peer result received, or elimination reported).

Two-process validation: after the result, both peers stayed at wave 16 with `map=active`, enemy base areas present, and no further wave starts. Host `Defeat`, join `Victory`, official end screens. Post-result hand growth remained in the join copy; extended the periodic hand trim to the custom-deck configuration as well.

## Full multiplayer decompilation

Scraped every multiplayer/gauntlet/snapshot/profile/matchmaking method from `dump.cs` (480 methods) into `local/dump/mp-methods.md`-style surface report, then decompiled all 480 into `local/dump/multiplayer-decompiled.txt` (1.4 MB) with PyGhidra. Local-only artifacts; no game assets in the repo.

Important confirmations from the full corpus:
- `MultiplayerSession.GetDataByPlayerKing(CardOwnership)` (0x180810670) simply scans `Session.Data` comparing each run's `kingId` at `+0x28` — this is why filling the session with a run per king made `RefreshMultiplayerMap` resolve an opponent.
- `MultiplayerManager.EndMultiplayerRun` (0x18080BC60) is a server submit/abandon path; it is not usable offline.
- `GameplayUI_EndUI_Stats.ShowMultiplayerResults` (0x1804F58E0) is the official multiplayer results screen. Wired it after `OnCompleteGame`.
- `GameplayUI_MultiplayerEnemyDeathScreen.Show(KingSo, GameSnapshot, bool)` (0x18054BF00) is the enemy-death presentation, used by the official async flow.
- `TryJumpToMultiplayerVictoryWave` (0x1804F0EA0) checks `WaveOptions+0x2B8` and the current wave, then sets a flag on a run and calls a wave setter; it only works when a multiplayer run is actually active.

Best approach confirmed: keep the official Gameplay scene, install the opponent's deck on all enemy kings, build base areas with `RefreshMultiplayerMap`, sync peers over LAN, and use `OnCompleteGame` + `ShowMultiplayerResults` for a shared result. The async gauntlet/server path remains unusable offline.

## Enemy base visual solved

Area dump showed the scene contains only **25 `KingdomArea` objects** — the player's board. There are no separate enemy-base areas; `GetAreasWithAnythingPlaced(true)` reads the same objects with a multiplayer flag. The opponent base is rendered by a completely separate component.

`Gameplay.GameplayMultiplayerManager` has a `m_MapPreview` **property** of type `MultiplayerRemoteMapPreview`, plus the method `EnterEnemyMapView(MultiplayerRunData run, KingSo king, GameSnapshot snapshot)`. `MultiplayerRemoteMapPreview.Show(KingSo, GameSnapshot, bool)` builds a fake board with walls/lives/areas from the opponent snapshot.

Implemented `show-preview` flag in `LocalSessionProbe.ShowOpponentPreview()`: resolves `m_MapPreview`, calls `Show(king, snapshot, false)` (returned True), then `EnterEnemyMapView(run, king, snapshot)` using `Session.GetDataByPlayerKing(king.Owner)`.

Two-process run with `show-preview` enabled: the enemy base appeared on screen (owner-confirmed), while both sides remained in combat at wave 16 with `map=active` and enemy areas populated (`enemyTroops=6-7`, `enemyBig=7-9`).

Remaining: the preview is currently triggered once at startup rather than refreshed per wave; result/end-screen flow already works.

## Multiplayer gameplay loop / avatar bar

`GameplayUI_MultiplayerScreen` (0x180519E90 area) implements `IWaveBeginEventListener`/`IWaveEndEventListener` and owns:
- `m_UserFaceTemplate` (`GameplayUI_UserFace`)
- `m_Faces` list, `m_PlayerNameByPlayerId`, `m_EnemyKingByPlayerId`, `m_RunDataByPlayerId`
- `m_EnemyKingdomUI`, `m_BackGradient`, `m_EnemyKingdomNameText`
- `m_Holder`, `m_HolderRectTransform`, `m_HolderLayoutGroup`
- `m_PreviewedEnemyPlayerId`, `m_UseExpandedLayout`, `m_OnFaceClicked`

`PopulateEnemyFaces(MultiplayerRunData[] runs, Action<MultiplayerRunData> onFaceClicked, Func<bool> canInteractWithFaces)` clears the face list/dictionaries, stores the callbacks, empties the holder, then (when runs exist) splits runs into local-player vs enemy using `GameplayData+0x140` and each run's `kingId`, instantiating faces from `m_UserFaceTemplate` and filling the dictionaries via `TryResolveKing`. `UpdateFaces()` refreshes them on wave begin/end. This is the right-side avatar bar.

Attempted to call it from the probe with session runs, real managed delegates, and `Show()` first; it still throws NRE inside the method — most likely because `m_UserFaceTemplate`/holder are only initialized by the real multiplayer start path from the hub. Building the avatar bar will require either reproducing that initialization or rendering our own equivalent with the game's face sprites.

For "bots with real bases": the session can carry a run per king (already implemented), and each run's snapshot can drive both a `DeckSo` (army) and a `MultiplayerRemoteMapPreview` (base). The remaining work is iterating bots rather than a single opponent.

## Multiple bots with distinct decks

Added `OpponentDeck.InstallBots()` behind a `multi-bot` flag. It iterates `Core.Wave.GetEnemyKings()`, resolves each king's run via `Session.GetDataByPlayerKing(king.Owner)`, builds a separate runtime `DeckSo` from that run's wave snapshot, and installs it at `KingSo + 0x1F8`. The deck list is refactored into `BuildDeck(snapshot)` reused by both single-opponent and multi-bot paths.

Single-process validation: `bots: installed 5 distinct bot decks`. So a match can now have several AI opponents, each fighting with an army built from its own snapshot. The avatar bar (`PopulateEnemyFaces`) still throws NRE from the probe path, so the right-side avatar UI remains unimplemented.

## Right-side avatar bar (working substitute)

The official `PopulateEnemyFaces` NREs from the probe path because its UI is initialized only by the hub-driven multiplayer start. Instead added `avatar-bar` flag → `LocalSessionProbe.BuildAvatarBar()`: a UGUI panel anchored right-center with one row per opponent run, using `KingSo.Face` sprites (via `MultiplayerManager.TryResolveKing`) and `run.player.username`, plus a TMP label. Validated: `avatar: built right-side bar with 14 faces`.

This is a custom bar using the game's own face sprites and font, not the original `GameplayUI_MultiplayerScreen` widget, but it provides the right-side opponent UI the official mode shows.

## Multiple opponent bases

`MultiplayerRemoteMapPreview.Show` stores one generated root in `m_CurrentPreview` and destroys the previous one on each call. Added `multi-preview` flag → `LocalSessionProbe.ShowMultiplePreviews()`: for each session run it calls `Show(king, snapshot, false)`, grabs `m_CurrentPreview`, clones it, offsets the clone by 22 world units per bot, and keeps the clones alive. Validated: `multi-preview: built 12 opponent base previews`.

Combined with `multi-bot` (5 distinct decks from snapshots), a match can now have several opponents each with its own army and its own visible base.

## Full integration test

Two-process run with all flags (auto-versus, multi-bot, multi-preview, avatar-bar, refresh-map): both peers stayed at wave 16 after the result; shared life-based result fired; official `OnCompleteGame` end screen called. `ShowMultiplayerResults` NREs when called after `OnCompleteGame` and was removed — `OnCompleteGame` is the correct end-screen entry. Both sides now also call `SetRunning(false)` when the match ends so combat freezes.

## Native menu decompilation

Decompiled the game's UI flow into `local/dump/menu-decompiled.txt`:
- `GameplayUI.ShowSingleUI(UISubController<GameplayUI>)` (0x1804DE900) and `ShowNextUI(float delay)` (0x1804DE630) push a subcontroller into `GameplayUI`'s panel stack. `RegisterNextUI`/`UnregisterNextUI` manage the queued panel.
- `MainMenuUI_Main.MainMenuPanel.ShowPanel` (0x180875400) shows the native main-menu panel; other panels (StartGame, Singleplayer, Extras) have their own `ShowPanel`.
- `GameplayUI_PlacingView.ShowStartBattleButton(bool)` (0x18067C910) toggles the native Start Battle button.
- `GameplayUI_EndUI.SetUI(EndUIType)` (0x1804F3800) selects Victory/Defeat variants.
- `GameplayUI_MultiplayerScreen.ShowResults(Button newRunButton, float statsPanelWidth)` (0x18051B240) lays out the native multiplayer results panel.

Added `native-menu` flag → `LocalSessionProbe.ShowNativeMenu()` calls `GameplayUI.ShowSingleUI(GameplayUI.MultiplayerScreen)`; logged successfully. Visual appearance not yet confirmed.

The long-term native-menu approach is to reuse these `UISubController` panels instead of the hand-built UGUI overlays, populating them through their own `Show`/`Populate` methods once the hub-driven initialization is reproduced.

## Native avatar bar attempt — blocked

Probed the live `GameplayUI_MultiplayerScreen` object at its native field offsets: `m_Holder`, `m_UserFaceTemplate`, `m_EnemyKingdomUI`, `m_Faces`, the three dictionaries, `m_HolderRectTransform` and `m_HolderLayoutGroup` are **all non-null**. Calling `PopulateEnemyFaces` still throws NRE, even after filtering `Session.Data` to only runs whose `kingId` resolves through `MultiplayerManager.TryResolveKing`, and with real managed callbacks.

Conclusion: the official right-side avatar widget cannot be populated from our probe path. It is likely bound to the hub-driven multiplayer start's initialization sequence (or expects `MultiplayerManager` state we don't reproduce). Our `avatar-bar` flag builds a substitute from the game's face sprites — it is explicitly **not** the official widget.

`native-menu` (`GameplayUI.ShowSingleUI(MultiplayerScreen)`) calls succeeded but presented a full-screen subcontroller that locked normal controls; it is not a usable overlay menu. The native VERSUS button in the main menu is the only native menu integration that works.

## Why the real multiplayer start/UI can't be used

Tried `MultiplayerManager.StartGame(instaLoad=true, forceScene="Gameplay")` from the hub with a fabricated session. It failed with:

`[StartGame] Matchmaking failed. StatusCode: 0, ErrorMessage: No multiplayer auth token is currently stored. Call Login or Register first.`

It then triggered the game's own `AbandonRun` HTTP request (status 200, rank -3). This is a server-dependent path and violates the offline-only constraint, so the `official-start` flag and method were removed from the plugin immediately.

This is the concrete reason the official multiplayer UI (including `PopulateEnemyFaces`) can't be initialized offline: the widget is set up by the hub→matchmaking→StartGame sequence, which requires a real auth token and server response. Our session-injection shortcut bypasses it, leaving the widget unusable.

Disclosure: the test made one unintended online AbandonRun request from the disposable test copy. No account credentials were used; the offline gate only patches the connectivity check, not the HTTP client itself.

## Player king / perk choice

Decompiled `MultiplayerManager.SelectKing` (0x18080D370): stores the chosen `KingSo` at manager +0x50 and updates the hub background/curtains. Decompiled hub perk-submit and `MultiplayerGameSnapshots.SetPerks` (0x1808097B0): perks are stored in a static list at `MultiplayerGameSnapshots + 0x20`, and the hub UI writes a selected perk loadout index per king.

Added `choices.json` (`kingId`, `perkIndex`) plus KING/PERK buttons on the native main-menu panel. `AttachForMultiplayerStart` now applies the chosen king in the hub before building the run: validated with `choices: king=... perkLoadout=2 (stored)` and `attach-pre ... king=KingOfNothing`. The chosen king becomes the local run, with the opponent using the other king.

Perk loadout index is stored but not yet applied to the run — the exact helper (`SetSelectedPerkLoadoutIndex`) lives on a settings object reached through a private decompiled helper, not on `KingSo`/`KingCollectionEntry`.

## Clean-arena branch (result)

Created branch `clean-arena` and rebuilt the spawner from first principles:

- Used Troop's internal factory with a real KingdomArea and `isEnemy=true`.
- Called `RegisterOnWaveLists()`; it throws duplicate-key because the factory already registers troops.
- Called `Wave_Begin()` / `SetReady()`; troops still do not fight.
- Tried replacing `Wave.WaveTroops` with opponent troop types before Start Battle. This removed all enemies: `WaveTroops` is the player's own deck/wave troops, not the enemy composition.

Conclusion: there is no exposed offline path that constructs a controllable enemy army with working AI. The clean branch confirms the earlier finding. No further hook-based iteration is worthwhile.

Recommendation: archive the milestone; a real implementation needs a custom combat scene/engine built outside the shipped Gameplay flow.

## Two-process loopback: verified result flow (2026-11-19)

Two disposable copies (`local\testgame` host, `local\testgame2` join) were launched together over loopback TCP (port 28764) with `menu-start-host` / `menu-start-join`. Verified in both logs:

- Both connect, load the hub, attach the session and reach Gameplay (`load-run ... returned True`, `scene changed: Gameplay`).
- Ready/go handshake and continuous snapshot exchange (`net: peer ready`, `net: peer says go`, `net: received opponent state`); each side rebuilds the other's board (`enemyTroops`, `enemyBig`, map active).
- Both start combat through the official `GameplayUI_PlacingView.StartBattle` twice (first stages/spawns, second engages) plus `Wave.StartCombat`, with `WaitingForEnemiesToSpawn=false` set to avoid the coroutine stall.
- A single shared result is decided by the host and applied on both: `match-result: timeout decision ... loser=join` → `applying victory on host` / `applying defeat on join` → `OnCompleteGame(Victory)` / `OnCompleteGame(Defeat)` (official end UI). Waves are frozen after the result (`SetWave`/`CompleteWave`/`StartWave` blocked).

### Fixes proven by this test
- **Menu spam:** `TryInstallMainMenuButton` used `GameObject.Find("VersusBtn")`, which ignores the button when the main-menu holder is deactivated; it re-cloned every frame (~299k log lines in 35 s). Now holds the clone in a static field: 1 clone, ~150 log lines per run.
- **Menu match never started:** `StartVersusMatch` only wrote `net.json`; it now also writes `auto-versus` + `multi-bot` and resets match state. Without these flags `AutoVersusStep`/`MatchResultStep` never ran.
- **Asymmetric handshake:** the join waited for a separate ready echo and fought a wave behind the host; `go` is now authoritative for the join.
- **Wave desync:** once combat engages, `ResultPending()` freezes `SetWave`/`CompleteWave`/`StartWave` so a peer cannot auto-advance and leave the other fighting alone.
- **Combat stall:** `StartBattle` sets `WaitingForEnemiesToSpawn=true` after `SpawnWave` already ran in the attached run; explicitly clearing it lets `CombatOn` go true.

### Honest limitations
- The attached session's combat simulation stalls: entity counts stay constant and no life is lost. The result therefore comes from the host's 18 s timeout decision (fewer lives loses; exact tie → host), not from units actually killing each other. Real army-elimination results are still not achieved.
- Perk loadout is still only stored, not applied.
- Only loopback two-process tests have run; no true two-PC LAN validation yet.

## Full match loop (2026-11-19, part 2)

Decompiled the official flow: `MultiplayerManager.StartGame.MoveNext` (matchmaking → `MultiplayerSession(runs, season pools)` → `Wave.StartNewGame(isMultiplayer=true)` → `KingManager.GetAllKingActivePerks(owner)` → `MultiplayerSession.Snapshots.SetPerks`); `Wave.PrepareMultiplayerGauntlet` → `Session.PopulateGauntletEnemies()` → `BecomeEnemy()` on the opponent kings; `PlayerBase.ReceivedMultiplayerEnemyBaseDamage`/`ReceivedLethalDamage` for life loss.

`Wave.StartNewGame(isMultiplayer=true)` NREs offline in every argument variant (hub manager alive or not), so the official run-init path is unavailable. We load from a snapshot (`LoadGame`) and reimplement the loop:

- **Perks applied for real:** `KingManager.AddPerkSettings(owner, PerkSettings{Name, Level})` then `GetAllKingActivePerks` → `MultiplayerGameSnapshots.SetPerks`. 30 perk terms exist. Verified: `perks: applied 1 active perks for KingOfNothing (loadout=2)` on both peers.
- **Multi-wave loop:** the host resolves each wave after a 12 s grace period (lower life count wins; then snapshot `UnitsKilled`; then stable alternation by wave number), broadcasts a `wavewin` message, both peers decrement the loser's life and advance via `SetWave`. The match ends when a life reaches 0. Verified waves 15–20 resolving in sync: host `0/2` → `OnCompleteGame(Defeat)`, join `2/0` → `OnCompleteGame(Victory)`.
- Removed the single-wave freeze (`ResultPending` now freezes only after the match result). This replaces the earlier one-shot timeout result.

Still unverified: real units fighting (attached-session combat stalls), army-elimination results, true two-PC LAN.

## Fresh start and enemy splash (2026-11-19, part 3)

- The match now begins at wave 0 on a genuinely empty board. `BuildFreshSnapshot()` clears all placements (0 troops / 0 buildings), keeps one level-1 Citadel, resets lives to 3 and the hand to Library/Warlock/Offering, and sets the chosen king/opponent. Both session construction and `LoadGame` use it. Verified: `fresh: built ... troops=0 buildings=0`, `load-run: snapshot wave=0`.
- A fresh board makes the game's own `CaptureSnapshot()` return null (and sometimes throw in `CastleBase.GetMaxHP → Entity.GetWaveBoosts`), which used to kill state exchange. `SnapshotDump.WriteFallback` now sends our own snapshot JSON instead, so the handshake completes from wave 0.
- Handshake and auto-versus accept wave 0; the session carries a 64-wave snapshot series so `GetWaveSnapshot` resolves as the game advances (loop verified through wave 32).
- Enemy reveal: `GameplayUI/Multiplayer Screen` is present in the Gameplay scene but inactive. `GameplayMultiplayerManager.Start` runs with `gauntlet=1 active=True` but leaves it off. `ShowMpScreen()` activates it and calls `PopulateEnemyFaces(runs, onFaceClicked, canInteract)` — now succeeds with the properly attached session (the earlier NRE was a symptom of the broken session), and runs once per match. Verified: `mp-screen: populated 1 enemy faces`.

### Official StartNewGame unblocked

`Wave.StartNewGame(isMultiplayer=true)` NRE'd because the reflection call passed `null` for the `Nullable<int> seed` argument; the IL2CPP wrapper calls `Il2CppObjectBaseToPtrNotNull` on it. Passing `new Il2CppSystem.Nullable<int>(12345)` makes it succeed. The attach flow now uses the official initializer (attach session → `StartNewGame` → `SetPerks`) instead of loading a snapshot. Verified `start-mp: invoked OK`, `GameplayMultiplayerManager.Start session=set gauntlet=1 active=True`, native `Multiplayer Screen active=True`.

The avatar bar NRE had two causes: `GameplayUI_UserFace.Setup` (0x180542710) dereferences `run.player.settings` (fixed with `PlayerSettingsData.CreateLocalSnapshot()`), and it calls `KingManager.GetEntry(run.kingId)` for every session run — fabricated runs for fake owners (`No_King`, `All_Kings`, `Joker`...) have no entry and throw. The session now contains only the nine real kings. Verified 9 face setups with no NRE.

### Playability correction
A first fresh-board version broke the actual game loop: the capture's 25 unlocked plots were kept (territory larger than 3x3), `IsMultiplayer=true` made the run expect a server gauntlet (no rewards, no card play), forced `SetWave(skipShop:true)` suppressed the shop, and `AutoVersusStep` cleared the hand by invoking `StartBattle` immediately. Corrected: 3x3 unlock pattern restored; run stays `IsMultiplayer=false` with the opponent injected via `SetMultiplayerCard`/`SetMultiplayerLife`; forced wave advancement removed so the game's own completion gives rewards and rebuilds the placing view; handshake no longer auto-starts combat. Verified both peers remain at wave 0 with a playable hand and the enemy faces visible. A human playtest of card placement and rewards is still outstanding.

## Stage 0 facts (from docs/DESIGN-PvP.md, 2026-11-20)

- **(a) Battle start entry point:** captured stack in a live run is `VersusBehaviour.Update() -> Core.Wave.StartCombat(bool)` — our own forced call. With the force flag removed and the player not clicking anything, no `StartCombat` fires at all in 55 s: the game does not auto-start a battle at hand <= 2 in this version. The native entry point is the player's Start Battle button (`GameplayUI_PlacingView.StartBattle` -> `StartCombat`). A `go` barrier has to intercept that button path.
- **(b) Combat stall:** `diag:` at 5 s intervals with `arena` flag during a forced battle shows `running=True combatOn=True waitingForEnemies=False entities=2 (players=1 enemies=1 multiplayer=1) waveTroops=1 validTroops=1` for several waves. The board is almost empty — only the two castles. Lives change between waves but no units actually fight. The stall is a consequence of an empty board, not a broken damage path.
- **(c) PrepareMultiplayerGauntlet with fixed session:** not yet verified in isolation; current flow calls it via StartNewGame with the attached session and it populates GauntletEnemies=8, but `CurrentMultiplayerEnemySnapshot` is only set by our BuildEnemyBoard, not by the native path.
- **(d) Enemy kings per round:** `bots: installed N distinct bot decks` from logs; with 9 session runs the gauntlet has 8 opponents. The exact native gauntlet selection per wave still needs a dedicated run with `PrepareMultiplayerGauntlet` traced.
- **(e) Steamworks:** present. `dump.cs` includes `com.rlabrecque.steamworks.net.dll` (Image 49) with `Steamworks.SteamClient`, `SteamAPI.Init()`, `SteamAPI_RestartAppIfNecessary`, `AppId_t`, networking interfaces. No `SteamManager` class in this build. AppID used for test launches: 2784470.
- **(f) Snapshot size:** ~30,661-30,663 bytes per snapshot at wave 56-57 (JSON). For 9 players the host would receive 8 x ~30 KB per state broadcast — negligible on LAN even at 10 Hz (~2.4 MB/s).


## Owner decisions on design open questions (2026-11-20)

1. **Who fights whom:** Native — the game picks 3 enemies for each player. Use the game's own gauntlet selection, not all-vs-all or a ring.
2. **Eliminated player view:** End screen only. Spectator mode is a possible future addition, not now.
3. **Eliminated/disconnected player's army:** Remove it from later rounds.
4. **Placement order when the last players lose their final life in the same round:** "Whose base fell later" — confirmed.
5. **Level B (shared battle):** Defer; I decide.


## Stage 1 started (foundation)

Added `src/NineKingsVersus/VersusQueue.cs`: a thread-safe message inbox replacing the old "last value wins" slots. Per-sender sequence dedup (duplicates and stale messages dropped), ack tracking, FIFO drain, reset for a new match. All network messages require seq >= 1. Added `tests/VersusQueueTests/` — a no-game console test project that links the source file and runs 12 assertions (FIFO, dedup, stale drop, per-sender independence, malformed drop, reset, concurrent enqueue). All tests pass; plugin still builds. Not yet wired into VersusNet — that is the next Step 1 task.


## Stage 0 — final facts (all six answered)

- **(a) Battle-start entry point:** captured stack in a live run is `VersusBehaviour.Update() -> Core.Wave.StartCombat(bool)` — our forced call. With the force flag removed and nothing clicked, no `StartCombat` fires in 55 s: the game does NOT auto-start at hand <= 2. Native path is the player's Start Battle button (`GameplayUI_PlacingView.StartBattle -> StartCombat`). Stage 2's `go` barrier must intercept that button, not an implicit wave call.
- **(b) Combat stall:** with the `arena` flag, `diag:` every 5 s during a forced battle shows `running=True combatOn=True waitingForEnemies=False entities=2 (players=1 enemies=1 multiplayer=1) waveTroops=1 validTroops=1` for several waves. The board is nearly empty (just two castles) — the stall is a consequence of an empty board, not a damage-path bug. Lives changed between waves via our synthetic result layer, not from real fighting.
- **(c) PrepareMultiplayerGauntlet with the fixed session:** it runs without throwing and the session is valid (9 runs x 65 snapshots, `byKing=found`). But the native method is gated by several early returns — `GameplayData +0x1a8`, `WaveProperties +0x14 == 5`, and `GauntletEnemies.Count == 0` / `GauntletActive == false`. Our manual gauntlet (8 pre-populated) made `Count > 0`, so `PopulateGauntletEnemies` was skipped; a `native-gauntlet` flag that leaves it empty still produced no native snapshot (`PrepareGauntlet out: snapshot=set wave=0` is our own `EnsureEnemySnapshot` fallback). So the native path does NOT currently populate the gauntlet or the opponent snapshot — we still supply both ourselves. Needs more work in Stage 2/3.
- **(d) Enemy kings per round:** current flow yields exactly 1 active enemy king per wave (`KingOfSpells` across waves 0-8), not the native 3 per player. Owner confirmed the native game picks 3 enemies for each player. Root cause is (c): the native gauntlet/RefreshWaveKing path is bypassed, so our single injected snapshot drives the wave. Stage 4 must restore the native 3-enemy selection.
- **(e) Steamworks:** bundled (`com.rlabrecque.steamworks.net.dll`, Image 49) with `Steamworks.SteamClient`, `SteamAPI.Init()`, `SteamAPI_RestartAppIfNecessary`, `AppId_t`, networking interfaces. No `SteamManager` class in this build. Test AppID 2784470.
- **(f) Snapshot size:** ~30,661-30,663 bytes at wave 56-57. For 9 players, host receives 8 x ~30 KB per state broadcast — negligible on LAN even at 10 Hz (~2.4 MB/s).
- **Decompiled this stage:** `PrepareMultiplayerGauntlet` (0x180626D90), `Session.PopulateGauntletEnemies` (0x1808106D0), `Wave.GetEnemyKings` (0x180620B50), `PauseMenu.OnMenuBtn` (0x180674DD0), `PlacingView.RerollKing` (0x18067B590).

