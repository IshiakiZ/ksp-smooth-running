using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Keystone;
using UnityEngine;

namespace SmoothRunning
{
    /// <summary>
    /// The game's code that a game's first lift-off runs for the first time, compiled before it is needed.
    ///
    /// Measured (2026-10-18, in fresh games, with Mono's own record of what it compiles: tools/native/jitlog.c): the runtime
    /// compiles each method the first time it is called, and a game's first lift-off calls some 140 of the game's own methods
    /// for the first time (staging, an engine's first steps and its effects, the staging list's gauges, the flight's log): 11
    /// ms of compiling on the game's thread, most of it in the two frames that were already the slowest of the flight. What
    /// is compiled is kept for the rest of the game.
    ///
    /// Here, a few seconds into the first flight scene, those methods are compiled ahead, a millisecond and a half's worth a
    /// frame. Only methods of classes the game is already using by then are on the list (compiling a method readies its
    /// class, and a class not yet in use could have its own set-up run early).
    ///
    /// And this workspace's own mods' (from the same record, a lift-off's first second and a half: 21 ms more, the smoke's,
    /// the flames', the lamps', the crash watch's), wherever those mods are installed: a mod that is not is passed over. Their
    /// classes set up only plain values and look-ups, so readying them early changes nothing but when the work is done.
    /// </summary>
    static class Ahead
    {
        // (from that record: each class as the runtime names it, a class inside another after a "/", and its methods)
        static readonly string[] Lift =
        {
            "AudioFX: OnEvent",
            "CollisionEventsHandler: OnCollisionExit",
            "CoroutineHost/<RunAndDispose>d__3: MoveNext System.Collections.IEnumerator.get_Current",
            "CoroutineHost: .ctor Create RunAndDispose StartCoroutine Terminate",
            "DeltaVEngineInfo: RequiresAir",
            "DestructibleBuilding: OnCollisionExit",
            "EffectBehaviour: OnEvent",
            "EffectList: Event",
            "EventReport: .ctor",
            "FXGroup/<VolumeFade>d__31: MoveNext",
            "FXGroup: Burst SetPower VolumeFade setActiveWithAudioFade set_Power",
            "FlightGlobals: GetSqrAltitude getExternalTemperature",
            "FlightLog: AddEntryUnique HasEntry",
            "FlightLogger: LogEvent get_LiftOff get_missionTimestamp onLaunch",
            "GameVariables: EVAIsPossible UnlockedEVA",
            "KSP.UI.Screens.ProtoStageIcon: DisplayInfo",
            "KSP.UI.Screens.ProtoStageIconInfo: .ctor SetMessage SetMsgBgColor SetMsgTextColor SetProgressBarBgColor SetProgressBarColor SetValue StartInfoBox",
            "KSP.UI.Screens.StageIcon: DisplayInfo ExpandGroupInUIOnly",
            "KSP.UI.Screens.StageIconInfoBox: Awake Expand SetCaption SetMessage SetMsgBgColor SetMsgTextColor SetProgressBarBgColor SetProgressBarColor SetValue",
            "KSP.UI.Screens.StageManager/<cooldownSeparation>d__110: MoveNext System.Collections.IEnumerator.get_Current",
            "KSP.UI.Screens.StageManager: ActivateNextStage ActivateStage cooldownSeparation",
            "KSP.UI.UIExtensions: Lock",
            "KSPAchievements.FirstLaunch/<TestFlight>d__2: MoveNext System.Collections.IEnumerator.get_Current",
            "KSPAchievements.FirstLaunch: TestFlight",
            "KSPUtil/DefaultDateTimeFormatter: PrintTimeStamp",
            "KSPUtil: PrintTimeStamp",
            "Kerbal: CheckEVAUnlocked",
            "LaunchPadFX: AddFX",
            "ModuleEngines: Activate ActivateRunningFX CalculateThrust CheckDeprived EngineExhaustDamage FXReset ModifyFlow OnActive PlayEngageFX RequestPropellant RequiredPropellantMass UnFlameout UpdatePropellantGauge UpdatePropellantStatus get_throttleSetting",
            "ModuleEnginesFX: FXReset PlayEngageFX",
            "ModuleGimbal: ApplyControlAdjustments GimbalRotation OnActive",
            "ModuleJettison: OnActive",
            "ModuleResourceHandler: UpdateModuleResourceOutputs",
            "ModuleTestSubject: OnActive RunTest",
            "ModuleTripLogger: InvokeLogVessel LogNewSituation LogSituation LogVesselSituation",
            "Part: AddThermalFlux CheckOtherGroundContact Effect InternalFixedUpdate ModulesOnActivate ModulesOnFixedUpdate OnCollisionExit RequestResource force_activate get_isControllable onActiveFixedUpdate onActiveUpdate onPartActivate",
            "PartModule: OnActive OnFixedUpdate ResetWheelGroundCheck",
            "PhysicsGlobals: get_InternalHeatProductionFactor get_StagingCooldownTimer",
            "Propellant: UpdateConnectedResources get_totalResourceAvailable",
            "ProtoCrewMember: UpdateExperience",
            "ScenarioAchievements: CheckAchievement OnLaunch",
            "TimeWarp: setMode",
            "Vessel: IsClearToSave RequestResource UpdateResourceSetsIfDirty",
            "VesselDeltaV: OnEngineActiveChange OnStageActivate ProcessActiveUpdateEngines",
            "VesselValues/<>c: <.ctor>b__0_2 <.ctor>b__0_3 <.ctor>b__0_4 <.ctor>b__0_5 <.ctor>b__0_6 <.ctor>b__0_7",
        };

        // (the mods', likewise; found in whichever library holds them)
        static readonly string[] Mods =
        {
            "VolumetricExplosions.Site: Balanced Begin Craft Ends Feeding Feeds Has Laying Moves Passed TopUp Track Trench TurnsOnly Wants YoungFar get_HandFar get_Leaving",
            "VolumetricExplosions.Air: TakeSmoke get_SmokeQuality",
            "VolumetricExplosions.Pad: Feed",
            "VolumetricExplosions.Pads: Overlap",
            "VolumetricExplosions.Sending: Put",
            "RocketSmoke.Trails: Frozen HideTheirs Measure",
            "RocketSmoke.Feed: Burning",
            "EngineFlames.Flames: HideOthers",
            "EngineFlames.Feed: Flame get_KnowsFuels",
            "Keystone.Engines: Flame",
            "Keystone.Water: HeightAt",
            "NaturalLight.EngineLamps: Hang TintOf",
            "NaturalWeather.Bridge: Wind",
            "NaturalWeather.Aero: Pushing",
            "NaturalOcean.Ocean/SeaWater: HeightAt",
            "Fragmentation.Crashes: BodyOf Note Remember Step Water",
            "Fragmentation.Water: Depth Height Keystones",
            "MissionMusic.Watch: At Km Moons Sting",
            "SmoothRunning.Motion: CentreOfMass",
            "SmoothRunning.Blending/Craft: Give",
        };

        static readonly List<MethodBase> queue = new List<MethodBase>();
        static int next, compiled, failed, missing;
        static double spent;
        static bool listed, done;
        static string why = "";

        /// <summary>(Each frame of a flight scene.)</summary>
        internal static void Frame()
        {
            if (done || Running.ahead == null || !Running.ahead) return;
            if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || Time.timeSinceLevelLoad < 4f) return;
            long from = Stopwatch.GetTimestamp();
            try
            {
                if (!listed) { listed = true; List(); }
                while (next < queue.Count && Ms(from) < 1.5)
                {
                    try { queue[next].MethodHandle.GetFunctionPointer(); compiled++; }
                    catch (Exception) { failed++; }
                    next++;
                }
            }
            catch (Exception ex) { why = "stopped: " + ex.Message; next = queue.Count; }
            spent += Ms(from);
            if (next >= queue.Count)
            {
                done = true;
                queue.Clear();
                Kit.Log("Smooth Running", "the first lift-off's code made ready: " + compiled + " methods compiled (the game's and the mods') in " + spent.ToString("F1") + " ms" + (missing + failed > 0 ? " (" + missing + " not found, " + failed + " could not be)" : ""));
            }
        }

        static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

        /// <summary>The methods on the list, found.</summary>
        static void List()
        {
            Assembly game = typeof(Part).Assembly;
            foreach (string line in Lift) Add(line, name => game.GetType(name, false), true);
            foreach (string line in Mods) Add(line, Mod, false);
            // (and a few of the game's own generic classes, as used for these kinds: each kind of value is compiled apart)
            Add(typeof(EventData<int>), "Fire");
            Add(typeof(DictionaryValueList<Collider, int>), "Remove", "get_Item", "set_Item");
            Add(typeof(DictionaryValueList<int, DeltaVEngineBurnTotals>), "Remove");
            Add(typeof(VesselValues.PartValuesComparison<float>), "get_value");
        }

        /// <summary>A line of the lists: a class (a class inside another after a "/") and its methods, put in line to be compiled.</summary>
        static void Add(string line, Func<string, Type> find, bool counted)
        {
            int colon = line.IndexOf(':');
            string[] path = line.Substring(0, colon).Split('/');
            Type t = find(path[0]);
            for (int k = 1; t != null && k < path.Length; k++) t = t.GetNestedType(path[k], BindingFlags.Public | BindingFlags.NonPublic);
            string[] names = line.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t == null) { if (counted) missing += names.Length; return; }
            Add(t, names);
        }

        /// <summary>A mod's class, by its whole name: in this library first (the development build holds every mod in one), then in each the game loaded.</summary>
        static Type Mod(string name)
        {
            Type t = typeof(Ahead).Assembly.GetType(name, false);
            if (t != null) return t;
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                try { t = a.assembly != null ? a.assembly.GetType(name, false) : null; }
                catch (Exception) { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        static void Add(Type t, params string[] names)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (string name in names)
            {
                bool found = false;
                if (name == ".ctor")
                    foreach (ConstructorInfo c in t.GetConstructors(all)) { queue.Add(c); found = true; }
                else
                    foreach (MethodInfo m in t.GetMethods(all))
                        if (m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters) { queue.Add(m); found = true; }
                if (!found) missing++;
            }
        }

        public static string Said()
        {
            if (Running.ahead != null && !Running.ahead) return "The first lift-off's code: compiled as it is first needed (off).";
            if (!done) return "The first lift-off's code: to be made ready a few seconds into the first flight." + (why.Length > 0 ? " " + why : "");
            return "The first lift-off's code: " + compiled + " methods (the game's and the mods') made ready ahead, in " + spent.ToString("F1") + " ms spread over frames" + (why.Length > 0 ? "; " + why : "") + ".";
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AheadInFlight : MonoBehaviour
    {
        void Update() => Ahead.Frame();
    }
}
