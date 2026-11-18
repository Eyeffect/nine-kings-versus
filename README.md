# 9 Kings Versus — private LAN prototype (incomplete)

Status: **prototype, not a playable 1v1 mode**. It is kept as a research artifact.

## What works

- Two local copies of the game can connect over private LAN (host/join TCP,
  length-prefixed JSON, no internet submission).
- Kingdom snapshots are exchanged; a ready/go handshake starts the same wave on
  both copies; a host-authoritative life/result message gives both copies the
  same victory/defeat decision.
- The game's own Start Battle / hand-card flow can be driven programmatically.
  In single-player wave mode this produces a complete, advancing battle.

## What does not work (tested and confirmed)

- Running with `WaveData.IsMultiplayer = true` makes the camera pull back for the
  multiplayer arena, but the enemy base is never constructed offline. The
  opponent's cards can be injected with `SetMultiplayerCard` (entities exist in
  memory, `IsMultiplayerEnemy` is set), but no visible base appears and no enemy
  army spawns into it. Both sides end up rushing an empty base.
- Victory is still the local single-player rule (all wave enemies dead), so both
  copies can report a win for the same fight.
- Rewards can be re-issued when the game re-runs `StartWave` for a wave; blocking
  those calls breaks the wave flow, so this remains unresolved.

## Conclusion

The shipped async-PvP snapshot pipeline cannot be rebuilt into a live 1v1 mode
by feeding snapshots alone: the enemy-base construction path depends on the
server-driven multiplayer session and fails offline. A real live mode would need
a custom arena spawner (build both armies from snapshots into one scene) plus a
shared result authority, instead of reusing the async gauntlet.

## Files

- `src/NineKingsVersus/VersusNet.cs` — LAN transport, ready/go and result messages.
- `src/NineKingsVersus/LocalSessionProbe.cs` — session/snapshot and board probes.
- `src/NineKingsVersus/VersusBehaviour.cs` — flag-driven probe orchestration.
- `src/NineKingsVersus/Patches.cs` — Harmony instrumentation and submission block.
- `games/9-kings/NOTES.md` (in the private workspace) — full probe history.

## Safety

- Never modify the shared game install or real saves.
- `OfflineGatePatch` disables the game's internet check; `BlockSubmitPatch`
  blocks online run submission.
- All testing used disposable copies of the game. The owner's save backup was
  read-only and remains intact.
