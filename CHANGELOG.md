# Changelog

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
