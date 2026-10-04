# Changelog

## 1.0.0 — 2026-10-04

First release, for ENDLESS Legend 2 1.0 (Steam build 25623753).

- Growth planner: aims each population at its last collection bonus without overshooting, then spreads one of it to
  every city; player order (★, ▲/▼, skip), per-city targets, automatic order.
- Handles populations the game stops offering, refused growths, and next-population picks made by hand.
- Job optimizer: swaps populations between Citizens, Artisans and Scribes using every population's job effects from
  the game data, weighted by each city's job strategy; never changes head-counts, never moves Severed Claws.
- Optional minimum approval level (Content / Happy / Jubilant, global or per city), with food protection.
- In-game window (F7) with automatic sizing for high resolutions; options saved per game.
