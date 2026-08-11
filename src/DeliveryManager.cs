using System;
using System.Collections.Generic;
using UnityEngine;

namespace RandomDelivery
{
    /// <summary>
    /// The core, HOST-ONLY orchestrator for a single delivery:
    ///   1. build the eligible-item pool (and the trap/monster pools),
    ///   2. abort with a log message if there is nothing to deliver,
    ///   3. roll a count in [MinItems, MaxItems] and, per slot, decide item / trap / monster,
    ///   4. dispatch the whole package through the real item dropship: the items ride inside it, and any
    ///      traps/monsters are dropped at the ship's own item-drop spots when its hatch opens — so
    ///      everything arrives together, in the same place, with vanilla timing.
    ///
    /// If there are no items to carry (or no dropship / the dropship is disabled), everything is spawned
    /// directly on the pad next to the ship instead. Generation runs only on the host; the spawned
    /// entities and the dropship replicate to clients automatically.
    /// </summary>
    internal static class DeliveryManager
    {
        private static readonly System.Random Rng = new System.Random();

        private enum Cat { Item, Trap, Monster }

        /// <summary>
        /// True once a Cruiser has been delivered this day — the dropship is then committed to the vehicle,
        /// so the scheduler cancels any remaining deliveries until the next day.
        /// </summary>
        internal static bool StopDeliveriesForDay { get; private set; }

        /// <summary>Called at the start of every new day to drop any per-day caches / flags.</summary>
        internal static void OnNewDay()
        {
            ItemListProvider.Reset();
            StopDeliveriesForDay = false;
        }

        /// <summary>True when we are the host/server (also true in singleplayer).</summary>
        internal static bool IsHost
        {
            get
            {
                var sor = StartOfRound.Instance;
                return sor != null && sor.IsServer;
            }
        }

        /// <summary>
        /// Runs one delivery. <paramref name="reason"/> is a short label for the log (e.g. "StartOfDay").
        /// Returns true if a delivery actually happened.
        /// </summary>
        internal static bool RunDelivery(string reason)
        {
            var cfg = Plugin.Cfg;
            if (cfg == null || !cfg.Enabled) return false;
            if (!IsHost) return false; // safety net; the scheduler already gates on this

            try
            {
                // Rolled first: a delivery can bring a free Cruiser instead of the item batch. Because the
                // dropship can only carry a vehicle OR items, a Cruiser cancels the rest of the day.
                if (Rng.NextDouble() * 100.0 < cfg.ChanceForVehicle && SpawnHelper.DeliverVehicle())
                {
                    StopDeliveriesForDay = true;
                    Plugin.Log.LogInfo($"[Delivery] ({reason}) delivered a Cruiser — remaining deliveries today cancelled.");
                    return true;
                }

                var itemPool = ItemListProvider.BuildPool();
                if (itemPool.Count == 0)
                {
                    Plugin.Log.LogWarning(
                        $"[Delivery] ({reason}) cancelled: the shop item pool is empty " +
                        "(no buyable items, or everything was filtered out by Allowed/BlockedItems).");
                    return false;
                }

                var trapPool = TrapMonsterProvider.BuildTrapPool();
                var monsterPool = TrapMonsterProvider.BuildMonsterPool();

                int count = Rng.Next(cfg.MinItems, cfg.MaxItems + 1);
                if (count <= 0)
                {
                    Plugin.Log.LogInfo($"[Delivery] ({reason}) rolled 0 slots — nothing delivered.");
                    return false;
                }

                // Delivery-wide mode: a single roll can make the WHOLE delivery all traps or all monsters.
                Cat? forcedCat = RollDeliveryMode(cfg, trapPool, monsterPool);
                if (forcedCat != null && cfg.EnableLogging)
                    Plugin.Log.LogInfo($"[Delivery] ({reason}) whole-delivery mode: all {forcedCat}s.");

                // Decide every slot up front; nothing is spawned yet.
                var itemPicks = new List<DeliverableItem>();
                var trapJobs = new List<TrapPrefab>();
                var monsterJobs = new List<EnemyType>();
                var details = new List<string>(count);

                for (int slot = 0; slot < count; slot++)
                {
                    Cat cat = forcedCat ?? Roll(cfg);
                    string outcome = null;

                    if (cat == Cat.Monster && monsterPool.Count > 0)
                    {
                        var m = monsterPool[Rng.Next(monsterPool.Count)];
                        monsterJobs.Add(m);
                        outcome = $"Monster:{m.enemyName}";
                    }
                    else if (cat == Cat.Trap && trapPool.Count > 0)
                    {
                        var tr = trapPool[Rng.Next(trapPool.Count)];
                        trapJobs.Add(tr);
                        outcome = $"Trap:{tr.Name}";
                    }

                    // Item slot, or a replacement category that had nothing available -> normal item.
                    if (outcome == null)
                    {
                        var pick = ItemListProvider.PickWeighted(itemPool, Rng);
                        if (pick != null) { itemPicks.Add(pick); outcome = $"Item:{pick.Name}"; }
                    }

                    details.Add(outcome ?? "(failed)");
                }

                // --- dispatch ---
                // Always deliver via the real dropship; it carries the items and drops the traps/monsters at
                // its own spots when it opens. If the moon has no dropship, fall back to a direct pad spawn.
                bool viaDropship = false;
                bool hasContent = itemPicks.Count > 0 || trapJobs.Count > 0 || monsterJobs.Count > 0;
                if (hasContent)
                {
                    var indices = new List<int>(itemPicks.Count);
                    foreach (var p in itemPicks) indices.Add(p.Index);
                    viaDropship = SpawnHelper.QueueDropshipDelivery(indices, trapJobs, monsterJobs);
                }

                string mode;
                if (viaDropship)
                {
                    mode = "dropship";
                }
                else
                {
                    // No dropship (or disabled, or no items to carry): place everything on the pad now.
                    int total = itemPicks.Count + trapJobs.Count + monsterJobs.Count;
                    var anchors = SpawnHelper.GetAnchorPositions(Math.Max(1, total));
                    int a = 0;
                    foreach (var p in itemPicks) SpawnHelper.SpawnItem(p.Item, anchors[a++ % anchors.Count]);
                    foreach (var tr in trapJobs) SpawnHelper.SpawnTrap(tr, anchors[a++ % anchors.Count]);
                    foreach (var m in monsterJobs) SpawnHelper.SpawnMonster(m, anchors[a++ % anchors.Count]);
                    mode = "direct";
                }

                Plugin.Log.LogInfo(
                    $"[Delivery] ({reason}) {count} slot(s) via {mode}: {itemPicks.Count} item(s), " +
                    $"{trapJobs.Count} trap(s), {monsterJobs.Count} monster(s).");
                if (cfg.EnableLogging)
                    Plugin.Log.LogInfo("[Delivery]   -> " + string.Join(" | ", details));

                return true;
            }
            catch (Exception e)
            {
                // A failed delivery must never crash the game.
                Plugin.Log.LogError($"[Delivery] ({reason}) failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Rolls the once-per-delivery "whole delivery is all traps / all monsters" chances. Returns the
        /// forced category, or null for normal per-slot behaviour. A category whose pool is empty is
        /// ignored (so we never force a mode nothing can fill). If both hit, Priority decides.
        /// </summary>
        private static Cat? RollDeliveryMode(DeliveryConfig cfg, List<TrapPrefab> trapPool, List<EnemyType> monsterPool)
        {
            bool allTraps = trapPool.Count > 0 && Rng.NextDouble() * 100.0 < cfg.ChanceForAllTraps;
            bool allMonsters = monsterPool.Count > 0 && Rng.NextDouble() * 100.0 < cfg.ChanceForAllMonsters;

            if (allTraps && allMonsters) return cfg.MonsterHasPriority ? Cat.Monster : Cat.Trap;
            if (allMonsters) return Cat.Monster;
            if (allTraps) return Cat.Trap;
            return null;
        }

        /// <summary>
        /// Per-slot category roll. Trap and monster are rolled independently; if both hit, Priority decides.
        /// If neither hits, the slot is a normal item.
        /// </summary>
        private static Cat Roll(DeliveryConfig cfg)
        {
            bool wantTrap = Rng.NextDouble() * 100.0 < cfg.ChanceForTrap;
            bool wantMonster = Rng.NextDouble() * 100.0 < cfg.ChanceForMonster;

            if (wantTrap && wantMonster) return cfg.MonsterHasPriority ? Cat.Monster : Cat.Trap;
            if (wantMonster) return Cat.Monster;
            if (wantTrap) return Cat.Trap;
            return Cat.Item;
        }
    }
}
