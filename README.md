# 9 Kings Versus — private LAN prototype

Unofficial, private mod for **9 Kings 1.0.3.1** (IL2CPP, BepInEx 6). It lets two
or more private copies of the game play a simultaneous match on a LAN using the
game's own visuals, troop AI, base renderer and end screens.

This is a **working prototype, not a finished release**. See "Known issues".

## What works

- Host/join TCP transport over private IPv4 only; snapshots, wave numbers and a
  shared result are exchanged. Online submission is blocked and the game's
  internet check is forced off.
- Real Gameplay scene with `WaveData.IsMultiplayer = true`.
- Opponent armies built from each bot's snapshot via a runtime `DeckSo` installed
  on the enemy king's custom-deck field (`KingSo + 0x1F8`). The game's own
  `CreateEnemies` spawns them with full official AI.
- Multiple bots: `multi-bot` installs a distinct deck per enemy king.
- Enemy bases: `show-preview` renders one base; `multi-preview` clones the
  preview per bot so several bases appear at once.
- Right-side opponent avatar bar built from the game's king face sprites.
- One shared winner from combat (life loss / army elimination), official
  `OnCompleteGame` end screen, and a freeze on wave progression after the result.
- Native in-game menu: a **VERSUS** button in the game's main menu.

## Build

```
dotnet build src/NineKingsVersus/NineKingsVersus.csproj -c Release
```

Output: `src/NineKingsVersus/bin/Release/net6.0/NineKingsVersus.dll`.

Copy the DLL into a **disposable test copy** of the game at
`<game dir>/BepInEx/plugins/NineKingsVersus/`. Never modify a shared install or
real saves. The build expects BepInEx/Unity assemblies in the test copy; see
`Directory.Build.props` for the `GameDir` property.

## Running a match

In `<game dir>/BepInEx/versus/`, create `net.json`:

```json
{ "mode": "host", "peer": "127.0.0.1", "port": 28764 }
```

Join copies use `"mode": "join"` and the host's LAN IP. Then create empty flag
files in the same directory. The menu toggles write these for you.

Useful flags:
- `attach-flow` — load a captured run at startup
- `skip-intro` — jump straight to MainMenu
- `auto-versus` — synchronized wave start
- `multi-bot` — one opponent deck per enemy king
- `multi-preview` — render several opponent bases
- `avatar-bar` — right-side opponent faces
- `refresh-map` — run the official enemy-area builder
- `custom-deck` — single-opponent deck replacement
- `show-preview` — single opponent base preview
- `dump-troops` / `dump-menu` / `dump-areas` — development probes

Press **Ctrl+Alt+F11** or click the **VERSUS** button to open the menu.

## Known issues

- Only loopback two-process tests have been run; a real two-PC LAN test is still
  needed. A two-process test now reaches a shared result with official end screens
  on both copies (`timeout decision ... loser=join` → `OnCompleteGame`).
- The attached session's combat can stall (constant entity counts, no life loss).
  Waves are frozen once combat engages so neither peer can advance alone, and the
  host decides after an 18 s grace period if no life was lost (fewer lives loses;
  exact tie goes to the host). Army-elimination results are not yet achieved.
- The two local copies share the same `LocalLow` settings folder, causing
  save-file conflicts; end-screen calls have a retry.
- `GameplayUI_MultiplayerScreen.PopulateEnemyFaces` (the original avatar widget)
  NREs outside the hub-driven multiplayer start; the `avatar-bar` flag builds a
  substitute from the game's sprites.
- No installer or one-click host/join flow yet.

## Reverse engineering notes

`docs/FINDINGS.md` records the Il2CppDumper/Ghidra workflow, method addresses,
decompiled behaviour, and the reasoning behind the chosen approach. All dumps and
decompiled output live in the private workspace, not in this repo.
