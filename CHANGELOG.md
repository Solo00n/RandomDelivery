# Changelog

## 1.4.5
- Removed the mine auto-detonation feature entirely (`MinesAutoDetonate` / `MineWarningTime` config and all
  related code, including the grabbable-landmine compat). Landmines are now delivered as plain live traps
  that detonate when something steps on them, like any facility mine.

## 1.4.4
- Fixed the real reason delivered mines never detonated: the arm/detonate coroutine wrote the private
  `Landmine.mineActivated` field, which throws a runtime `FieldAccessException` (the publicized game-libs
  only expose it at compile time) and silently killed the whole coroutine. The field already defaults to
  true, so the write was removed. Mines now beep and detonate right after the dropship opens.

## 1.4.3
- Mine fix: the Landmine component is now found with `includeInactive` — previously it was missed when it
  sat on an inactive child, so the arm/detonate coroutine never started and mines just sat there.
- New `ChanceForAllTraps` / `ChanceForAllMonsters` (in `[Replacements]`): a once-per-delivery roll that
  makes the entire delivery all traps or all monsters. If both hit, `Priority` decides.

## 1.4.2
- Reverted 1.4.1's auto-open (the dropship stays closed unless `DropshipAutoOpen`, as intended).
- Mine fix: the grabbable-landmine conversion is now suppressed for a generous window around our mine's
  spawn (was too short before), so our mine stays a normal landmine and detonates right after the ship is
  opened — not only when the ship departs (which was the converted grabbable mine going off on collision
  exit). Added detection/spawn/arm diagnostics to the log.

## 1.4.1
- Deliveries that carry traps/monsters now auto-open the dropship on arrival so those "burst out" and
  their mine countdowns start right when it opens and drops the items — instead of waiting for a player
  to open the (possibly empty-looking) ship, which previously left mines going off only as it departed.
  Item-only deliveries still respect `DropshipAutoOpen`. Added land/open/deploy stage logging.

## 1.4.0
- Simplified config: removed `DeliverItemsViaDropship` (the dropship is now always used, with an
  automatic direct-spawn fallback only if a moon has no dropship), `DeliveryInterval` (list times in
  `DeliveryTimes` instead), and `DropshipArrivalTime` (the dropship is dispatched at the `DeliveryTimes`
  time). `DeliveryTimes` now defaults to `08:30`, so the dropship lands in the morning by default.
- Mines now detonate even under a BrutalCompany "grabbable landmines" event: the conversion flag is
  turned off just around our own mine's spawn, so only our mine stays a normal, detonating landmine.
- Fixed the Backwater Gunkfish alias (its internal name is "Stingray"). Added a one-time log of every
  loaded enemy name to make monster names easy to verify.

## 1.3.3
- Monster pool is now built from all loaded monster assets (filtered by the allow/block lists) instead
  of only the current moon's enemy list, so every allowed monster can be delivered on any moon and the
  random pick actually varies. Added monster-pool and per-spawn logging.

## 1.3.2
- Dropship descent is now gated to a configurable in-game clock time (`DropshipArrivalTime`, default
  08:30 → lands ~08:45) using the game's own clock, so it no longer arrives at day-start.
- Mines are skipped when a BrutalCompany-style "grabbable landmines" event is active (that event
  converts them into pick-ups so they never auto-detonate); added mine arm/detonate logging.

## 1.3.1
- Force the dropship to arrive even for pure trap/monster deliveries; fixed direct-fallback navmesh
  positioning.

## 1.3.0
- Dropship now uses **vanilla timing** — it no longer arrives instantly; it starts its landing run and
  touches down on the normal schedule (~8:30 / ~8:45), like a real order placed in orbit / on landing.
- **Traps and monsters now drop at the dropship's own item-drop spots when the hatch opens**, so they
  arrive together with the items in the same place (instead of spawning separately around the ship).
- Dropship stays **closed by default** like the original; `DropshipAutoOpen` (default false) makes it
  open by itself. Traps/monsters appear once the hatch is opened (by a player or by auto-open).
- Fixed the trap pool being empty on moons that don't list turrets/mines (now scanned across all moons).

## 1.2.0
- Items are now delivered by the game's **real item dropship**: it flies in, lands next to the ship
  and drops the items like a normal terminal order (still free). Implemented by queueing the chosen
  items onto `Terminal.orderedItemsFromTerminal`, which bypasses the credit charge.
- The dropship auto-opens on landing so items drop without a player interacting (`DropshipAutoOpen`).
- New options: `DeliverItemsViaDropship` (set false to make items appear directly on the pad instead)
  and `DropshipAutoOpen`.
- Traps and monsters still spawn directly on the pad, now snapped to the walkable navmesh (fixes items
  landing on invisible AI line-of-sight colliders / floating off the pad).

## 1.1.0
- Config moved from a JSON file to a standard BepInEx `.cfg`
  (`BepInEx/config/Timofey.RandomDelivery.cfg`), so it is now editable **in-game via LethalConfig**
  (auto-detected — no dependency required) with sliders, check-boxes and drop-downs, applied live.
- Removed the Newtonsoft.Json dependency.
- `.cfg` is re-read at the start of each day for external hand edits.

## 1.0.0
- Initial release for Lethal Company **v81**.
- Daily (or repeated) deliveries of 2-4 random buyable shop items onto the ship's landing pad.
- Auto-picks up items added by other mods (reads `Terminal.buyableItemsList`); excludes ship upgrades.
- `Random` and `PriceWeighted` item selection modes, with optional discount boost.
- Per-slot chance to replace an item with a **Turret/Landmine** trap or a small **monster**
  (Manticoil, Roaming/Red Locust, Hoarding Bug, Gunkfish, Slime, Tulip Snake, Maneater).
- Allow/block lists for items, traps and monsters.
- Flexible JSON config (`BepInEx/config/RandomDelivery.json`), re-read at the start of every day.
- Host-authoritative: only the host generates; results replicate to clients via Unity Netcode.
- Verbose BepInEx logging of every delivery.
