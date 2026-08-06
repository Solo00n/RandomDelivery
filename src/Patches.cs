using HarmonyLib;

namespace RandomDelivery
{
    /// <summary>
    /// Harmony hook into the round lifecycle. <c>StartOfRound.StartGame</c> runs when a new day/round
    /// begins (on every client). We use it purely to drop per-round caches so a recreated Terminal /
    /// changed shop is picked up fresh; the actual delivery timing is owned by <see cref="DeliveryScheduler"/>.
    /// </summary>
    [HarmonyPatch(typeof(StartOfRound))]
    internal static class StartOfRoundPatches
    {
        [HarmonyPatch(nameof(StartOfRound.StartGame))]
        [HarmonyPostfix]
        private static void OnStartGame()
        {
            DeliveryManager.OnNewDay();
        }
    }
}
