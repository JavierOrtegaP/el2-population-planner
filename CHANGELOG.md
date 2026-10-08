# Changelog

## 1.4.1 — 2026-10-08

- No more cap of three on placing a city's populations again for new slots in one turn: every construction that adds
  slots counts, however many you buy. What it guarded against (slots going back and forth when the game moves
  populations that add slots to their job) is now caught directly: a slot count the city already had that turn
  doesn't count again.

## 1.4.0 — 2026-10-07

- New job slots are used right away. The game only fills new slots (a construction finishing, also when you buy it
  out) with new and Destitute populations; those already at work stay where they are, so for example an Industrial
  city's three new Artisan slots stayed empty until it grew, unless you switched its job strategy back and forth.
  Now, when a city gets new slots, the mod has the game place its populations again by its job strategy (the game's
  own order, as when you pick a strategy), then optimizes them. At most three times a city per turn, never while your
  own job moves there hold, and not in a city whose jobs you turned off. Off with Jobs / PlaceAgainOnNewSlots, also
  in Settings.

## 1.3.3 — 2026-10-07

- Fixed: a city growing for approval judged each population by the job where its approval would be highest, but the
  game places a new population by the city's job strategy. For example, a new Agrarian city grew Noquensii "for +3
  approval", which it only gives as a Scribe, while the game puts it among the Citizens (-3 there). It now counts
  the job the game will place it in, or the one the mod then moves it to for its own bonus, so that city grows a
  Xavius next to its Citizen (+1) instead.
- The log names the strategy a city's job strategy was changed to.

## 1.3.2 — 2026-10-07

- When the game resets a city's job strategy to Balanced on its own (it does that to every city whenever your empire
  gains or loses a special ability, a game bug), the mod now sets back the strategy you picked, with the game's own
  order. As when you pick one, the game then places the city's populations again, and the mod optimizes them. Not in
  a city you turned off (or its jobs), and not while your own job moves there hold (it waits for the next turn). Off
  with Jobs / RestoreStrategyAfterGameReset, also in Settings.

## 1.3.1 — 2026-10-07

- Fixed: with *only when it pays*, a city already Happy or Jubilant but still inside the buffer (e.g. at 86, with
  Jubilant at 85 and a buffer of 3) weighed that level against itself and found it worth nothing ("for no change
  from Jubilant"). It then held its jobs only at the level below, so later job swaps could cost it the level. It now
  weighs what's at stake: the level over the one below.
- The game resets every city's job strategy to Balanced whenever your empire gains or loses a special ability (a
  bug in the game: the check meant to reset only strategies that are no longer allowed resets all of them), and it
  doesn't place anyone again. The mod now says so in the log and in the Cities tab, so you can pick the strategy
  again. It no longer mistakes this for a strategy change of yours.
- The log no longer repeats the same approval-level and job notes every turn: each is written when it changes. A
  pick made for approval now says about how much approval it adds.
- No settings file is written for a game where nothing was chosen (e.g. the main menu's background game).

## 1.3.0 — 2026-10-07

- When a construction finishes during your turn (e.g. one you buy out) and changes a city's job slots or yields per
  population, the mod sorts out that city's jobs again right away, populations it already moved that turn included.
  The log says what changed.
- New slots are now used: a population takes a free slot in the job where its own bonus applies (e.g. a Green Scion
  as a Citizen, a Daughter of Bor as an Artisan), or leaves a job where its own malus applies (e.g. a Last Lord out
  of the Citizens), when that gains at least the minimum, weighed by the city's job strategy, within the approval and
  food limits. Until now the mod only swapped populations, so after Communal Habitations added a slot to each job,
  Green Scions outside the Citizens stayed there until the city grew. Otherwise, how many work each job still stays
  as the strategy set it. The Cities tab's "nobody else works in that job to trade places with" note is gone: those
  populations now take the free slot themselves.
- Made for the game update of 2026-10-07 (Steam build 25725410).

## 1.2.3 — 2026-10-04

- No changes to the mod itself. The download now comes with the current README, and Population Planner is also on
  [Nexus Mods](https://www.nexusmods.com/endlesslegend2/mods/9), updated with every release.

## 1.2.2 — 2026-10-04

- Fixed: a population the game refused to add to a settlement that isn't a city (which the mod doesn't manage) was
  logged as a warning, with the settlement's number instead of a name. Only cities count now, and a refusal the game
  repeats within a turn is logged once.

## 1.2.1 — 2026-10-04

- *Only when it pays* weighs job moves only, as intended: below the minimum you picked, cities again grow the
  population that adds the most approval (it costs no yields). In 1.2.0, a city whose next level was out of reach
  with job moves also stopped growing for approval.

## 1.2.0 — 2026-10-04

- Minimum approval, *only when it pays* (on by default): Content is always kept, but Happy (+15% Food and Industry)
  and Jubilant (+30%) are now only chased when that bonus is worth more than the job moves it takes to get there,
  both weighed by the city's job strategy. For example, moving four Daughters of Bor out of the Artisans for +12
  approval can cost more Industry than Happy gives back. The Cities tab shows the numbers when a city is held lower.
  The level bonuses are read from the game data.
- When a population misses its job bonus because nobody else works in that job to trade places with (e.g. Artisans
  emptied by earlier approval moves), the Cities tab now says so, and that Reset jobs lets the game place everyone
  again.

## 1.1.3 — 2026-10-04

- When no job swap can put a population in the job that gives it a bonus of its own (e.g. a Daughter of Bor
  outside the Artisans), the Cities tab now says why: that job is full and the game would send out the same type to
  make room, it was already moved this turn, or no swap gains enough. The log says it once a turn, with who works
  where.
- The Populations tab leaves out populations the game describes no bonus for at all (Mangrove of Harmony's Elder
  variant).

## 1.1.2 — 2026-10-04

- Fixed: a job swap worth exactly the minimum gain (0.5 by default) was never made, although the option says "at
  least". For example a Daughter of Bor's +1 Industry as an Artisan at a Food or Science focus, where Industry
  counts half.
- Changing a city's job strategy makes the game place all its populations again. The mod now optimizes that city
  again right away for the new strategy, instead of leaving the populations it had already moved that turn wherever
  the game put them until the next turn.

## 1.1.1 — 2026-10-04

- Job changes are no longer capped at 10 per city per turn, so a big city is sorted out in the same turn (for
  example after changing its job strategy). Each population still moves at most once a turn. A cap can be set with
  Jobs / MaxChangesPerCityPerTurn, also in the window's Settings; a city that reaches it says so in the Cities tab.

## 1.1.0 — 2026-10-04

- Populations tab: every population in the game with what each of its collection bonuses gives (the game's own text,
  in the game's language) and its thresholds, including populations the empire has none of, which the game's screens
  don't show.
- Optional [Mod Menu](https://github.com/JavierOrtegaP/el2-mod-menu) support: with it installed, the window is the
  menu's Population page with a status line under All mods, and F7 is left to the menu. Without it, nothing changes.
- Fixed: the first-game hint and the window title could turn dark when the mouse was over them.

## 1.0.0 — 2026-10-04

First release, for ENDLESS Legend 2 1.0 (Steam build 25623753).

- Growth planner: aims each population at its last collection bonus without overshooting, then spreads one of it to
  every city; player order (★, ▲/▼, skip), per-city targets, automatic order.
- Handles populations the game stops offering, refused growths, and next-population picks made by hand.
- Job optimizer: swaps populations between Citizens, Artisans and Scribes using every population's job effects from
  the game data, weighted by each city's job strategy; never changes head-counts, never moves Severed Claws.
- Optional minimum approval level (Content / Happy / Jubilant, global or per city), with food protection.
- In-game window (F7) with automatic sizing for high resolutions; options saved per game.
