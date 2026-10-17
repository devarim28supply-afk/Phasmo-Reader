# Phasmo Reader

Deaf-accessibility tool for **Phasmophobia**: every sound the game plays shows up on screen as a
mark on a ring around the crosshair, pointing the way the sound came from.

- 🔴 red circle with **S**: a ghost sound (ghost voice, ghost events, whispers)
- 🔴 red circle with a **footprint**: ghost footsteps
- 🔵 blue: other players' footsteps
- dots: doors, light switches, equipment, objects

Each mark carries a name, and it is bigger the closer the sound is. Your own footsteps and items are
hidden. Menus, rain, ambience and the truck are never shown.

It reads the game from **outside**, read-only (`ReadProcessMemory` only). Nothing goes into the
game folder, no mod, nothing injected. It does not listen to audio: it reads which sounds the game
is playing, and where.

Separate from The Other Side Reader (`The-Other-Side` repo): same overlay look, its own copy here.

## What is here

| Folder | What it does |
|---|---|
| `reader/PhasmoReader` | The outside reader (.NET 10). Finds Unity's `AudioSource` / `AudioListener` classes in the IL2CPP game, reads every playing source's world position and the listener's position and facing, names each sound (`PhasmoFeed.Classify`), and writes `soundfeed.txt` 20 times a second. Extra modes for working on it: `probe`, `names`, `all [diff\|playing]`, `listener`, `record` (a text log of which sources play, no audio). |
| `overlay/` | **OtherSideSound.exe**, the WPF overlay that draws the ring from the feed (fork of the PhasmoSound overlay). `default-config.json` = clean starter config: sounds on screen only. |
| `reader/install-phasmo-reader.cmd` | Builds both into `D:\Tools\phasmo-reader` and makes the "Phasmo Reader" desktop shortcut. Keeps an existing `config.json`. |
| `reader/friend/make-friend-zip.ps1` | Builds `PhasmoReader.zip` for friends: self-contained (.NET inside), nothing to install. |
| `reader/friend/friend-config.js` | Remakes `overlay/default-config.json` from a live config (strips voice, captions, mic, hotkeys, personal paths). |

## Feed format

One line per event, Unreal convention (cm, X forward, Y right, Z up; the reader converts from Unity):

```
L x y z yaw            listener (you)
S id name x y z range src   sound started / moved   (src = ghost | player | own | other)
E id                   sound ended
2 name src             sound with no position (heartbeat)
M                      new match
```

## Use

Friends: unzip `PhasmoReader.zip`, run `Start Phasmo Reader.cmd`, play in Borderless.
F8 hides / shows the overlay, Ctrl+F8 quits it.
