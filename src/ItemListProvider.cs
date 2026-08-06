using System;
using System.Collections.Generic;
using UnityEngine;

namespace RandomDelivery
{
    /// <summary>One shop item that is eligible for delivery, with its pre-computed selection weight.</summary>
    internal class DeliverableItem
    {
        public Item Item;
        public int Index;       // index into Terminal.buyableItemsList (for the sales-percentage lookup)
        public double Weight;   // relative selection weight (> 0)
        public string Name => Item != null ? Item.itemName : "<null>";
    }

    /// <summary>
    /// Builds the pool of deliverable items from the terminal's shop list (which already includes any
    /// items other mods added), applies the allow/block filters, and assigns selection weights.
    /// </summary>
    internal static class ItemListProvider
    {
        private static Terminal _terminal;

        private static Terminal FindTerminal()
        {
            if (_terminal == null)
                _terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            return _terminal;
        }

        /// <summary>The shop terminal (cached), or null if not in a game. Used to queue dropship orders.</summary>
        internal static Terminal GetTerminal() => FindTerminal();

        internal static void Reset() => _terminal = null;

        /// <summary>
        /// Assembles the eligible-item pool for one delivery. Returns an empty list (never null) if the
        /// shop is unavailable or every item was filtered out.
        /// </summary>
        internal static List<DeliverableItem> BuildPool()
        {
            var cfg = Plugin.Cfg;
            var pool = new List<DeliverableItem>();

            var terminal = FindTerminal();
            if (terminal == null || terminal.buyableItemsList == null)
            {
                Plugin.Log.LogWarning("[Delivery] No Terminal / buyableItemsList found — cannot build item pool.");
                return pool;
            }

            var allow = Names.NormalizedSet(cfg.AllowedItems);
            var block = Names.NormalizedSet(cfg.BlockedItems);
            int[] sales = terminal.itemSalesPercentages; // parallel to buyableItemsList; 100 = full price

            var items = terminal.buyableItemsList;
            for (int i = 0; i < items.Length; i++)
            {
                Item item = items[i];
                if (item == null || item.spawnPrefab == null) continue;

                // Only physical, grabbable tools are deliverable. Ship upgrades are UnlockableItems and
                // never appear here anyway, but this also rejects anything without a GrabbableObject.
                if (item.spawnPrefab.GetComponent<GrabbableObject>() == null) continue;

                var candidates = new[] { item.itemName, item.spawnPrefab.name };

                // Allow-list wins when non-empty; otherwise a block-list is applied.
                if (allow.Count > 0)
                {
                    if (!Names.NameMatchesSet(candidates, allow)) continue;
                }
                else if (Names.NameMatchesSet(candidates, block))
                {
                    continue;
                }

                pool.Add(new DeliverableItem
                {
                    Item = item,
                    Index = i,
                    Weight = ComputeWeight(item, i, sales, cfg)
                });
            }

            return pool;
        }

        /// <summary>
        /// Selection weight for one item.
        ///
        /// Random mode:        every item gets weight 1 (equal chance).
        /// PriceWeighted mode: weight = 1 / price^PriceWeightFactor, so the cheaper an item is the more
        ///                     likely it is to be picked. PriceWeightFactor tunes the steepness
        ///                     (1.0 = plain inverse price; 0 = flat; 2.0 = strongly favours cheap items).
        ///                     If DiscountBoost is on and the item is on sale, its weight is additionally
        ///                     multiplied by (1 + discount/100) — e.g. a 20%-off item gets a x1.2 boost.
        /// </summary>
        private static double ComputeWeight(Item item, int index, int[] sales, DeliveryConfig cfg)
        {
            if (!cfg.IsPriceWeighted)
                return 1.0;

            double price = Math.Max(1, item.creditsWorth);
            double weight = 1.0 / Math.Pow(price, cfg.PriceWeightFactor);

            if (cfg.DiscountBoost && sales != null && index < sales.Length)
            {
                int discount = 100 - sales[index]; // 100 = full price -> 0% discount
                if (discount > 0)
                    weight *= 1.0 + discount / 100.0;
            }

            return weight <= 0 ? double.Epsilon : weight;
        }

        /// <summary>Weighted random pick (with replacement) from a pre-built pool.</summary>
        internal static DeliverableItem PickWeighted(List<DeliverableItem> pool, System.Random rng)
        {
            if (pool == null || pool.Count == 0) return null;

            double total = 0;
            foreach (var d in pool) total += d.Weight;
            if (total <= 0) return pool[rng.Next(pool.Count)];

            double roll = rng.NextDouble() * total;
            foreach (var d in pool)
            {
                roll -= d.Weight;
                if (roll < 0) return d;
            }
            return pool[pool.Count - 1];
        }
    }
}
