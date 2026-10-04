# Changelog

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
