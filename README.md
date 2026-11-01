# VS Mem Cleanup

Vintage Story mod. Frees each world when you leave it.

A mod that leaves a handler or reference on something that lasts the whole game keeps that
world's client or server in memory, a few GB per world loaded since the game started. VSImGui 1.2.7
and ConfigLib 1.13.1 both do:

| Mod | Left behind | Keeps alive |
|---|---|---|
| VSImGui | `MouseWheel` and `TextInput` handlers on the game window, one controller per world | That world's client and every mod in it |
| ConfigLib | Static `ConfigRegistry.OnToBytes` lambda, both sides | That world's client or server |
| ConfigLib | Static `GuiManager._instance` | The last world's client |

Five seconds after a world closes, it looks for references back to that world (its API, its
world, or one of its mod systems):

- **Game window events:** handlers from mod code that lead back to the world are removed.
- **Static events in any mod or the game:** the same.
- **Plain static fields in mod code:** cleared when they hold one of those objects, or something
  that directly holds one.

The game's own handlers and fields are never changed. A world still held by a task that is
running, such as distant-terrain generation started before you left, is checked again every
20 s for up to 5 min. Once the world is freed, one compacting garbage collection gives the memory
back to the system. The log names the mods it released references from and the memory before and
after.

Measured with VSImGui, ConfigLib and 12 worldgen mods, after one world: the game used 1.3 GB at the
main menu, against 4.4 GB without this mod and with fewer mods.

- Install on the client. It also runs on servers but does nothing useful on a dedicated one.
- Reading a static field runs the type's static constructor if it hasn't run yet. It reads fields
  only of types that have none, the closed world's own mod systems, or mod types whose constructor
  only sets field initializers.
- No settings.

Build: `./build.sh` (needs the .NET 10 SDK and Vintage Story at `/opt/vintagestory`, or set
`VINTAGE_STORY`). Output: `dist/vsmemcleanup_<version>.zip`.
