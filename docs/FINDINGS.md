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

## Clean-arena branch (result)

Created branch `clean-arena` and rebuilt the spawner from first principles:

- Used Troop's internal factory with a real KingdomArea and `isEnemy=true`.
- Called `RegisterOnWaveLists()`; it throws duplicate-key because the factory already registers troops.
- Called `Wave_Begin()` / `SetReady()`; troops still do not fight.
- Tried replacing `Wave.WaveTroops` with opponent troop types before Start Battle. This removed all enemies: `WaveTroops` is the player's own deck/wave troops, not the enemy composition.

Conclusion: there is no exposed offline path that constructs a controllable enemy army with working AI. The clean branch confirms the earlier finding. No further hook-based iteration is worthwhile.

Recommendation: archive the milestone; a real implementation needs a custom combat scene/engine built outside the shipped Gameplay flow.
