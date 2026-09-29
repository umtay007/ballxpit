# BALLxPIT Online Co-op

An add-on for sparrow's [BALLxPIT: Local Coop](https://www.nexusmods.com/ballxpit/mods/24) (0.1.0)
that lets a friend anywhere play Player 2. Only the host runs the game. The friend opens a link in a
web browser, watches the host's screen, hears the game and controls P2 with keyboard and mouse or a
controller. Players install it with [docs/INSTALL.txt](docs/INSTALL.txt).

## How it works

```
 host PC (BALL x PIT + BepInEx)                                   friend's browser
 ┌───────────────────────────────────────────────┐
 │ Local Coop (patched): native P2 ◄─ input hooks│
 │ Online Coop plugin                            │   https://*.trycloudflare.com
 │   end-of-frame ReadPixels ─► JPEG (parallel) ─┼──► cloudflared ──► Cloudflare ──► page: <canvas>,
 │   AudioListener tap ─► IMA ADPCM ─────────────┤                                  Web Audio,
 │   HTTP + WebSocket server ◄───────────────────┼─── keys / mouse / gamepad ◄──── input as JSON
 └───────────────────────────────────────────────┘   (or a direct UPnP / LAN link)
```

- **Input.** The build adds hooks to Local Coop's `PlayerTwoController` (with Mono.Cecil, see
  `patcher/`): its `Input.GetKey` reads also see keys the guest holds (Shoot), and
  `UpdatePlayerTwoInput` calls back right before it applies P2's movement and aim. The add-on puts
  the guest's stick into `_playerTwoMoveDir` and the guest's aim, run through the game's own
  `BallMgr.MousePosToAimDir`, into `_playerTwoAimDir`. Everything else, such as shooting rules,
  recall, game over and the aim preview, is Local Coop's own code, unchanged. The add-on reaches Local Coop's
  private members through `IgnoresAccessChecksTo`, compiling against a publicized copy.
- **Video.** After each rendered frame (`WaitForEndOfFrame`) the back buffer is read with
  `Texture2D.ReadPixels`, but only when a guest is ready for another picture. A managed baseline
  JPEG encoder shrinks it (1080 lines to 540) and encodes horizontal strips in parallel, joined with
  restart markers. Guests acknowledge every frame. From the acknowledgement delays the host
  works out how many frames may be on the way: more on high-latency links, so the round trip
  doesn't cap the frame rate, and fewer as soon as frames start queueing on a slow link, so lag
  stays low. When the connection is what holds the frame rate down, JPEG quality drops and then
  the picture shrinks another step; both climb back when there's room.
- **H.264.** Browsers that can decode H.264 with WebCodecs (Chrome, Edge, Firefox, Safari; https
  or localhost pages only) get an H.264 stream instead of JPEG pictures, several times smaller for
  the same picture. The encoder is Cisco's OpenH264 (constrained baseline, rate-controlled),
  downloaded from Cisco at first use and checked against a SHA-256 (the `.bz2` is unpacked by a
  small built-in bzip2 decoder), called through its C++ function table. A guest that misses a frame
  waits for the next keyframe; keyframes are sent on join, on request and when the picture size
  changes. The bitrate drops by 30% while frames queue and climbs while the stream uses what it
  has. On slow links the sound goes to half rate. "OpenH264 Video Codec provided by Cisco Systems, Inc."
- **Audio.** BALL x PIT plays sound through FMOD, so a pass-through DSP is added at the head of
  FMOD's master bus (straight through `fmod.dll`'s exports, with the handle from
  `FMODUnity.RuntimeManager.CoreSystem`); it copies the final mix and sends 20 ms IMA ADPCM packets
  (about 390 kbit/s for 48 kHz stereo). A tap next to Unity's `AudioListener` is the fallback.
- **P2's own health.** Harmony patches on `Player.Damage` / `Player.Heal` swap P2's health into
  `BattleSaveData.CurHealth` while they run for P2 (first and last in the patch order, so Local
  Coop's game-over guard sees P2's value). At zero P2 is knocked out for a while: no movement (input
  bridge), no shots (hook version 2 in `RequestNativePlayerTwoShot`) and no damage.
- **P2's own balls and level-ups.** Local Coop swaps P2's ball list into `BattleSaveData.Heroes`
  while P2's code runs, but builds it from copies of P1's. Hook version 3 lets the add-on own that
  list instead (`TakeOverPlayerTwoHeroes` at the top of `EnsureInventoryReady` and
  `InvalidateForHeroChanges`), and tells it whenever Local Coop's P2 context starts or ends
  (`OnInventoryContextChanged`). At those moments the add-on also swaps `BattleSaveData.Passives`
  and `UpgradeMgr`'s derived numbers (every primitive instance field, found through IL2CPP's field
  list and copied as raw memory, plus its cached `PassiveInst` references; `TgtXP` stays shared), so
  P2's balls hit with P2's stats. P2's numbers come from the game's own `UpgradeMgr.CalculateStats`,
  run inside P2's context. Each rise of `BattleSaveData.UpgradeLvl` gives P2 a pick, offered on the
  guest page and in the panel and applied with `UpgradeMgr.ApplyUpgrade` inside P2's context.
  `LevelUpUI.Activate(kFuser)` gives P2 a fuser of its own: Fission (1-5 random upgrade levels), or
  the Evolutions and Fusions the game lists for P2 (`LevelUpUI.PopulateUpgrades` run in P2's
  context, then `_availMerges` / `_availHCombos`), applied with `ApplyUpgrade` / `CombineHeroes`.
- **Sync test (towards playing on two PCs).** Lockstep play needs the game to be deterministic:
  same start + same inputs = same frames. `Game/Sync` checks that. A test run fixes the frame time
  (`Time.captureDeltaTime` = 1/60 through IL2CPP internal calls, vsync off, 60 fps cap), keeps
  `BattleSaveData.Seed` fixed from `GridMgr.InitGrid` until the fight starts (the level's generator
  is made from it every turn), starts `TimeMgr._gameTime` at an agreed power of two (the game's
  float timers round differently at different clock values), reseeds `GridMgr.MiscRnd`,
  `ThreadSafeRandom` and `UnityEngine.Random`, places P2 exactly, counts which threads draw from
  `ThreadSafeRandom`, and replaces P1's `InputMgr` gameplay actions and mouse aim with a fixed
  pattern. Every frame it records time, players, health/XP, every enemy (`BattleSaveData.Pieces`),
  every ball, pickups and the random generators' full state, plus a setup fingerprint (character,
  level, character stats, every `UpgradeMgr` number, balls, passives). A check run compares frame by
  frame and reports the first difference.
- **Host panel.** Press F8, or click the "Online Co-op" button in the top-right corner of the menus
  (it also shows that the plugin loaded). It's drawn with IMGUI's `GUI.Button`, the one IMGUI call
  Local Coop already relies on.
- **Reaching the host.** A small HTTP/WebSocket server (no HttpListener, so no admin rights) serves
  the page in `client/index.html` and the stream. Cloudflare quick tunnels give an https link that
  works through any NAT; UPnP adds a direct link when the router allows it. The link's `#code` is
  the password.

## Build

Needs the .NET 8 SDK and the original `BALLxPITLocalCoop.dll` 0.1.0 from Nexus Mods. The DLL isn't
in this repository.

```sh
./build.sh path/to/BALLxPITLocalCoop.dll
# dist/BALLxPITOnlineCoop.zip               the two mod DLLs, for a game that already has BepInEx 6
# dist/BALLxPITOnlineCoop-with-BepInEx.zip  plus BepInEx 6.0.0-be.788 (IL2CPP, win-x64) with
#                                           UnityLogListening off, as Unity 6 games need
# dist/Install-BALLxPIT-OnlineCoop.bat      one-click installer: carries the first zip inside, finds
#                                           the game through Steam and fetches BepInEx if missing
```

The plugin compiles against BepInEx 6 be.788 / Il2CppInterop 1.5.3 (fetched by
`tools/fetch-deps.sh`) and against `stubs/`. These are stand-ins for the game's interop assemblies
and declare only the members the plugin calls, with Il2CppInterop's exact signatures. Apart from
`ReadPixels`, `GetRawTextureData`, the `Texture2D` constructor, `ScreenToWorldPoint`,
`WaitForEndOfFrame` and `AudioSettings.outputSampleRate`, every Unity or game member it uses is one
Local Coop already calls. Those six were checked against Unity 6000.0's assemblies, and each is
isolated so a failure only disables its own feature.

## Test

```sh
dotnet build test/FakeHost/FakeHost.csproj -c Release -o out/fakehost
cd test && npm install && node e2e.mjs
```

`node stream.mjs "rtt=60 down=30" "rtt=60 down=3"` runs the same stream through `netsim.mjs`, a
proxy that adds latency and caps bandwidth like a real internet link, and prints the frame rate,
lag, quality and picture size the guest ends up with.

`FakeHost` runs the networking core outside the game with synthetic 1080p frames and a test tone.
`e2e.mjs` drives the guest page in headless Chromium and checks 24 things. They include the picture
and its orientation, frame rate, keyboard, mouse and turn-aim input reaching the host, the
auto-shoot toggle, decoding of the audio test tones, spectators, wrong codes, taking over P2 and
reloading the page.

## Limits

- Tested outside the game only. The in-game parts can't be exercised without BALL x PIT (capture,
  audio tap, the P2 bridge and the panel). They are written to fail soft and log to
  `BepInEx/LogOutput.log`.
- It streams the host's screen, so the guest sees what the host sees. Menus and level-ups stay with
  the host.
- Picture quality is limited by the host's upload. Expect roughly 5–15 Mbit/s at 960×540 and 30 fps.
