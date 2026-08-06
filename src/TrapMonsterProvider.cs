using System;
using System.Collections.Generic;
using UnityEngine;

namespace RandomDelivery
{
    /// <summary>A resolved trap prefab (a spawnable map hazard) ready to be instantiated.</summary>
    internal class TrapPrefab
    {
        public string Name;        // "Turret" or "Landmine"
        public GameObject Prefab;  // the SpawnableMapObject.prefabToSpawn
    }

    /// <summary>
    /// Supplies the concrete traps and small monsters that a delivery slot can be replaced with.
    ///
    /// Both pools are sourced from the CURRENT MOON so the spawns are guaranteed to be registered as
    /// network prefabs for this session (the same reasoning as LethalPresents / MonstersGordion):
    ///   * traps come from <c>currentLevel.spawnableMapObjects[].prefabToSpawn</c>,
    ///   * monsters come from the moon's own enemy lists.
    /// The configured allow/block lists are then applied on top.
    /// </summary>
    internal static class TrapMonsterProvider
    {
        // ============================================================ TRAPS

        internal static List<TrapPrefab> BuildTrapPool()
        {
            var cfg = Plugin.Cfg;
            var result = new List<TrapPrefab>();

            var allow = Names.NormalizedSet(cfg.AllowedTraps);
            var block = Names.NormalizedSet(cfg.BlockedTraps);

            GameObject turret = null, mine = null;
            ScanTrapPrefabs(ref turret, ref mine);

            // (A BrutalCompany "grabbable landmines" event would normally convert our mine into a pick-up;
            //  SpawnHelper suppresses that just around our mine spawn instead of dropping mines here.)
            TryAdd(result, "Turret", turret, allow, block);
            TryAdd(result, "Landmine", mine, allow, block);

            if (cfg.EnableLogging)
                Plugin.Log.LogInfo($"[Delivery] trap pool: turretPrefab={(turret != null)} " +
                                   $"minePrefab={(mine != null)} -> {result.Count} allowed after filters");
            return result;
        }

        /// <summary>
        /// Finds the turret / landmine prefabs by scanning spawnable map objects. The CURRENT moon is
        /// scanned first, then EVERY moon as a fallback — many moons don't list turrets/mines in their own
        /// hazard set, but those prefabs are globally network-registered so spawning them here still works
        /// (same approach as the LethalPresents mod). Identified by COMPONENT, robust against renames.
        /// </summary>
        private static void ScanTrapPrefabs(ref GameObject turret, ref GameObject mine)
        {
            var sor = StartOfRound.Instance;
            if (sor == null) return;

            var sources = new List<SpawnableMapObject[]>();
            if (sor.currentLevel != null && sor.currentLevel.spawnableMapObjects != null)
                sources.Add(sor.currentLevel.spawnableMapObjects);
            if (sor.levels != null)
                foreach (var lvl in sor.levels)
                    if (lvl != null && lvl.spawnableMapObjects != null)
                        sources.Add(lvl.spawnableMapObjects);

            foreach (var arr in sources)
            {
                foreach (var smo in arr)
                {
                    var prefab = smo != null ? smo.prefabToSpawn : null;
                    if (prefab == null) continue;
                    if (turret == null && prefab.GetComponentInChildren<Turret>() != null) turret = prefab;
                    if (mine == null && prefab.GetComponentInChildren<Landmine>() != null) mine = prefab;
                }
                if (turret != null && mine != null) break;
            }
        }

        private static void TryAdd(List<TrapPrefab> list, string name, GameObject prefab,
                                   HashSet<string> allow, HashSet<string> block)
        {
            if (prefab == null) return;
            var candidates = new[] { name };

            if (allow.Count > 0)
            {
                if (!Names.NameMatchesSet(candidates, allow)) return;
            }
            else if (Names.NameMatchesSet(candidates, block))
            {
                return;
            }

            list.Add(new TrapPrefab { Name = name, Prefab = prefab });
        }

        // ============================================================ MONSTERS

        private static bool _dumpedAllEnemies;

        internal static List<EnemyType> BuildMonsterPool()
        {
            var cfg = Plugin.Cfg;
            var result = new List<EnemyType>();

            var allow = Names.NormalizedSet(cfg.AllowedMonsters);
            var block = Names.NormalizedSet(cfg.BlockedMonsters);
            if (allow.Count == 0)
            {
                if (cfg.EnableLogging) Plugin.Log.LogInfo("[Delivery] monster pool: AllowedMonsters is empty — no monsters.");
                return result;
            }

            // Source from EVERY loaded EnemyType, not just the current moon's lists. RoundManager's
            // SpawnEnemyGameObject(pos, yRot, -1, enemyType) instantiates the given prefab directly
            // regardless of the moon (verified in v81), so any allowed monster can be delivered on any
            // moon. Building from the moon lists only was why a moon that happened to list just the
            // Hoarding Bug (among the allowed set) always delivered that one.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allNames = new List<string>();
            foreach (var et in Resources.FindObjectsOfTypeAll<EnemyType>())
            {
                if (et == null || et.enemyPrefab == null || string.IsNullOrWhiteSpace(et.enemyName)) continue;
                if (!seen.Add(et.enemyName)) continue; // FindObjectsOfTypeAll can return duplicates
                allNames.Add(et.enemyName);

                var candidates = new[] { et.enemyName, et.enemyPrefab.name, et.name };
                if (!Names.MonsterMatchesSet(candidates, allow)) continue; // whitelist decides eligibility
                if (Names.MonsterMatchesSet(candidates, block)) continue;  // blacklist removes on top

                result.Add(et);
            }

            // One-time dump of every enemy name in the build, so a monster that won't appear can be traced
            // to its exact internal name (e.g. the Backwater Gunkfish is "Stingray").
            if (cfg.EnableLogging && !_dumpedAllEnemies)
            {
                _dumpedAllEnemies = true;
                allNames.Sort(StringComparer.OrdinalIgnoreCase);
                Plugin.Log.LogInfo($"[Delivery] all loaded enemies ({allNames.Count}): {string.Join(", ", allNames)}");
            }

            if (cfg.EnableLogging)
            {
                var names = new List<string>(result.Count);
                foreach (var e in result) names.Add(e.enemyName);
                Plugin.Log.LogInfo($"[Delivery] monster pool ({result.Count}): " +
                                   (names.Count > 0 ? string.Join(", ", names) : "<empty>"));
            }
            return result;
        }
    }
}
