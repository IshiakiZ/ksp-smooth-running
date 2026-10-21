using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SmoothRunning
{
    /// <summary>
    /// (Since 0.3.0.) A launch's terrain built once, not twice.
    ///
    /// Measured (2026-10-19, Warm Start's probe build, the test save): a launch's longest step is the home world's terrain, and
    /// it is built twice. Setting the craft on its launch site, the game starts the site's sphere (`PSystemSetup.SetPQSActive`
    /// with that sphere: `PQS.ForceStart`, the whole sphere built at once, 1.16 s; every other sphere told to reset and wait),
    /// puts the craft on the ground, and then calls `SetPQSActive()` with no sphere, which tells every sphere to reset and wait:
    /// the one just built is thrown away, and since the craft is on it, it is built again on the spot (1.35 s more). Found in
    /// the game's own code (FlightDriver.setStartupNewVessel). A save's craft loaded into flight is set up the other way, once.
    ///
    /// Here, within a launch's setting up, that second call is left out when a sphere was started for the launch: every other
    /// sphere was told to reset and wait by the first. (KSP Community Fixes mends the same, its PQSOnlyStartOnce: where it is
    /// installed this one stands aside.)
    /// </summary>
    static class LaunchTerrain
    {
        static bool launching, started, patched;
        static int skipped;
        static string why = "";

        public static void Patch(Harmony h)
        {
            try
            {
                foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                    if (a.name == "KSPCommunityFixes") { why = "KSP Community Fixes is installed and does the same: left to it"; return; }
                MethodInfo setup = AccessTools.Method(typeof(FlightDriver), "setStartupNewVessel");
                MethodInfo one = AccessTools.Method(typeof(PSystemSetup), nameof(PSystemSetup.SetPQSActive), new[] { typeof(PQS) });
                MethodInfo every = AccessTools.Method(typeof(PSystemSetup), nameof(PSystemSetup.SetPQSActive), Type.EmptyTypes);
                if (setup == null || one == null || every == null) { why = "the game's launch was not found as expected: left alone"; return; }
                h.Patch(setup, prefix: new HarmonyMethod(typeof(LaunchTerrain), nameof(Begin)), finalizer: new HarmonyMethod(typeof(LaunchTerrain), nameof(End)));
                h.Patch(one, postfix: new HarmonyMethod(typeof(LaunchTerrain), nameof(One)));
                h.Patch(every, prefix: new HarmonyMethod(typeof(LaunchTerrain), nameof(Every)));
                patched = true;
            }
            catch (Exception ex) { why = "could not be put in: " + ex.Message; Debug.LogWarning("[Smooth Running] a launch's terrain was left as the game has it: " + ex); }
        }

        static void Begin() { launching = true; started = false; }

        static Exception End(Exception __exception) { launching = false; started = false; return __exception; }

        static void One() { if (launching) started = true; }

        static bool Every()
        {
            if (!launching || !started || (Running.launchTerrain != null && !Running.launchTerrain)) return true;
            skipped++;
            return false;
        }

        public static string Said()
        {
            if (!patched) return "A launch's terrain: " + why;
            if (Running.launchTerrain != null && !Running.launchTerrain) return "A launch's terrain: built twice, as the game has it (off).";
            return "A launch's terrain: built once" + (skipped > 0 ? "; " + skipped + " second building" + (skipped == 1 ? "" : "s") + " left out so far this game" : "") + ".";
        }
    }
}
