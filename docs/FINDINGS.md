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

## Conclusion

The async gauntlet pipeline records local progress and resolves opponents on the
server; it does not provide an offline path that constructs a live enemy base.
Feeding snapshots into it produces in-memory enemy entities but no visible base
or army. A true simultaneous 1v1 needs a custom arena: resolve both players'
`TroopSo` types by name (`Core.Game.GetTroop(name)`), spawn both armies into one
scene through the existing spawn API, and keep the shared result authority.
