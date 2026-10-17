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
        public const string Version = "0.2.0";
        internal const string Id = "smoothrunning";
        static Harmony harmony;
        static Mod mod;
        internal static Toggle terrain, handlers, scenes;

        public static void Setup()
        {
            if (mod == null || Kit.Find("Smooth Running") != mod)
            {
                mod = Kit.Register("Smooth Running", Version, "The game made faster where it wastes its time, each fix measured in the game first. Each can be switched off here.");
                terrain = mod.Toggle("terrain", "One update per terrain sphere", true, "The game starts a world's terrain update again at every load and scene change without stopping the last one: after a while of play it runs dozens of times a frame. Off: as the game has it.");
                scenes = mod.Toggle("scenes", "One clean-up fewer at every scene change", true, "At every scene change the game has Unity go through all its loaded things for any no longer used, a fifth of a second each time, three times over; the game asks for the second itself, right after Unity has done the first, and it never finds anything. With this on, that second one is left out. Off: as the game has it.");
                handlers = mod.Toggle("handlers", "Clear the handlers of things that are gone", true, "The game's events keep a list of whom to tell. Things that are destroyed stay on those lists, and the game's records of which parts share resources never take themselves off: every scene change leaves hundreds more, still told of every event and keeping the parts of craft long gone in memory. With this on they are cleared at every scene change. Off: as the game has it.");
                mod.Panel = Readout;
            }
            harmony = new Harmony(Id);
            // (a development build swapping a new copy in: whatever an old copy put into the game goes first)
            try { harmony.UnpatchAll(Id); } catch (Exception) { }
            TerrainUpdates.Patch(harmony);
            SceneChanges.Patch(harmony);
            Handlers.Hook();
        }

        static void Readout()
        {
            GUILayout.Label(TerrainUpdates.Said());
            GUILayout.Label(SceneChanges.Said());
            GUILayout.Label(Handlers.Said());
        }

#if DEV
        /// <summary>(Development build: every one of the game's events, how many handlers it holds, and how many of those belong
        /// to things already destroyed (the game still calls them, and keeps what they belong to in memory). For seeing whether
        /// handlers pile up from one scene change to the next.)</summary>
        public static string Sweep() => Handlers.Sweep();

        /// <summary>(Development build: what each fix says on the mod's page.)</summary>
        public static string Status() => TerrainUpdates.Said() + "\n" + SceneChanges.Said() + "\n" + Handlers.Said();

        /// <summary>(Development build: the managed memory in use after a full collection, in MB. A hitch of its own: for tests.)</summary>
        public static string Memory()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return (GC.GetTotalMemory(true) / 1048576.0).ToString("F1") + " MB of managed memory in use after a full collection";
        }

        public static string Events()
        {
            int events = 0, handlers = 0, dead = 0;
            var worst = new List<KeyValuePair<string, int>>();
            var owners = new Dictionary<string, int>();
            var types = new List<Type> { typeof(GameEvents) };
            types.AddRange(typeof(GameEvents).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (Type t in types)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    object e = f.GetValue(null);
                    if (e == null || !(e is BaseGameEvent)) continue;
                    FieldInfo list = null;
                    for (Type k = e.GetType(); k != null && list == null; k = k.BaseType) list = k.GetField("events", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (!(list?.GetValue(e) is IList items)) continue;
                    events++;
                    int gone = 0;
                    foreach (object d in items)
                    {
                        handlers++;
                        FieldInfo o = d.GetType().GetField("originator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        object who = o?.GetValue(d);
                        if (who is UnityEngine.Object u && u == null)
                        {
                            gone++;
                            string n = who.GetType().Name;
                            owners[n] = owners.TryGetValue(n, out int c) ? c + 1 : 1;
                        }
                    }
                    dead += gone;
                    if (gone > 0) worst.Add(new KeyValuePair<string, int>(t.Name + "." + f.Name, gone));
                }
            worst.Sort((a, b) => b.Value.CompareTo(a.Value));
            var top = new List<KeyValuePair<string, int>>(owners);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            string said = events + " events, " + handlers + " handlers, " + dead + " of them belonging to destroyed things; " + (GC.GetTotalMemory(false) / 1048576L) + " MB of managed memory in use";
            for (int i = 0; i < worst.Count && i < 8; i++) said += "\n  " + worst[i].Key + ": " + worst[i].Value;
            said += "\n  destroyed owners:";
            for (int i = 0; i < top.Count && i < 8; i++) said += " " + top[i].Key + " " + top[i].Value + ";";
            return said;
        }

        /// <summary>(Development build: every handler's owner kind, counted, as "kind count" lines: for setting two counts side by
        /// side and seeing which kinds pile up from one scene change to the next. "all" also counts each event's handlers.)</summary>
        public static string Owners(string how)
        {
            var count = new Dictionary<string, int>();
            var types = new List<Type> { typeof(GameEvents) };
            types.AddRange(typeof(GameEvents).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (Type t in types)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    object e = f.GetValue(null);
                    if (e == null || !(e is BaseGameEvent)) continue;
                    FieldInfo list = null;
                    for (Type k = e.GetType(); k != null && list == null; k = k.BaseType) list = k.GetField("events", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (!(list?.GetValue(e) is IList items)) continue;
                    foreach (object d in items)
                    {
                        FieldInfo o = d.GetType().GetField("originator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        object who = o?.GetValue(d);
                        FieldInfo ev = d.GetType().GetField("evt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        Delegate del = ev?.GetValue(d) as Delegate;
                        string key = (who == null ? "(static)" : who.GetType().FullName) + (how == "all" ? " @ " + t.Name + "." + f.Name + " -> " + (del?.Method?.Name ?? "?") : "") + (who is UnityEngine.Object u && u == null ? " (destroyed)" : "");
                        count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
            var rows = new List<KeyValuePair<string, int>>(count);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            var said = new System.Text.StringBuilder();
            foreach (var r in rows) said.Append(r.Key).Append('\t').Append(r.Value).Append('\n');
            return said.ToString();
        }

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

    /// <summary>
    /// One clean-up of unused things fewer at every scene change.
    ///
    /// Measured (2026-10-17, the test save, from a flight to the space centre): a scene change had Unity go through every one
    /// of its loaded things for any no longer used three times, 190 ms each with the game's quarter of a million things
    /// (nearly all of it in marking what is still used). The first freed the 3,727 things of the flight; the second freed
    /// nothing at all; the third, once the space centre was in, freed 11. The game changes scenes by loading a scene that
    /// is all but empty first (its "loading buffer") and then the one asked for; Unity goes through its things by itself
    /// every time a scene takes another's place, and the game, in the same step as asking for the buffer, asks for one more
    /// (HighLogic.bufferedLoad), which comes right after Unity's own, with nothing having changed in between.
    ///
    /// Here that one call of the game's is left out, in both of its ways of changing scenes. Unity's own, after the buffer
    /// scene is in, still frees everything the last scene left. (KSP Community Fixes has a fix of its own for this, one going
    /// through left of three by its own account; this one never touches Unity's own, so with both nothing is lost.)
    /// </summary>
    static class SceneChanges
    {
        static int skipped, found;
        static bool patched;
        static string why = "";

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfo start = AccessTools.Method(typeof(HighLogic), "bufferedLoad");
                Type machine = start?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
                MethodInfo next = machine != null ? AccessTools.Method(machine, "MoveNext") : null;
                if (next == null) { why = "the game's scene change was not found as expected: left alone"; return; }
                found = 0;
                h.Patch(next, transpiler: new HarmonyMethod(typeof(SceneChanges), nameof(Transpile)));
                patched = found > 0;
                if (!patched) why = "the game's own clean-up was not found in its scene change: left alone";
            }
            catch (Exception ex) { why = "could not be put in: " + ex.Message; Debug.LogWarning("[Smooth Running] the scene changes were left as the game has them: " + ex); }
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            MethodInfo unload = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets));
            MethodInfo mine = AccessTools.Method(typeof(SceneChanges), nameof(Unload));
            foreach (CodeInstruction c in code)
            {
                if (c.Calls(unload)) { c.operand = mine; found++; }
                yield return c;
            }
        }

        /// <summary>In place of the game's own call (whose answer it throws away): nothing, as Unity has just done the same.</summary>
        static AsyncOperation Unload()
        {
            if (Running.scenes != null && !Running.scenes) return Resources.UnloadUnusedAssets();
            skipped++;
            return null;
        }

        public static string Said()
        {
            if (!patched) return "Scene changes: " + why;
            return "Scene changes: one clean-up fewer each" + (skipped > 0 ? "; " + skipped + " left out so far this game" : "") + ".";
        }
    }

    /// <summary>
    /// The game's event handlers that belong to things already gone, cleared at every scene change.
    ///
    /// Measured (2026-10-17, the test save, rounds of scene changes: space centre, assembly building, space centre, a flight):
    /// every round left about 420 more handlers on the game's events, 4,200 after the third, against 2,950 at the start. Of
    /// those 420, some 330 were the game's own records of which parts share resources (PartSet), two each: such a record adds
    /// itself to two events when it is made and takes itself off only in its finaliser, which never runs, because the events
    /// themselves keep it alive. So every one ever made stays, told of every change to a resource's flow, keeping the parts of
    /// craft destroyed long ago in memory for the collector to go through at every collection. Most of the rest belonged to
    /// destroyed things (a craft's network link, the staging list's groups, the editor's own logic): the game takes a handler
    /// off only when its owner is gone altogether, and a destroyed object is still something.
    ///
    /// Here, once each scene is ready, every one of the game's events is gone through: a handler is taken off if its owner is a
    /// destroyed object, or one of those records whose craft is destroyed or whose every part is. A record that is empty, or
    /// whose craft is being built in the editor with no parts yet, is left alone. Nothing else is touched.
    /// </summary>
    static class Handlers
    {
        static int cleared, sets, sweeps, lastSweep;
        static List<BaseGameEvent> all;
        static readonly Dictionary<Type, FieldInfo> listOf = new Dictionary<Type, FieldInfo>();
        static readonly Dictionary<Type, FieldInfo> ownerOf = new Dictionary<Type, FieldInfo>();
        static AccessTools.FieldRef<PartSet, Vessel> setVessel;
        static AccessTools.FieldRef<PartSet, ShipConstruct> setShip;
        static AccessTools.FieldRef<PartSet, HashSet<Part>> setParts;
        static AccessTools.FieldRef<PartSet, bool> setWide;
        static string why = "";

        /// <summary>
        /// What is told when a scene is ready. An object of its own, not a static method: the game takes a handler off its list
        /// the first time the event fires if the handler has no object behind it, and a static method has none.
        /// </summary>
        sealed class Listener
        {
            public void Loaded(GameScenes scene)
            {
                if (Running.handlers != null && !Running.handlers) return;
                Sweep();
            }
        }
        static Listener listener;

        public static void Hook()
        {
            // (a development build putting a new copy in: this copy's own listener goes first, if it had one)
            if (listener != null) GameEvents.onLevelWasLoadedGUIReady.Remove(listener.Loaded);
            listener = new Listener();
            GameEvents.onLevelWasLoadedGUIReady.Add(listener.Loaded);
        }

        /// <summary>Every event gone through once, and the handlers of things that are gone taken off. Says how many.</summary>
        public static string Sweep()
        {
            int here = 0, setsHere = 0;
            try
            {
                if (all == null)
                {
                    setVessel = AccessTools.FieldRefAccess<PartSet, Vessel>("vessel");
                    setShip = AccessTools.FieldRefAccess<PartSet, ShipConstruct>("ship");
                    setParts = AccessTools.FieldRefAccess<PartSet, HashSet<Part>>("targetParts");
                    setWide = AccessTools.FieldRefAccess<PartSet, bool>("vesselWide");
                    all = new List<BaseGameEvent>();
                    var types = new List<Type> { typeof(GameEvents) };
                    types.AddRange(typeof(GameEvents).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
                    foreach (Type t in types)
                        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            if (typeof(BaseGameEvent).IsAssignableFrom(f.FieldType) && f.GetValue(null) is BaseGameEvent e) all.Add(e);
                }
                foreach (BaseGameEvent e in all)
                {
                    if (!(ListOf(e.GetType())?.GetValue(e) is IList items)) continue;
                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        object d = items[i];
                        if (d == null) continue;
                        object who = OwnerOf(d.GetType())?.GetValue(d);
                        bool gone = false, isSet = false;
                        if (who is UnityEngine.Object u) gone = u == null;
                        else if (who is PartSet ps) gone = isSet = SetGone(ps);
                        if (!gone) continue;
                        items.RemoveAt(i);
                        here++;
                        if (isSet) setsHere++;
                    }
                }
                cleared += here; sets += setsHere; sweeps++; lastSweep = here;
                return here + " handlers of things that are gone taken off (" + setsHere + " of them part sets' own)";
            }
            catch (Exception ex)
            {
                why = ex.Message;
                Debug.LogWarning("[Smooth Running] the events' handlers could not be gone through: " + ex);
                return "could not: " + ex.Message;
            }
        }

        static FieldInfo ListOf(Type t)
        {
            if (listOf.TryGetValue(t, out FieldInfo f)) return f;
            for (Type k = t; k != null && f == null; k = k.BaseType) f = k.GetField("events", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null && !typeof(IList).IsAssignableFrom(f.FieldType)) f = null;
            return listOf[t] = f;
        }

        static FieldInfo OwnerOf(Type t)
        {
            if (ownerOf.TryGetValue(t, out FieldInfo f)) return f;
            return ownerOf[t] = t.GetField("originator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>Whether a record of parts sharing resources belongs to nothing that is left: its craft destroyed, or every one of its parts.</summary>
        static bool SetGone(PartSet set)
        {
            if (setWide(set))
            {
                Vessel v = setVessel(set);
                if (!ReferenceEquals(v, null)) return v == null;
                ShipConstruct ship = setShip(set);
                if (ship?.parts == null || ship.parts.Count == 0) return false;
                foreach (Part p in ship.parts) if (p != null) return false;
                return true;
            }
            HashSet<Part> parts = setParts(set);
            if (parts == null || parts.Count == 0) return false;
            foreach (Part p in parts) if (p != null) return false;
            return true;
        }

        public static string Said()
        {
            if (why != "") return "Handlers of things that are gone: " + why;
            if (Running.handlers != null && !Running.handlers) return "Handlers of things that are gone: left as the game has them.";
            return "Handlers of things that are gone: " + cleared + " taken off so far this game" + (sets > 0 ? " (" + sets + " of them the game's part sets)" : "") + (sweeps > 0 ? "; " + lastSweep + " at the last scene change." : ".");
        }
    }
}

