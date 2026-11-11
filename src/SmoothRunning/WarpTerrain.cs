using System;
using System.Collections.Generic;
using UnityEngine;

namespace SmoothRunning
{
    /// <summary>
    /// (Since 0.3.0.) The terrain's building spread over frames while time is warped on rails.
    ///
    /// Measured (2026-10-20, the test save's craft in a 250 km orbit of Kerbin, a round of warp on rails up to x1000 and back,
    /// with the development tracer on the game's terrain and the mods' weather): the warp's longest frames, 110 to 154 ms,
    /// were the game building the terrain under the craft as it raced over it, 65 to 119 ms of a frame in `PQS.UpdateQuads`:
    /// some thirty of the terrain's squares built at once, with the normals along their edges. The game lets a world's
    /// terrain take up to a tenth of a second of any frame (`PQS.maxFrameTime`, 0.1 s) before it leaves the rest of its
    /// building for the next frame.
    ///
    /// Here, while time is warped on rails (where nothing meets the ground: the craft is high above it, or landed and still),
    /// and for five seconds after while the craft is more than 10 km up, every world's terrain is given 8 ms of a frame,
    /// its building going on in the frames after; then the game's own tenth again. The nearest squares are built first
    /// either way (the game sorts them by distance), so it is the furthest that come a few frames later.
    /// </summary>
    static class WarpTerrain
    {
        // (as installed: the page's fine details may set others)
        const float Budget = 0.008f;
        const float After = 5f;
        // (each sphere held, and its own budget to give back; and the budget last given, by which a sphere is known to be ours)
        static readonly Dictionary<PQS, float> held = new Dictionary<PQS, float>();
        static float given = Budget;
        static float warpSeen = -100f;
        static int warps;
        static string why = "";

        internal static void Frame()
        {
            bool on = Running.warpTerrain == null || Running.warpTerrain;
            bool warping = TimeWarp.fetch != null && TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1.01f;
            if (warping) warpSeen = Time.unscaledTime;
            Vessel v = FlightGlobals.ActiveVessel;
            bool want = on && (warping || (Time.unscaledTime - warpSeen < Running.Of(Running.warpAfter, After) && v != null && v.altitude > 10000.0));
            if (!want) { if (held.Count > 0) Back(); return; }
            if (held.Count == 0)
            {
                warps++;
                try
                {
                    foreach (CelestialBody b in FlightGlobals.Bodies)
                        if (b != null && b.pqsController != null)
                            foreach (PQS q in b.pqsController.GetComponentsInChildren<PQS>(true))
                                if (q != null && !held.ContainsKey(q)) held[q] = q.maxFrameTime;
                }
                catch (Exception ex) { why = ex.Message; }
            }
            // (not while the game builds a world's maps all at once: it sets its own budget for that, and puts the last back. A
            // sphere at the budget last given is ours, given the page's new one if it has changed, never more than its own)
            float budget = Running.Of(Running.warpBudget, Budget * 1000f) / 1000f;
            foreach (KeyValuePair<PQS, float> one in held)
            {
                PQS q = one.Key;
                if (q == null || q.isBuildingMaps) continue;
                if (Mathf.Approximately(q.maxFrameTime, given)) { if (!Mathf.Approximately(budget, given)) q.maxFrameTime = Mathf.Min(budget, one.Value); }
                else if (q.maxFrameTime > budget && q.maxFrameTime < 1f) q.maxFrameTime = budget;
            }
            given = budget;
        }

        /// <summary>Every sphere's own budget back.</summary>
        internal static void Back()
        {
            foreach (KeyValuePair<PQS, float> one in held)
                if (one.Key != null && !one.Key.isBuildingMaps && Mathf.Approximately(one.Key.maxFrameTime, given)) one.Key.maxFrameTime = one.Value;
            held.Clear();
        }

        internal static string Said()
        {
            if (Running.warpTerrain != null && !Running.warpTerrain) return "The terrain under time warp: as the game has it (off).";
            return "The terrain under time warp: " + (held.Count > 0 ? "spread over frames now" : "spread over frames while warping")
                + (warps > 0 ? "; " + warps + " warp" + (warps == 1 ? "" : "s") + " so far this game" : "") + (why.Length > 0 ? " (" + why + ")" : "") + ".";
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class WarpTerrainInFlight : MonoBehaviour
    {
        void Update() => WarpTerrain.Frame();
        void OnDestroy() => WarpTerrain.Back();
    }
}
