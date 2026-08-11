using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace RandomDelivery
{
    /// <summary>
    /// All mod settings, backed by a standard BepInEx <see cref="ConfigFile"/>
    /// (<c>BepInEx/config/Timofey.RandomDelivery.cfg</c>).
    ///
    /// Because every setting is a real <see cref="ConfigEntry{T}"/>, the in-game **LethalConfig** menu
    /// auto-detects and renders them (sliders / check-boxes / drop-downs) with zero extra dependency, and
    /// edits apply live — the mod always reads <c>.Value</c>. The .cfg file can also be edited by hand; it
    /// is reloaded at the start of each day (see <see cref="DeliveryScheduler"/>).
    ///
    /// Public properties expose plain values so the rest of the code stays unchanged; list settings are
    /// stored as comma-separated strings and split on read.
    /// </summary>
    public class DeliveryConfig
    {
        // ---- General ----
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<bool> _enableLogging;

        // ---- Schedule ----
        private readonly ConfigEntry<string> _deliveryTimes;
        private readonly ConfigEntry<int> _maxDeliveriesPerDay;

        // ---- Items ----
        private readonly ConfigEntry<int> _minItems;
        private readonly ConfigEntry<int> _maxItems;
        private readonly ConfigEntry<string> _itemSelectionMode;
        private readonly ConfigEntry<float> _priceWeightFactor;
        private readonly ConfigEntry<bool> _discountBoost;
        private readonly ConfigEntry<bool> _dropshipAutoOpen;

        // ---- Replacements ----
        private readonly ConfigEntry<float> _chanceForTrap;
        private readonly ConfigEntry<float> _chanceForMonster;
        private readonly ConfigEntry<string> _priority;
        private readonly ConfigEntry<float> _chanceForAllTraps;
        private readonly ConfigEntry<float> _chanceForAllMonsters;
        private readonly ConfigEntry<float> _chanceForVehicle;

        // ---- Traps ----
        private readonly ConfigEntry<string> _allowedTraps;
        private readonly ConfigEntry<string> _blockedTraps;

        // ---- Monsters ----
        private readonly ConfigEntry<string> _allowedMonsters;
        private readonly ConfigEntry<string> _blockedMonsters;

        // ---- Item filters ----
        private readonly ConfigEntry<string> _allowedItems;
        private readonly ConfigEntry<string> _blockedItems;

        public DeliveryConfig(ConfigFile cfg)
        {
            // ================= General =================
            _enabled = cfg.Bind("General", "Enabled", true,
                "Enable or disable the whole mod.");
            _enableLogging = cfg.Bind("General", "EnableLogging", true,
                "Verbose per-delivery logging to the BepInEx console.");

            // ================= Schedule =================
            _deliveryTimes = cfg.Bind("Schedule", "DeliveryTimes", "08:30",
                "Comma-separated list of times the dropship is dispatched (in-game clock, day starts 06:00). " +
                "Each entry is an 'HH:MM' time, 'StartOfDay' (right after landing), or a number of seconds " +
                "after the day starts. The dropship then descends and touches down a short while later, like " +
                "a normal order — so the default 08:30 lands it in the morning. List several for multiple " +
                "deliveries, e.g. 08:30,13:00.");
            _maxDeliveriesPerDay = cfg.Bind("Schedule", "MaxDeliveriesPerDay", 1,
                new ConfigDescription("Hard cap on deliveries per day.", new AcceptableValueRange<int>(0, 20)));

            // ================= Items =================
            _minItems = cfg.Bind("Items", "MinItems", 2,
                new ConfigDescription("Minimum items per delivery.", new AcceptableValueRange<int>(0, 10)));
            _maxItems = cfg.Bind("Items", "MaxItems", 4,
                new ConfigDescription("Maximum items per delivery.", new AcceptableValueRange<int>(0, 10)));
            _itemSelectionMode = cfg.Bind("Items", "ItemSelectionMode", "Random",
                new ConfigDescription("How items are chosen.",
                    new AcceptableValueList<string>("Random", "PriceWeighted")));
            _priceWeightFactor = cfg.Bind("Items", "PriceWeightFactor", 1.0f,
                new ConfigDescription("PriceWeighted steepness: 0 = flat, 1 = inverse price, 2 = strongly favour cheap.",
                    new AcceptableValueRange<float>(0f, 5f)));
            _discountBoost = cfg.Bind("Items", "DiscountBoost", true,
                "In PriceWeighted mode, boost the chance of items that are on sale.");
            _dropshipAutoOpen = cfg.Bind("Items", "DropshipAutoOpen", false,
                "If true, the dropship hatch opens by itself when it lands (items drop automatically). " +
                "If false (default), it stays closed like a normal order and a player must walk up and " +
                "open it. Traps/monsters that ride the delivery appear once the hatch is opened.");

            // ================= Replacements =================
            _chanceForTrap = cfg.Bind("Replacements", "ChanceForTrap", 15f,
                new ConfigDescription("Per-slot % chance to replace the item with a trap.",
                    new AcceptableValueRange<float>(0f, 100f)));
            _chanceForMonster = cfg.Bind("Replacements", "ChanceForMonster", 10f,
                new ConfigDescription("Per-slot % chance to replace the item with a monster.",
                    new AcceptableValueRange<float>(0f, 100f)));
            _priority = cfg.Bind("Replacements", "Priority", "Monster",
                new ConfigDescription("Winner when both a trap and a monster roll hit the same slot.",
                    new AcceptableValueList<string>("Monster", "Trap")));
            _chanceForAllTraps = cfg.Bind("Replacements", "ChanceForAllTraps", 0f,
                new ConfigDescription("Chance (0-100) that the WHOLE delivery is nothing but traps (every " +
                    "slot). Rolled once per delivery, before the per-slot chances.",
                    new AcceptableValueRange<float>(0f, 100f)));
            _chanceForAllMonsters = cfg.Bind("Replacements", "ChanceForAllMonsters", 0f,
                new ConfigDescription("Chance (0-100) that the WHOLE delivery is nothing but monsters (every " +
                    "slot). Rolled once per delivery. If both all-traps and all-monsters hit, Priority decides.",
                    new AcceptableValueRange<float>(0f, 100f)));
            _chanceForVehicle = cfg.Bind("Replacements", "ChanceForVehicle", 0f,
                new ConfigDescription("Chance (0-100) that a delivery brings a free Cruiser (vehicle) instead " +
                    "of the item batch. Rolled first, before everything else. Because the dropship can only " +
                    "carry a vehicle OR items, once a Cruiser is delivered the rest of that day's deliveries " +
                    "are cancelled.",
                    new AcceptableValueRange<float>(0f, 100f)));

            // ================= Traps =================
            _allowedTraps = cfg.Bind("Traps", "AllowedTraps", "Turret, Landmine",
                "Comma-separated whitelist of traps. Non-empty = only these; empty = allow all except BlockedTraps.");
            _blockedTraps = cfg.Bind("Traps", "BlockedTraps", "",
                "Comma-separated blacklist of traps (used only when AllowedTraps is empty).");

            // ================= Monsters =================
            _allowedMonsters = cfg.Bind("Monsters", "AllowedMonsters",
                "Manticoil, RoamingLocust, RedLocust, HoardingBug, GunkFish, Slime, TulipSnake, Maneater",
                "Comma-separated whitelist of small monsters. Only these may be delivered (and only if the " +
                "current moon has them).");
            _blockedMonsters = cfg.Bind("Monsters", "BlockedMonsters", "",
                "Comma-separated blacklist of monsters, removed on top of the whitelist.");

            // ================= Item filters =================
            _allowedItems = cfg.Bind("ItemFilters", "AllowedItems", "",
                "Comma-separated whitelist of shop items. Non-empty = only these; empty = allow all except BlockedItems.");
            _blockedItems = cfg.Bind("ItemFilters", "BlockedItems", "Clipboard, ToyCube",
                "Comma-separated blacklist of shop items (used only when AllowedItems is empty).");
        }

        // ================================================================ value accessors

        public bool Enabled => _enabled.Value;
        public bool EnableLogging => _enableLogging.Value;

        public int MaxDeliveriesPerDay => Math.Max(0, _maxDeliveriesPerDay.Value);
        /// <summary>Trigger tokens (strings) parsed from the comma-separated DeliveryTimes setting.</summary>
        public List<object> DeliveryTimes => SplitCsv(_deliveryTimes.Value).Cast<object>().ToList();

        public int MinItems => Math.Max(0, _minItems.Value);
        public int MaxItems => Math.Max(MinItems, _maxItems.Value); // never less than MinItems
        public float PriceWeightFactor => Math.Max(0f, _priceWeightFactor.Value);
        public bool DiscountBoost => _discountBoost.Value;
        public bool DropshipAutoOpen => _dropshipAutoOpen.Value;

        public float ChanceForTrap => Clamp01to100(_chanceForTrap.Value);
        public float ChanceForMonster => Clamp01to100(_chanceForMonster.Value);
        public float ChanceForAllTraps => Clamp01to100(_chanceForAllTraps.Value);
        public float ChanceForAllMonsters => Clamp01to100(_chanceForAllMonsters.Value);
        public float ChanceForVehicle => Clamp01to100(_chanceForVehicle.Value);

        public List<string> AllowedTraps => SplitCsv(_allowedTraps.Value);
        public List<string> BlockedTraps => SplitCsv(_blockedTraps.Value);

        public List<string> AllowedMonsters => SplitCsv(_allowedMonsters.Value);
        public List<string> BlockedMonsters => SplitCsv(_blockedMonsters.Value);

        public List<string> AllowedItems => SplitCsv(_allowedItems.Value);
        public List<string> BlockedItems => SplitCsv(_blockedItems.Value);

        public bool MonsterHasPriority =>
            string.Equals(_priority.Value?.Trim(), "Monster", StringComparison.OrdinalIgnoreCase);
        public bool IsPriceWeighted =>
            string.Equals(_itemSelectionMode.Value?.Trim(), "PriceWeighted", StringComparison.OrdinalIgnoreCase);

        // ================================================================ helpers

        private static float Clamp01to100(float v) => v < 0f ? 0f : (v > 100f ? 100f : v);

        private static List<string> SplitCsv(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (var part in raw.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }
    }
}
