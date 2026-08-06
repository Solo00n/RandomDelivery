using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RandomDelivery
{
    /// <summary>
    /// Persistent MonoBehaviour (DontDestroyOnLoad) that drives WHEN deliveries happen.
    ///
    /// It polls the ship state a few times a second — the same robust approach as MonstersGordion's
    /// LandingWatcher — instead of relying on a single coroutine hook that heavy modpacks often replace.
    /// On each new landing it re-reads the JSON config, resets the per-day counter, and builds the set of
    /// trigger times. Delivery firing is gated to the host.
    /// </summary>
    internal sealed class DeliveryScheduler : MonoBehaviour
    {
        private const float PollInterval = 0.25f;
        private const float StartOfDayDelay = 3f; // let players spawn into the ship before the first drop

        private enum TriggerKind { StartOfDay, Seconds, ClockTime }

        private sealed class Trigger
        {
            public TriggerKind Kind;
            public float Seconds;      // for Seconds: seconds since day start
            public float Normalized;   // for ClockTime: normalizedTimeOfDay threshold [0..1]
            public bool Fired;
            public string Label;
        }

        private float _nextPoll;
        private bool _dayActive;         // true while we consider the ship "landed for a workday"
        private int _deliveredToday;
        private float _landedElapsed;    // seconds since this landing began
        private readonly List<Trigger> _triggers = new List<Trigger>();

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollInterval;

            try { Poll(); }
            catch (Exception e) { Plugin.Log.LogError($"[Delivery] scheduler poll failed: {e}"); }
        }

        private void Poll()
        {
            var sor = StartOfRound.Instance;
            if (sor == null) { _dayActive = false; return; } // main menu / disconnected

            bool landed = sor.shipHasLanded && !sor.shipIsLeaving && !sor.inShipPhase;

            if (landed && !_dayActive)
            {
                OnDayStart();
            }
            else if (!landed && _dayActive)
            {
                _dayActive = false; // returned to orbit / leaving; next landing is a fresh day
            }

            if (!_dayActive) return;

            _landedElapsed += PollInterval;

            var cfg = Plugin.Cfg;
            if (cfg == null || !cfg.Enabled) return;
            if (!DeliveryManager.IsHost) return;                 // only the host delivers
            if (_deliveredToday >= cfg.MaxDeliveriesPerDay) return;

            float dayTime = TimeOfDay.Instance != null ? TimeOfDay.Instance.currentDayTime : _landedElapsed;

            // Fire at most one due trigger per poll (so several due at once still space out a little).
            float normalized = TimeOfDay.Instance != null ? TimeOfDay.Instance.normalizedTimeOfDay : 0f;
            foreach (var trig in _triggers)
            {
                if (trig.Fired) continue;
                if (!IsDue(trig, dayTime, normalized)) continue;

                trig.Fired = true;
                if (DeliveryManager.RunDelivery(trig.Label))
                    _deliveredToday++;
                break; // one delivery per poll so multiple due triggers space out
            }
        }

        private bool IsDue(Trigger t, float dayTime, float normalized)
        {
            switch (t.Kind)
            {
                case TriggerKind.StartOfDay: return _landedElapsed >= StartOfDayDelay;
                case TriggerKind.Seconds: return dayTime >= t.Seconds;
                case TriggerKind.ClockTime: return normalized >= t.Normalized;
                default: return false;
            }
        }

        /// <summary>Re-reads config and rebuilds the trigger set for a freshly started day.</summary>
        private void OnDayStart()
        {
            _dayActive = true;
            _deliveredToday = 0;
            _landedElapsed = 0f;

            // Re-read the .cfg from disk each day so hand edits apply. (In-game LethalConfig edits already
            // update the live values immediately, so this is only for external file edits.)
            Plugin.ReloadConfigFile();
            DeliveryManager.OnNewDay();

            BuildTriggers();

            if (Plugin.Cfg.EnableLogging)
                Plugin.Log.LogInfo(
                    $"[Delivery] New day: {_triggers.Count} scheduled trigger(s), " +
                    $"max/day={Plugin.Cfg.MaxDeliveriesPerDay}.");
        }

        private void BuildTriggers()
        {
            _triggers.Clear();
            var cfg = Plugin.Cfg;
            if (cfg.DeliveryTimes == null) return;

            int numberOfHours = TimeOfDay.Instance != null && TimeOfDay.Instance.numberOfHours > 0
                ? TimeOfDay.Instance.numberOfHours
                : 18; // fallback: LC's workday is ~18 in-game hours

            foreach (var raw in cfg.DeliveryTimes)
            {
                if (raw == null) continue;
                var t = ParseTrigger(raw, numberOfHours);
                if (t != null) _triggers.Add(t);
            }
        }

        /// <summary>
        /// Parses one DeliveryTimes entry. Accepts:
        ///   * "StartOfDay",
        ///   * "HH:MM" (mapped onto the in-game clock; the workday runs from ~6:00 over numberOfHours),
        ///   * a JSON number or numeric string (seconds since the day started).
        /// </summary>
        private Trigger ParseTrigger(object raw, int numberOfHours)
        {
            // JSON numbers come through as long/double; numeric strings are handled below.
            if (raw is long l) return SecondsTrigger(l);
            if (raw is int iv) return SecondsTrigger(iv);
            if (raw is double d) return SecondsTrigger((float)d);
            if (raw is float f) return SecondsTrigger(f);

            string s = raw.ToString().Trim();
            if (s.Length == 0) return null;

            if (s.Equals("StartOfDay", StringComparison.OrdinalIgnoreCase))
                return new Trigger { Kind = TriggerKind.StartOfDay, Label = "StartOfDay" };

            if (s.Contains(":"))
            {
                var parts = s.Split(':');
                if (parts.Length >= 2
                    && int.TryParse(parts[0], out int hh)
                    && int.TryParse(parts[1], out int mm))
                {
                    // 6:00 AM = start of the workday (normalizedTimeOfDay 0).
                    float hoursIntoDay = (hh + mm / 60f) - 6f;
                    float normalized = Mathf.Clamp01(hoursIntoDay / numberOfHours);
                    return new Trigger { Kind = TriggerKind.ClockTime, Normalized = normalized, Label = $"clock {s}" };
                }
                Plugin.Log.LogWarning($"[Delivery] Could not parse time '{s}' — ignored.");
                return null;
            }

            if (float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out float secs))
                return SecondsTrigger(secs);

            Plugin.Log.LogWarning($"[Delivery] Unrecognised DeliveryTimes entry '{s}' — ignored.");
            return null;
        }

        private static Trigger SecondsTrigger(float seconds) =>
            new Trigger { Kind = TriggerKind.Seconds, Seconds = Mathf.Max(0f, seconds), Label = $"{Mathf.RoundToInt(seconds)}s" };
    }
}
