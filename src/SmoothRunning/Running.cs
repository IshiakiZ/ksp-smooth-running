using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keystone;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("SmoothRunning", 0, 1)]
[assembly: KSPAssemblyDependency("Keystone", 0, 8)]
[assembly: KSPAssemblyDependency("HarmonyKSP", 1, 0)]
#endif

namespace SmoothRunning
{
    /// <summary>
    /// The game made faster where it wastes its time, found by measuring it first (the owner, 2026-10-13: "A performance
    /// enhancer, just overall performance. I want you to improve, research, design, and invent your own stuff, and then
    /// also improve rendering in the game. You can change the game's code if you need to for this mod").
    ///
    /// Each fix is a change to the game's own code as it runs (through Harmony; nothing on disk is touched), made only after
    /// the time it wastes was measured in the game, and each can be switched off on the mod's page. What each one saved is
    /// said there. Where KSP Community Fixes is installed and already mends the same thing, this one stands aside.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class RunningAtStart : MonoBehaviour
    {
        void Awake() { Running.Setup(); Destroy(gameObject); }
    }

    public static class Running
    {
        public const string Version = "0.1.0";
        internal const string Id = "smoothrunning";
        static Harmony harmony;
        static Mod mod;
        internal static Toggle terrain;

        public static void Setup()
        {
            if (mod == null || Kit.Find("Smooth Running") != mod)
            {
                mod = Kit.Register("Smooth Running", Version, "The game made faster where it wastes its time, each fix measured in the game first. Each can be switched off here.");
                terrain = mod.Toggle("terrain", "One update per terrain sphere", true, "The game starts a world's terrain update again at every load and scene change without stopping the last one: after a while of play it runs dozens of times a frame. Off: as the game has it.");
                mod.Panel = Readout;
            }
            harmony = new Harmony(Id);
            // (a development build swapping a new copy in: whatever an old copy put into the game goes first)
            try { harmony.UnpatchAll(Id); } catch (Exception) { }
            TerrainUpdates.Patch(harmony);
        }

        static void Readout()
        {
            GUILayout.Label(TerrainUpdates.Said());
        }

#if DEV
        static ShadowQuality shadowsWere = (ShadowQuality)(-1);
        static int cascadesWere = -1;
        static float distanceWere = -1f;
        /// <summary>(Development build, for measuring what the shadows cost, turn and turn about: "off", "2" (two cascades),
        /// "far N" (shadow distance N m), "back" (as they were). The game's settings file is not touched.)</summary>
        public static string Shadows(string how)
        {
            if ((int)shadowsWere < 0) { shadowsWere = QualitySettings.shadows; cascadesWere = QualitySettings.shadowCascades; distanceWere = QualitySettings.shadowDistance; }
            string[] w = (how ?? "").Split(' ');
            if (w[0] == "off") QualitySettings.shadows = ShadowQuality.Disable;
            else if (w[0] == "2") QualitySettings.shadowCascades = 2;
            else if (w[0] == "far" && w.Length > 1) QualitySettings.shadowDistance = float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture);
            else if (w[0] == "back") { QualitySettings.shadows = shadowsWere; QualitySettings.shadowCascades = cascadesWere; QualitySettings.shadowDistance = distanceWere; }
            return "shadows " + QualitySettings.shadows + ", cascades " + QualitySettings.shadowCascades + ", distance " + QualitySettings.shadowDistance + ", resolution " + QualitySettings.shadowResolution + " (as they were: " + shadowsWere + ", " + cascadesWere + ", " + distanceWere + ")";
        }
#endif
    }

    /// <summary>
    /// The terrain's update, once a frame per sphere, not once for every time the sphere was ever started.
    ///
    /// Measured (2026-10-14, at the pad after a session of loads): the game's terrain coroutine `PQS.UpdateSphere` cost
    /// 6.0 ms of every frame, the dearest thing the game itself did; Kerbin's quad tree was being updated 75 times a frame.
    /// Why (the game's own code, decompiled): a sphere's update is started as `StartCoroutine(UpdateSphere())`, the routine
    /// itself, at every StartSphere (every load, every scene change, every time the world is brought back), and stopped with
    /// `StopCoroutine("UpdateSphere")`, by name, which only ends coroutines that were started by name: so none is ever
    /// stopped, and each start adds one more that runs to the end of the game.
    ///
    /// The game's other coroutine of a sphere, ResetAndWaitCoroutine (started at every ResetAndWait, stopped the same way by
    /// name), leaks alike: 28 of them were found ticking every frame for the worlds not in use, each waiting for its world
    /// to come alive. Cheap while they wait; mended the same way.
    ///
    /// Here: the newest of each kind started for each sphere is noted, and an older one still running for the same sphere
    /// ends at its next step, doing nothing more. (KSP Community Fixes mends the same flaw in its own way; with it installed
    /// only one is ever running and this changes nothing.)
    /// </summary>
    static class TerrainUpdates
    {
        // (for each of the two coroutines: the newest started, by sphere; and how to find a running one's sphere)
        static readonly string[] Kinds = { "UpdateSphere", "ResetAndWaitCoroutine" };
        static readonly Dictionary<int, object>[] newest = { new Dictionary<int, object>(), new Dictionary<int, object>() };
        static readonly Dictionary<Type, int> kindOf = new Dictionary<Type, int>();
        static readonly AccessTools.FieldRef<object, PQS>[] owner = new AccessTools.FieldRef<object, PQS>[2];
        static int ended;
        static bool patched;
        static string why = "";

        public static void Patch(Harmony h)
        {
            try
            {
                for (int k = 0; k < Kinds.Length; k++)
                {
                    MethodInfo start = AccessTools.Method(typeof(PQS), Kinds[k]);
                    Type machine = start?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
                    if (machine == null) { why = "the game's terrain coroutine " + Kinds[k] + " was not found as expected: left alone"; continue; }
                    kindOf[machine] = k;
                    owner[k] = AccessTools.FieldRefAccess<PQS>(machine, "<>4__this");
                    h.Patch(start, postfix: new HarmonyMethod(typeof(TerrainUpdates), nameof(Started)));
                    h.Patch(AccessTools.Method(machine, "MoveNext"), prefix: new HarmonyMethod(typeof(TerrainUpdates), nameof(Step)));
                    patched = true;
                }
            }
            catch (Exception ex) { why = "could not be put in: " + ex.Message; Debug.LogWarning("[Smooth Running] the terrain's updates were left as the game has them: " + ex); }
        }

        /// <summary>One of a sphere's coroutines has just been made: it is the newest of its kind for that sphere.</summary>
        static void Started(PQS __instance, IEnumerator __result)
        {
            if (__instance == null || __result == null || !kindOf.TryGetValue(__result.GetType(), out int k)) return;
            newest[k][__instance.GetInstanceID()] = __result;
        }

        /// <summary>One is about to take its next step: only the newest of its kind for its sphere goes on.</summary>
        static bool Step(object __instance, ref bool __result)
        {
            if (Running.terrain != null && !Running.terrain) return true;
            if (!kindOf.TryGetValue(__instance.GetType(), out int k)) return true;
            PQS q = owner[k](__instance);
            if (q == null) return true;
            int id = q.GetInstanceID();
            // (one started before this was in, as in a development build put in mid-game: the first to step is kept)
            if (!newest[k].TryGetValue(id, out object latest)) { newest[k][id] = __instance; return true; }
            if (ReferenceEquals(latest, __instance)) return true;
            ended++;
            __result = false;
            return false;
        }

        public static string Said()
        {
            if (!patched) return "Terrain updates: " + why;
            return "Terrain updates: one per sphere" + (ended > 0 ? "; " + ended + " extra update" + (ended == 1 ? "" : "s") + " ended so far this game" : "") + ".";
        }
    }
}
