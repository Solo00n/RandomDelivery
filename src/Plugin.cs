using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RandomDelivery
{
    /// <summary>
    /// Main entry point of the mod (the <c>RandomDeliveryMod</c> the spec asks for).
    /// BepInEx instantiates this once at game start. It:
    ///   * loads the JSON configuration,
    ///   * applies the Harmony patches,
    ///   * spins up a persistent <see cref="DeliveryScheduler"/> that drives the delivery timers.
    ///
    /// The plugin is itself a MonoBehaviour, so it can host coroutines (used for the small
    /// delays around mine arming / item settling).
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("Lethal Company.exe")]
    public class Plugin : BaseUnityPlugin
    {
        // GUID doubles as the config file base name -> BepInEx writes settings to
        // BepInEx/config/Timofey.RandomDelivery.cfg (which LethalConfig then edits in-game).
        public const string GUID = "Timofey.RandomDelivery";
        public const string NAME = "RandomDelivery";
        public const string VERSION = "1.5.1";

        /// <summary>Singleton, used as the coroutine host for delayed spawns.</summary>
        public static Plugin Instance { get; private set; }

        /// <summary>Shared logger. Actual verbosity for delivery details is gated by config.</summary>
        public static ManualLogSource Log { get; private set; }

        /// <summary>The live configuration, backed by the BepInEx config file (LethalConfig-editable).</summary>
        public static DeliveryConfig Cfg { get; internal set; }

        private readonly Harmony _harmony = new Harmony(GUID);

        /// <summary>
        /// Re-reads the .cfg from disk so hand edits (outside the game / LethalConfig) are picked up.
        /// Exposed because <see cref="BaseUnityPlugin.Config"/> is not accessible from other classes.
        /// </summary>
        public static void ReloadConfigFile()
        {
            try { Instance?.Config.Reload(); }
            catch (System.Exception e) { Log?.LogWarning($"Config reload failed: {e.Message}"); }
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Bind every setting to BepInEx/config/Timofey.RandomDelivery.cfg. LethalConfig auto-detects
            // these entries and renders an in-game menu for them.
            Cfg = new DeliveryConfig(Config);

            // Apply all [HarmonyPatch] classes in this assembly.
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // A persistent watcher that survives scene loads and drives the delivery schedule.
            var go = new GameObject("RandomDelivery_Scheduler");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<DeliveryScheduler>();

            Log.LogInfo($"{NAME} v{VERSION} loaded.");
        }
    }
}
