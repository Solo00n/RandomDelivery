using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace RandomDelivery
{
    /// <summary>
    /// Low-level, HOST-ONLY helpers that create the actual entities in the world and network them to
    /// clients. Everything here mirrors the proven v81 spawn paths from the sibling mods:
    ///   * items  -> Instantiate + settle-to-floor + NetworkObject.Spawn,
    ///   * traps  -> Instantiate + NetworkObject.Spawn(true) (+ optional mine auto-detonate),
    ///   * monsters -> RoundManager.SpawnEnemyGameObject (auto-replicates).
    /// </summary>
    internal static class SpawnHelper
    {
        // Independent RNG: the game re-seeds UnityEngine.Random from the map seed, which biases repeated
        // picks. System.Random keeps our selection genuinely random (same fix as LethalPresents).
        private static readonly System.Random Rng = new System.Random();

        // ============================================================ DELIVERY POSITIONS

        /// <summary>
        /// Returns <paramref name="count"/> world positions to drop items on the landing pad.
        ///
        /// The item dropship's <c>itemSpawnPositions</c> are the exact place ordered items land — but ONLY
        /// while the dropship is parked on the pad. With no active order it idles high in the sky, so those
        /// transforms would put our items far above/off the map (invisible). We therefore only use the
        /// dropship spot when it is genuinely near the ship at ground level; otherwise we spread the items
        /// in a ring around the ship, wide enough to clear its hull so they land on the open pad.
        /// </summary>
        internal static List<Vector3> GetAnchorPositions(int count)
        {
            var list = new List<Vector3>(count);
            bool usedDropship = TryGetDropshipPad(out Vector3 center);
            if (!usedDropship) center = PadCenterNearShip();

            for (int i = 0; i < count; i++)
            {
                Vector3 anchor;
                if (usedDropship)
                {
                    // Dropship is parked on the pad: cluster loosely around its delivery spot.
                    anchor = center + new Vector3((float)(Rng.NextDouble() - 0.5) * 2.4f, 1.0f,
                                                  (float)(Rng.NextDouble() - 0.5) * 2.4f);
                }
                else
                {
                    // Ring around the navmesh point next to the ship, kept tight so each anchor stays close
                    // to walkable ground (SpawnItem/SpawnTrap then snap it precisely onto the navmesh).
                    float ang = Mathf.Deg2Rad * (360f / Mathf.Max(1, count) * i + (float)(Rng.NextDouble() * 40.0));
                    float r = 3.5f + (float)(Rng.NextDouble() * 2.5);
                    anchor = center + new Vector3(Mathf.Cos(ang) * r, 1f, Mathf.Sin(ang) * r);
                }
                list.Add(anchor);
            }

            if (Plugin.Cfg.EnableLogging)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(" ; "); sb.Append(Fmt(list[i])); }
                Plugin.Log.LogInfo($"[Delivery] anchors via {(usedDropship ? "dropship-pad" : "ship-ring")} " +
                                   $"center={Fmt(center)} -> {sb}");
            }
            return list;
        }

        /// <summary>
        /// True if the ItemDropship is parked near the ship on the pad (not idling up in the sky), and
        /// returns its item-drop spot — the exact place ordered items normally land.
        /// </summary>
        private static bool TryGetDropshipPad(out Vector3 pad)
        {
            pad = Vector3.zero;
            var dropship = UnityEngine.Object.FindObjectOfType<ItemDropship>();
            if (dropship == null)
            {
                if (Plugin.Cfg.EnableLogging) Plugin.Log.LogInfo("[Delivery] No ItemDropship in scene.");
                return false;
            }

            Transform[] spots = dropship.itemSpawnPositions;
            Vector3 spot = (spots != null && spots.Length > 0 && spots[0] != null)
                ? spots[0].position : dropship.transform.position;
            Vector3 ship = ShipPoint();
            float horiz = Vector2.Distance(new Vector2(spot.x, spot.z), new Vector2(ship.x, ship.z));
            float vert = Mathf.Abs(spot.y - ship.y);

            if (Plugin.Cfg.EnableLogging)
                Plugin.Log.LogInfo($"[Delivery] ItemDropship spot={Fmt(spot)} ship={Fmt(ship)} " +
                                   $"horiz={horiz:F1} vert={vert:F1}");

            if (horiz <= 30f && vert <= 12f) { pad = spot; return true; }
            return false; // idle / high in the sky -> use the ship ring instead
        }

        /// <summary>
        /// A walkable navmesh point next to the ship, used as the ring centre for the direct (no-dropship)
        /// spawn path. The raw ship transform sits inside/above the hull where the navmesh can be &gt;15 m
        /// away (which made trap placement fail), so we snap it onto the nearest navmesh with a wide search.
        /// </summary>
        private static Vector3 PadCenterNearShip()
        {
            Vector3 ship = ShipPoint();
            if (NavMesh.SamplePosition(ship, out NavMeshHit hit, 40f, NavMesh.AllAreas))
                return hit.position;
            return ship;
        }

        private static Vector3 ShipPoint()
        {
            var sor = StartOfRound.Instance;
            if (sor != null && sor.elevatorTransform != null) return sor.elevatorTransform.position;
            if (sor != null && sor.shipLandingPosition != null) return sor.shipLandingPosition.position;
            return Vector3.zero;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F1},{v.y:F1},{v.z:F1})";

        // ============================================================ ITEMS

        /// <summary>
        /// Spawns a free copy of a buyable shop item, resting on the pad, and networks it.
        ///
        /// Multiplayer note: the item's fall fields are NOT NetworkVariables, so we cannot rely on a
        /// server-side "fall" replicating. Instead we raycast the floor on the server and spawn the item
        /// already grounded there. The NetworkObject spawn syncs that resting transform to every client,
        /// so it lands in the right place for everyone regardless of how each client settles it.
        /// </summary>
        internal static bool SpawnItem(Item item, Vector3 anchor)
        {
            if (item == null || item.spawnPrefab == null) return false;

            // Resolve the actual walkable pad under the anchor. Prefer the NAVMESH (the real ground
            // players stand on) — a plain raycast can hit invisible helper colliders like AI
            // line-of-sight cubes and leave the item floating. Raycast is only a last resort, and even
            // then against the game's ground/room mask (not everything) so it skips those helpers.
            string how;
            Vector3 rest;
            if (NavMesh.SamplePosition(anchor, out NavMeshHit navHit, 15f, NavMesh.AllAreas))
            {
                rest = navHit.position;
                how = "navmesh";
            }
            else
            {
                int mask = StartOfRound.Instance != null
                    ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                    : ~0;
                if (Physics.Raycast(anchor + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 60f, mask,
                        QueryTriggerInteraction.Ignore))
                {
                    rest = hit.point; how = "raycast:" + hit.collider.name;
                }
                else { rest = anchor; how = "anchor(no-ground)"; }
            }
            rest += Vector3.up * Mathf.Max(0.05f, item.verticalOffset);

            if (Plugin.Cfg.EnableLogging)
                Plugin.Log.LogInfo($"[Delivery]   item '{item.itemName}' anchor={Fmt(anchor)} " +
                                   $"via {how} rest={Fmt(rest)}");

            Transform parent = StartOfRound.Instance != null ? StartOfRound.Instance.propsContainer : null;
            Quaternion rot = Quaternion.Euler(item.restingRotation.x, item.restingRotation.y, item.restingRotation.z);

            GameObject go = UnityEngine.Object.Instantiate(item.spawnPrefab, rest, rot, parent);

            var grab = go.GetComponent<GrabbableObject>();
            var netObj = go.GetComponent<NetworkObject>();
            if (grab == null || netObj == null)
            {
                Plugin.Log.LogWarning($"[Delivery] Item '{item.itemName}' prefab missing GrabbableObject/NetworkObject; skipped.");
                UnityEngine.Object.Destroy(go);
                return false;
            }

            // Mark it as already landed at this spot so nothing yanks it to the origin.
            try
            {
                if (grab.itemProperties != null && grab.itemProperties.isScrap)
                    grab.SetScrapValue(Mathf.Max(0, item.creditsWorth));

                grab.fallTime = 1f;
                grab.hasHitGround = true;
                grab.reachedFloorTarget = true;
                Transform p = go.transform.parent;
                grab.targetFloorPosition = p != null ? p.InverseTransformPoint(rest) : rest;
                grab.startFallingPosition = grab.targetFloorPosition;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Delivery] Floor placement for '{item.itemName}' failed: {e.Message}");
            }

            netObj.Spawn(); // replicates to all clients
            return true;
        }

        // ============================================================ DROPSHIP DELIVERY

        // Vanilla ItemDropship.LandShipOnServer() is private; we invoke it to start the descent right when
        // the delivery fires (at its DeliveryTimes-scheduled time), for both item and pure trap/monster drops.
        private static MethodInfo _landShipMethod;
        private static MethodInfo LandShipMethod =>
            _landShipMethod ??= typeof(ItemDropship).GetMethod("LandShipOnServer",
                BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Sends a delivery on the game's real item dropship. The items are added to
        /// <c>Terminal.orderedItemsFromTerminal</c> — exactly what a paid order does, minus the credit
        /// charge (only deducted in Terminal.BuyItemsServerRpc, which we bypass) — and the ship's descent is
        /// started immediately, so it arrives at the delivery's scheduled time (DeliveryTimes). Any traps/
        /// monsters ride along and are dropped at the ship's own spots once its hatch opens, so everything
        /// arrives in the same place. Returns false if there is no terminal/dropship (caller direct-spawns).
        /// </summary>
        internal static bool QueueDropshipDelivery(List<int> itemIndices, List<TrapPrefab> traps, List<EnemyType> monsters)
        {
            var terminal = ItemListProvider.GetTerminal();
            if (terminal == null || terminal.orderedItemsFromTerminal == null || terminal.buyableItemsList == null)
                return false;

            var dropship = UnityEngine.Object.FindObjectOfType<ItemDropship>();
            if (dropship == null || Plugin.Instance == null) return false;

            // Add our (free) items to the order and start the descent now.
            int added = 0;
            if (itemIndices != null)
                foreach (int i in itemIndices)
                    if (i >= 0 && i < terminal.buyableItemsList.Length) { terminal.orderedItemsFromTerminal.Add(i); added++; }
            terminal.numberOfItemsInDropship = Mathf.Clamp(terminal.numberOfItemsInDropship + added, 0, 12);

            if (!dropship.deliveringOrder && !dropship.shipLanded && LandShipMethod != null)
            {
                try { LandShipMethod.Invoke(dropship, null); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Delivery] Starting dropship descent failed: {e.Message}"); }
            }

            Plugin.Instance.StartCoroutine(HandleDropshipDelivery(dropship, traps, monsters));

            if (Plugin.Cfg.EnableLogging)
                Plugin.Log.LogInfo($"[Delivery] Dropship dispatched: carrying {added} item(s), " +
                                   $"{(traps?.Count ?? 0)} trap(s) + {(monsters?.Count ?? 0)} monster(s) to drop on opening.");
            return true;
        }

        /// <summary>
        /// Waits for the dispatched dropship to land, opens it (if configured), and — once the hatch is open
        /// (by us or a player) — drops this delivery's traps/monsters at the ship's own item-drop spots.
        /// </summary>
        private static IEnumerator HandleDropshipDelivery(ItemDropship dropship, List<TrapPrefab> traps, List<EnemyType> monsters)
        {
            bool hasExtras = (traps != null && traps.Count > 0) || (monsters != null && monsters.Count > 0);
            bool log = Plugin.Cfg.EnableLogging;

            // wait for the landing animation to finish.
            float t = 0f;
            while (dropship != null && !dropship.shipLanded && t < 180f) { t += 0.25f; yield return new WaitForSeconds(0.25f); }
            if (dropship == null) yield break;
            if (log) Plugin.Log.LogInfo("[Delivery] Dropship landed.");

            // Open it ourselves only if configured; otherwise leave it closed for a player to open (vanilla).
            if (Plugin.Cfg.DropshipAutoOpen && !dropship.shipDoorsOpened)
            {
                try { dropship.TryOpeningShip(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Delivery] Auto-open failed: {e.Message}"); }
            }

            if (!hasExtras) yield break;

            // wait until the hatch is open (the ship only lingers ~30s, so give up after that).
            t = 0f;
            while (dropship != null && !dropship.shipDoorsOpened && t < 35f) { t += 0.25f; yield return new WaitForSeconds(0.25f); }
            if (dropship == null || !dropship.shipDoorsOpened)
            {
                if (log) Plugin.Log.LogInfo("[Delivery] Dropship never opened — traps/monsters not deployed.");
                yield break;
            }

            if (log) Plugin.Log.LogInfo("[Delivery] Dropship open — deploying traps/monsters at its drop spots.");
            yield return new WaitForSeconds(0.2f); // let the items instantiate first
            SpawnAtDropship(dropship, traps, monsters);
        }

        /// <summary>Spawns the delivery's traps/monsters at the landed dropship's item-drop spots.</summary>
        private static void SpawnAtDropship(ItemDropship dropship, List<TrapPrefab> traps, List<EnemyType> monsters)
        {
            Transform[] spots = dropship.itemSpawnPositions;
            int idx = 0;

            if (traps != null)
                foreach (var trap in traps)
                    SpawnTrap(trap, DropSpot(dropship, spots, ref idx));
            if (monsters != null)
                foreach (var m in monsters)
                    SpawnMonster(m, DropSpot(dropship, spots, ref idx));
        }

        private static Vector3 DropSpot(ItemDropship dropship, Transform[] spots, ref int idx)
        {
            Vector3 p = (spots != null && spots.Length > 0 && spots[idx % spots.Length] != null)
                ? spots[idx % spots.Length].position
                : dropship.transform.position;
            idx++;
            p += new Vector3((float)(Rng.NextDouble() - 0.5) * 1.6f, 0.5f, (float)(Rng.NextDouble() - 0.5) * 1.6f);
            return p;
        }

        // ============================================================ TRAPS

        /// <summary>Spawns a trap (turret/landmine) on reachable ground near the anchor. Host only.</summary>
        internal static bool SpawnTrap(TrapPrefab trap, Vector3 anchor)
        {
            if (trap == null || trap.Prefab == null) return false;

            if (!TryGetGroundPosition(anchor, out Vector3 ground, out Vector3 normal))
            {
                Plugin.Log.LogWarning($"[Delivery] No walkable ground near {anchor} for trap '{trap.Name}'.");
                return false;
            }

            // Stand the trap upright on the surface with a random yaw.
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, normal)
                             * Quaternion.Euler(0f, (float)(Rng.NextDouble() * 360.0), 0f);

            GameObject go = UnityEngine.Object.Instantiate(trap.Prefab, ground, rot);
            var netObj = go.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Plugin.Log.LogWarning($"[Delivery] Trap '{trap.Name}' prefab has no NetworkObject; skipped.");
                UnityEngine.Object.Destroy(go);
                return false;
            }

            netObj.Spawn(true); // destroyWithScene = true; turrets and mines are live traps on spawn.
            return true;
        }

        // ============================================================ MONSTERS

        /// <summary>Networked monster spawn via the game's own RoundManager helper (server only).</summary>
        internal static bool SpawnMonster(EnemyType type, Vector3 anchor)
        {
            if (RoundManager.Instance == null || type == null) return false;

            TryGetMonsterPosition(anchor, out Vector3 pos);
            float yRot = (float)(Rng.NextDouble() * 360.0);

            try
            {
                // Passing a non-null EnemyType makes the game instantiate that exact prefab and Spawn a
                // NetworkObject replicated to all clients — independent of the moon's enemy list.
                NetworkObjectReference reference = RoundManager.Instance.SpawnEnemyGameObject(pos, yRot, -1, type);
                bool ok = reference.TryGet(out NetworkObject _);
                if (Plugin.Cfg.EnableLogging)
                    Plugin.Log.LogInfo($"[Delivery] Spawned monster '{type.enemyName}' at {Fmt(pos)} (ok={ok}).");
                return ok;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Delivery] Monster '{type.enemyName}' failed to spawn: {e.Message}");
                return false;
            }
        }

        // ============================================================ POSITIONING

        /// <summary>
        /// Snaps to the navmesh (the real walkable pad) so traps sit on reachable ground, then reads the
        /// surface normal via a short raycast against the ground/room mask so the trap stands upright. Uses
        /// the navmesh FIRST (not a raw raycast) to avoid landing on invisible AI line-of-sight colliders.
        /// </summary>
        internal static bool TryGetGroundPosition(Vector3 origin, out Vector3 position, out Vector3 normal)
        {
            position = origin;
            normal = Vector3.up;

            if (!NavMesh.SamplePosition(origin, out NavMeshHit navHit, 15f, NavMesh.AllAreas))
                return false;

            position = navHit.position + Vector3.up * 0.05f;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (Physics.Raycast(position + Vector3.up * 1f, Vector3.down, out RaycastHit hit, 3f, mask,
                    QueryTriggerInteraction.Ignore))
                normal = hit.normal;

            return true;
        }

        /// <summary>Like the above but never fails: monster NavMeshAgents settle themselves onto the mesh.</summary>
        internal static bool TryGetMonsterPosition(Vector3 origin, out Vector3 position)
        {
            if (TryGetGroundPosition(origin, out position, out _)) return true;
            position = origin;
            return true;
        }
    }
}
