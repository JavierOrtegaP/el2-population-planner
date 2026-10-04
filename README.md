# Population Planner for ENDLESS Legend 2

A [BepInEx](https://github.com/BepInEx/BepInEx) mod that takes the population micromanagement off your hands:

- **Growth** — picks every city's next population so you unlock population bonuses one after another, without
  overshooting, and then gets one of each unlocked population into every city.
- **Jobs** — swaps populations between Citizens, Artisans and Scribes so their job bonuses apply.
- **Approval** *(optional)* — keeps cities at least Content, Happy or Jubilant by growing and placing populations for
  approval when a city slips.

Everything is shown and adjustable in an in-game window (**F7**).

## What it does

### Growth: one bonus at a time

Each population type unlocks bonuses at 5, 15 and 30 populations in your empire (the mod reads the actual thresholds
from the game, faction reductions included). The 30-population bonus only applies in cities that have at least one
population of that type. So the mod:

1. grows the population you are aiming for until its last bonus unlocks, in only as many cities as still needed
   (soonest-growing first), so it stops at 30;
2. then gets **one** of that population into every city that has none, so the bonus applies everywhere;
3. and moves on to the next population in your order.

You choose the order in the window (★ = aim for this one next), or let it go for whatever is closest to its last
bonus. A city can also get its own target.

Populations the game stops offering (lost access to a minor faction, last population of a type gone, ...) are skipped
until they come back. If the game refuses to add a population to a city, the mod avoids it there for a few turns.
Populations that can't be grown with food (Called, Divined, Primordial Last Lord) are never picked.

### Jobs: the right population in the right job

Many populations have job effects, read by the mod from the game data for every population, for example:

| Population | Effect |
| --- | --- |
| Daughter of Bor | +1 Industry as Artisan |
| Green Scion | +4 Food as Citizen |
| The Consortium | +4 Dust as Scribe |
| Xavius | +4 Approval when another population type works the same job |
| Foundling / Ochling | +3 Science / Dust, -2 Approval when mixed with other types |
| Sollusk | -2 Approval when mixed with other types |
| Last Lord, Hydracorn | -3 Approval as Citizens |

The game already counts job bonuses when it places a *new* population, but not the mixing effects, and it never
revisits populations already at work. The mod swaps populations between jobs to get the most out of each city's own
job strategy (Balanced / Food / Industry / Science): how many work each job stays as the strategy set it; only who
works where changes. One swap at a time per city, at most 10 a turn, each population at most once a turn. Severed
Claws (and any population that adds job slots) are never moved.

### Minimum approval (optional)

Pick a level in the window: Content (25+), Happy (60+) or Jubilant (85+), globally or per city. When a city falls
below it (plus a small buffer, 3 by default — about what one more Citizen or Artisan costs), the city:

- grows the population that adds the most approval there (e.g. Xavius next to other types, Noquensii as Scribes),
  and avoids ones that cost approval where they would work;
- puts approval first in its jobs, and may also move populations into free Scribe slots (Scribes cost no approval,
  Citizens and Artisans -3 each), never so far that the city's food goes negative.

Above the level, no job swap may take a city back under it.

### You stay in charge

- Picking a city's next population yourself in the city screen: the mod leaves that city alone until it grows (or
  until you resume it; or never — your choice in Settings).
- Dragging populations between jobs yourself: the mod leaves that city's jobs alone until next turn.
- Any city can be switched off entirely, or just its jobs. "Reset jobs" puts a city's populations back where its job
  strategy wants them.
- **Automation: ON/OFF** at the top of the window pauses everything (the plan is still shown).

## Install

1. Install **BepInEx 5** for Windows x64 (tested with
   [5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)): extract it into the game folder (Steam:
   right-click ENDLESS Legend 2 > Manage > Browse local files — the folder with `Endless Legend 2.exe`), so that
   `winhttp.dll` sits next to the game's exe. Start the game once.
   - Linux / Steam Deck: set the game's launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
2. Download `PopulationPlanner-<version>.zip` from the [releases](../../releases) and extract it into the same
   folder. The mod ends up in `BepInEx/plugins/PopulationPlanner/`.
3. Load a game and press **F7**.

If nothing happens, check `BepInEx/LogOutput.log` for `Population Planner ... active`.

To update, extract the new zip over the old files. To uninstall, delete `BepInEx/plugins/PopulationPlanner` (and,
if you like, `BepInEx/config/javierortegap.el2.populationplanner.cfg` and `BepInEx/config/PopulationPlanner/`). The
mod only gives the game its own orders, the same as clicking in the UI, so saves don't depend on it.

## The window (F7)

- **Bonuses** — the order the mod aims for: ★ aim for it next, ▲/▼ reorder, Skip. Entries marked *(auto)* follow,
  closest to their last bonus first; *Back to automatic order* clears yours. Unlocked bonuses show how many cities
  they apply in, with a *1 per city* switch.
- **Cities** — each city's approval, current pick, what the mod picks and why; its target, its minimum approval,
  Auto/Off, Jobs auto/off, the last job change, and Reset jobs.
- **Settings** — the options below, window size and opacity.

## Options

Saved in `BepInEx/config/javierortegap.el2.populationplanner.cfg`, all also editable in the window. Your order and
per-city choices are saved per game in `BepInEx/config/PopulationPlanner/<game id>.json`.

| Option | Default | |
| --- | --- | --- |
| General / Automation | true | Pick next populations and optimize jobs. |
| General / SpreadUnlockedBonusesFirst | true | One of each unlocked bonus's population per city before anything else. |
| General / ContinueAfterOrder | true | After your order, keep going with the other populations. |
| General / ManualPicks | UntilCityGrows | What a pick made by hand in the city screen does (UntilCityGrows, UntilResumed, Ignore). |
| General / FailedGrowthCooldownTurns | 5 | Turns to avoid a population the game failed to add to a city. |
| Jobs / OptimizeJobs | true | Swap populations between jobs. |
| Jobs / MinimumGain | 0.5 | Smallest gain worth a swap (yields weighted by the city's job strategy). |
| Approval / MinimumLevel | Off | Off, Content, Happy or Jubilant. |
| Approval / Buffer | 3 | Act this many points above the level. |
| Window / ToggleKey | F7 | Unity Input System key name. F7 is unused by the game. |
| Window / Size | 0 | 0 = follows the screen resolution (2x at 4K); else a multiplier. |
| Window / Opacity | 1 | Window background opacity. |
| Debug / LogDecisions | true | Log every pick and job change to `BepInEx/LogOutput.log`. |

## Compatibility

- Made for ENDLESS Legend 2 **1.0** (Steam build 25623753). It reads the game's data at runtime, so balance changes
  are followed; if an update breaks one of its hooks, that part switches itself off and the log says so.
- Tested in single player. Multiplayer is untested.
- Works alongside [District Planner](https://github.com/AndKenneth/el2-district-planner).

## How it works

- The game state is read on the game's simulation thread, right after the game copies it for its own UI, and handed
  to the main thread as an immutable snapshot; nothing in the simulation is modified directly.
- All changes go through the game's own orders: select growing population, switch population between categories,
  optimize population assignment.
- Collection thresholds (with faction reductions), which bonuses need presence in a city, and every population's job
  effects (formulas included) are decoded from the game data, not hard-coded.

## Building

Needs the .NET SDK (7 or newer) and the game installed: the project compiles against the game's own assemblies, which
are not part of this repository.

1. Tell the build where the game is, one of: a `local.props` next to the project
   (`<Project><PropertyGroup><GameDir>D:\...\ENDLESS Legend 2</GameDir></PropertyGroup></Project>`), the
   `EL2_GAME_DIR` environment variable, or `-p:GameDir=...`. Default: Steam's standard install path.
2. BepInEx's DLLs come from the game folder if BepInEx is installed there, else from `.cache/bepinex/BepInEx/core`.
3. `dotnet build -c Release` builds, copies the DLL into the game (when BepInEx is installed; `-p:SkipDeploy=true`
   to skip) and writes `dist/PopulationPlanner-<version>.zip`.

The planner and job optimizer are plain C# and have tests that don't need the game:
`dotnet run --project tests/PlannerTests.csproj`.

## License

[MIT](LICENSE). Not affiliated with or endorsed by Amplitude Studios.
