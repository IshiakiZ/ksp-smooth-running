using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;

namespace SmoothRunning
{
    /// <summary>
    /// Each frame drawn at its own moment. The game moves everything in steps of a fiftieth of a second, its physics, and draws
    /// whatever pose the last step left; frames do not come every fiftieth, so some frames carry two steps and some none. At 45
    /// frames a second the world on screen beats five times a second; at 60 it hitches ten times a second; at 120 it stands still in
    /// 70 frames of every 120, and the motion strobes at 50 a second however fast the game draws. (No part of the game is smoothed
    /// between steps: nothing sets a part's body to interpolate. Found from the decompiled game by the rendering research chat,
    /// research/rendering/notes/physics-beat.md, whose prototype, research/rendering/ingame/SmoothBeat, this is made from.)
    ///
    /// In both of the game's ways of moving a fast craft (through the scene, or the scene past it) the fault is the same: the craft
    /// and its camera go forward against the world in jumps. So just before the frame is drawn, the camera's pivot and
    /// the models of the craft's parts are carried on by the craft's velocity against the world times the time since the last step
    /// (and turned by its turning, about its centre of mass), and put back after the near camera has drawn, and again before every
    /// physics step and every Update. The craft stays still against its camera, as before, and the world flows evenly. Carried on,
    /// not held back: at a steady speed it is exact; under thrust it misses by half the acceleration times the time squared, under
    /// a millimetre at ten g, with no delay added (holding the last two steps and drawing between them would be exact and a step
    /// late). Physics never sees anything moved: only the parts' models are, never the parts themselves, and the camera's pivot,
    /// which the game places again every frame.
    ///
    /// What was carried on is handed to the other mods (Keystone.Motion), for what they draw riding with the craft.
    /// </summary>
    static class Motion
    {
        static bool hooked;
        static int appliedFrame = -1, stepsSince, steadyFrames;
        static Transform pivotMoved;
        static Vector3 pivotWas;
        static Quaternion pivotTurnWas;
        static readonly List<Transform> moved = new List<Transform>();
        static readonly List<Vector3> movedWas = new List<Vector3>();
        static readonly List<Quaternion> movedTurn = new List<Quaternion>();
        sealed class Held { public Transform[] Children; public int Count; }
        static readonly Dictionary<uint, Held> heldOf = new Dictionary<uint, Held>();
        static float lastAhead;

        /// <summary>
        /// What is told when a scene is loaded. An object of its own, not a static method: the game's events read the object behind
        /// each handler as it is added, and adding a static method, which has none, throws (it did, and the stages below were never
        /// hooked: the models were put back only after drawing).
        /// </summary>
        sealed class Listener { public void Loaded(GameScenes scene) => Scene(scene); }
        static Listener listener;

        internal static void Hook()
        {
#if DEV
            // (a development build swapping a new copy in: what the old copies hooked goes first, or each carries the craft on again
            // from where the last left it, and puts it back there)
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a == typeof(Motion).Assembly) continue;
                try { a.GetType("SmoothRunning.Motion", false)?.GetMethod("Unhook", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null); }
                catch (Exception) { }
            }
#endif
            if (hooked) return;
            hooked = true;
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            listener = new Listener();
            GameEvents.onLevelWasLoaded.Add(listener.Loaded);
            Scene(HighLogic.LoadedScene);
        }

        /// <summary>Everything this copy put into the game taken out again (a development build swapping a new copy in).</summary>
        static void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            PutBack();
            Camera.onPreCull -= PreCull;
            Camera.onPostRender -= PostRender;
            if (listener != null) { GameEvents.onLevelWasLoaded.Remove(listener.Loaded); listener = null; }
            Stages(false);
        }

        /// <summary>At every scene: the game's own stages hooked anew where there is a flight (their list is the scene's).</summary>
        static void Scene(GameScenes scene)
        {
            PutBack();
            heldOf.Clear();
            if (scene == GameScenes.FLIGHT) Stages(true);
        }

        static void Stages(bool on)
        {
            try
            {
                TimingManager.FixedUpdateRemove(TimingManager.TimingStage.ObscenelyEarly, OnFixed);
                TimingManager.UpdateRemove(TimingManager.TimingStage.ObscenelyEarly, OnUpdate);
                TimingManager.LateUpdateRemove(TimingManager.TimingStage.BetterLateThanNever, OnLateLast);
                if (!on) return;
                TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, OnFixed);
                TimingManager.UpdateAdd(TimingManager.TimingStage.ObscenelyEarly, OnUpdate);
                TimingManager.LateUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, OnLateLast);
            }
            catch (Exception ex)
            {
                if (!on) return;
                Kit.Log("Smooth Running", "could not hook the game's stages (" + ex.Message + "): frames are drawn as the game has them");
                Running.motionBroken = true;
            }
        }

        static void OnFixed()
        {
            PutBack(); stepsSince++;
#if DEV
            fixedCalls++;
#endif
#if DEV
            if (physFrom < 0) physFrom = Now();
#endif
        }

        static void OnUpdate()
        {
            PutBack();
#if DEV
            double t = Now();
            if (physFrom >= 0) { physMs += t - physFrom; physFrom = -1; }
            scriptsFrom = t;
#endif
        }

#if DEV
        // (where the processor's time goes in each frame, as the rendering chat's prototype splits it: physics, from the frame's
        // first step to the first Update; the scripts' Update and LateUpdate; drawing, from the first camera's culling to the near
        // camera's end)
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static double physFrom = -1, physMs, scriptsFrom = -1, scriptsMs, drawFrom = -1, drawMs;
        static int drawFrame = -1;
        static double Now() => clock.Elapsed.TotalMilliseconds;
        static int fixedCalls;
#endif

        /// <summary>The frame's scripts are through (the last of the game's stages): from here on its cameras draw it.</summary>
        static void OnLateLast()
        {
            lateDone = Time.frameCount;
#if DEV
            if (scriptsFrom >= 0) { scriptsMs += Now() - scriptsFrom; scriptsFrom = -1; }
#endif
        }
        static int lateDone = -1;

        static void PreCull(Camera cam)
        {
#if DEV
            if (drawFrame != Time.frameCount) { drawFrame = Time.frameCount; drawFrom = Now(); }
#endif
            // (carried before the first camera that draws the frame to the screen, once the frame's scripts are through: the explosions
            // mod lays its patches then, by the craft as drawn; not before a camera drawn into a picture of its own, as a kerbal's
            // portrait is, which can come in the middle of the frame, before the game's camera has been placed)
            if (appliedFrame == Time.frameCount || cam.targetTexture != null || lateDone != Time.frameCount) return;
            appliedFrame = Time.frameCount;
            if (Running.motion == null || !Running.motion || Running.motionBroken || HighLogic.LoadedScene != GameScenes.FLIGHT) return;
            if (FlightDriver.Pause || Time.timeScale <= 0f || TimeWarp.CurrentRate > 1f) return;
            if (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight) return;
            float since = Time.time - Time.fixedTime;
            if (since <= 0f || since > 1.5f * Time.fixedDeltaTime) return;
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null) return;
#if DEV
            long carryFrom = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            Vessel followed = fc.vesselTarget != null ? fc.vesselTarget : fc.partTarget != null ? fc.partTarget.vessel : FlightGlobals.ActiveVessel;
            // (every craft in physics is carried on by its own velocity against the world, which stands still between steps. If the
            // craft the camera follows has no models to carry, as a kerbal on foot has not, everything is carried against it instead:
            // the other craft move evenly beside it, and the world goes as the game has it)
            Vector3 against = followed != null && !followed.packed && !HasModels(followed) ? (Vector3)followed.velocityD : Vector3.zero;
            // (the camera's pivot as it stands before anything is carried: it may hang inside what is, as it does inside a kerbal on
            // foot, and is then carried with it, not again)
            Transform pivot = fc.transform.parent;
            Vector3 pivotFrom = pivot != null ? pivot.position : Vector3.zero;
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            for (int k = 0; k < loaded.Count; k++)
            {
                Vessel v = loaded[k];
                if (v == null || v.packed || v.state == Vessel.State.DEAD || v.parts == null || v.parts.Count == 0) continue;
                // (the craft against the world, as the game reckons it at each step: its bodies' own velocity and the frame's)
                Vector3 o = ((Vector3)v.velocityD - against) * since;
                // (and its turning, about its centre of mass where its bodies are now: the game's own centre is reckoned before the
                // step, and before the world is moved to keep the craft flown near the middle of the scene)
                Quaternion r = Quaternion.identity;
                if (v.ReferenceTransform != null)
                {
                    // (the game keeps the angular velocity in the craft's own frame)
                    Vector3 w = v.ReferenceTransform.rotation * (Vector3)v.angularVelocityD;
                    float angle = w.magnitude * since * Mathf.Rad2Deg;
                    if (angle > 1e-4f) r = Quaternion.AngleAxis(angle, w.normalized);
                }
                // (a craft standing still, on the pad or landed, has nothing to be carried by)
                bool still = o.sqrMagnitude < 1e-6f && r == Quaternion.identity;
                Vector3 com = still ? (Vector3)v.CoMD : CentreOfMass(v);
                int was = moved.Count;
                for (int i = 0; i < v.parts.Count && !still; i++)
                {
                    Transform[] held = Carried(v.parts[i]);
                    for (int j = 0; j < held.Length; j++)
                    {
                        Transform m = held[j];
                        // (the camera's pivot is left to the pivot's own carrying below. Behind a kerbal on foot it hangs from the kerbal's
                        // part, and until 2026-10-25 it was carried as one of the part's models, turn and all, in every view: on the PC a
                        // kerbal holding a plane's ladder, whose body the game gives a turning it never makes, swung the picture over
                        // for a frame six times a second, as the frames and the steps fell out of step)
                        if (m == null || m == pivot) continue;
                        Vector3 at = m.position;
                        Quaternion turn = m.rotation;
                        moved.Add(m); movedWas.Add(at); movedTurn.Add(turn);
                        m.SetPositionAndRotation(com + r * (at - com) + o, r * turn);
                    }
                }
                if (moved.Count == was && !still) continue;
                Keystone.Motion.Set(v, o, r, com, since, Time.frameCount);
                if (v == FlightGlobals.ActiveVessel) lastAhead = o.magnitude;
                if (v != followed || Keystone.Motion.CameraKept || pivot == null || pivot.position != pivotFrom) continue;
                pivotMoved = pivot; pivotWas = pivot.position; pivotTurnWas = pivot.rotation;
                // (turned with the craft only where the camera turns with it, the locked view; the chase view follows the way the craft
                // goes, not the way it points, and turned with a spinning craft's spin it judders (measured: 0.28 to 1.5 at 229 deg/s))
                bool withCraft = fc.mode == FlightCamera.Modes.LOCKED;
                if (withCraft) { pivot.position = com + r * (pivotWas - com) + o; pivot.rotation = r * pivotTurnWas; }
                else pivot.position = pivotWas + o;
            }
            // (loose bodies that are not craft: fairing panels thrown off, the wreck's pieces; each by its own velocity against the world)
            Vector3 frameVelocity = Krakensbane.GetFrameVelocityV3f();
            List<physicalObject> loose = FlightGlobals.physicalObjects;
            for (int k = 0; k < loose.Count; k++)
            {
                physicalObject po = loose[k];
                if (po == null || po.rb == null) continue;
                Transform m = po.transform;
                moved.Add(m); movedWas.Add(m.position); movedTurn.Add(m.rotation);
                m.position += (po.rb.velocity + frameVelocity - against) * since;
            }
            // (what other mods hang on the moving frame, moved every frame by them: by the frame's own going on)
            Vector3 frameAhead = (frameVelocity - against) * since;
            Keystone.Motion.SetFrame(frameAhead, since, Time.frameCount);
            List<Transform> riders = Keystone.Motion.FrameRiders;
            for (int k = 0; k < riders.Count; k++)
            {
                Transform m = riders[k];
                moved.Add(m); movedWas.Add(m.position); movedTurn.Add(m.rotation);
                m.position += frameAhead;
            }
            steadyFrames++;
#if DEV
            carryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - carryFrom; carryFrames++; carriedCount = moved.Count;
#endif
        }

        static Vector3 CentreOfMass(Vessel v)
        {
            Vector3 sum = Vector3.zero;
            float mass = 0f;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Rigidbody rb = v.parts[i].rb;
                if (rb == null) continue;
                float m = rb.mass;
                sum += rb.worldCenterOfMass * m; mass += m;
            }
            return mass > 0f ? sum / mass : (Vector3)v.CoMD;
        }

        static bool HasModels(Vessel v)
        {
            for (int i = 0; i < v.parts.Count; i++) if (Carried(v.parts[i]).Length > 0) return true;
            return false;
        }

        static void PostRender(Camera cam)
        {
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null || cam != fc.mainCamera) return;
#if DEV
            if (drawFrom >= 0) { drawMs = Now() - drawFrom; drawFrom = -1; }
            Measure(cam);
#endif
            PutBack();
        }

#if DEV
        // ---- (development build) the judder index, as research/rendering/ingame/SmoothBeat/judder.py reckons it: each frame, how
        // many physics steps came since the last, and where a place fixed on the planet falls in the picture as drawn
        static bool measuring, marked;
        static double markLat, markLon;
        static int measuredFrame = -1;
        static readonly List<float> dts = new List<float>(), sxs = new List<float>(), sys = new List<float>();
        static readonly List<int> steps = new List<int>();
        static readonly List<float> phys = new List<float>(), scripts = new List<float>(), draws = new List<float>();
        static readonly List<float> sinces = new List<float>(), aheads = new List<float>(), times = new List<float>(), sxw = new List<float>(), syw = new List<float>(), dists = new List<float>();
        static readonly List<float> gapx = new List<float>(), gapy = new List<float>();
        static float Median(List<float> v) { var c = new List<float>(v); c.Sort(); return c.Count > 0 ? c[c.Count / 2] : 0f; }
        static int vsyncWas = int.MinValue, fpsWas;

        static Transform Drawn(Part p) { Transform[] h = Carried(p); return h.Length > 0 && h[0] != null ? h[0] : p.transform; }

        // (frame by frame, for hitches: how long each frame was and whether a collection ended in it; "hitch" begins, "hitch?" reads)
        static bool hitchOn; static int hitchFrame, hitchCollections;
        static readonly List<float> hitchDts = new List<float>(); static readonly List<int> hitchGc = new List<int>();
        static void Hitch()
        {
            if (!hitchOn || Time.frameCount == hitchFrame) return;
            hitchFrame = Time.frameCount;
            int c = GC.CollectionCount(0);
            hitchDts.Add(Time.unscaledDeltaTime * 1000f); hitchGc.Add(c - hitchCollections); hitchCollections = c;
        }
        static string HitchSay()
        {
            hitchOn = false;
            int n = hitchDts.Count;
            if (n < 2) return "no frames";
            var sorted = new List<float>(hitchDts); sorted.Sort();
            float sum = 0f; foreach (float d in hitchDts) sum += d;
            int over33 = 0, over50 = 0, over80 = 0, gcs = 0; var withGc = new List<string>();
            for (int i = 1; i < n; i++)
            {
                float d = hitchDts[i];
                if (d > 33f) over33++; if (d > 50f) over50++; if (d > 80f) over80++;
                if (hitchGc[i] > 0) { gcs += hitchGc[i]; withGc.Add(d.ToString("F0")); }
            }
            return n + " frames, mean " + (sum / n).ToString("F1") + " ms, median " + sorted[n / 2].ToString("F1") + ", 99th " + sorted[(int)(n * 0.99)].ToString("F1") + ", worst " + sorted[n - 1].ToString("F1")
                + "; over 33 ms " + over33 + ", over 50 " + over50 + ", over 80 " + over80 + "; " + gcs + " collections ended, in frames of " + string.Join(", ", withGc.ToArray()) + " ms"
                + "; slice " + (UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds / 1e6).ToString("F1") + " ms";
        }

        static bool garbageOn; static int garbageFrames; static long garbageBytes, garbageLast;
        static void Garbage()
        {
            if (!garbageOn) return;
            long now = GC.GetTotalMemory(false);
            if (now > garbageLast) garbageBytes += now - garbageLast;
            garbageLast = now; garbageFrames++;
        }

        static void Measure(Camera cam)
        {
            Garbage(); Hitch();
            if (!measuring || measuredFrame == Time.frameCount) return;
            measuredFrame = Time.frameCount;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            CelestialBody body = v.mainBody;
            if (!marked)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                Vector3d o = ray.origin, d = ray.direction, c = body.position;
                double b = Vector3d.Dot(o - c, d), q = (o - c).sqrMagnitude - body.Radius * body.Radius, disc = b * b - q;
                if (disc < 0.0) return;
                Vector3d hit = o + d * (-b - Math.Sqrt(disc));
                markLat = body.GetLatitude(hit); markLon = body.GetLongitude(hit);
                marked = true; stepsSince = 0;
                return;
            }
            Vector3 sp = cam.WorldToScreenPoint((Vector3)body.GetWorldSurfacePosition(markLat, markLon, 0.0));
            dts.Add(Time.unscaledDeltaTime); steps.Add(stepsSince); sxs.Add(sp.x); sys.Add(sp.y);
            sinces.Add(Time.time - Time.fixedTime); aheads.Add(Keystone.Motion.Now ? Keystone.Motion.Ahead.magnitude : 0f); times.Add(Time.time);
            Vector3 w = cam.WorldToScreenPoint((Vector3)v.CoMD); sxw.Add(w.x); syw.Add(w.y); dists.Add(Vector3.Distance(cam.transform.position, (Vector3)body.GetWorldSurfacePosition(markLat, markLon, 0.0)));
            // (the nearest other craft in physics, as drawn, against the craft flown as drawn: on the screen, the gap between them)
            Vessel other = null; float best = float.MaxValue;
            foreach (Vessel u in FlightGlobals.VesselsLoaded)
            {
                if (u == null || u == v || u.packed || u.rootPart == null) continue;
                float d = (u.transform.position - v.transform.position).sqrMagnitude;
                if (d < best) { best = d; other = u; }
            }
            Transform mine = v.rootPart != null ? Drawn(v.rootPart) : null, theirs = other != null ? Drawn(other.rootPart) : null;
            if (mine != null && theirs != null) { Vector3 a1 = cam.WorldToScreenPoint(mine.position), b1 = cam.WorldToScreenPoint(theirs.position); gapx.Add(b1.x - a1.x); gapy.Add(b1.y - a1.y); }
            else { gapx.Add(0f); gapy.Add(0f); }
            phys.Add((float)physMs); scripts.Add((float)scriptsMs); draws.Add((float)drawMs);
            stepsSince = 0; physMs = 0; scriptsMs = 0;
        }

        /// <summary>(Development build: "start" measures from now; "stop" says the judder index since; "fps N" holds the frame rate at N, vsync off, "fps 0" as it was.)</summary>
        public static string Judder(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a.StartsWith("fps "))
            {
                int n = int.Parse(a.Substring(4).Trim());
                if (vsyncWas == int.MinValue) { vsyncWas = QualitySettings.vSyncCount; fpsWas = Application.targetFrameRate; }
                if (n > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = n; }
                else { QualitySettings.vSyncCount = vsyncWas; Application.targetFrameRate = fpsWas; vsyncWas = int.MinValue; }
                return "frame rate " + (n > 0 ? "held at " + n : "as it was") + " (vsync " + QualitySettings.vSyncCount + ", limit " + Application.targetFrameRate + ")";
            }
            if (a == "craft")
            {
                var b = new System.Text.StringBuilder();
                foreach (Vessel u in FlightGlobals.VesselsLoaded)
                {
                    if (u == null) continue;
                    int withModel = 0; foreach (Part p in u.parts) if (p != null && Carried(p).Length > 0) withModel++;
                    b.Append(u.vesselName).Append(u == FlightGlobals.ActiveVessel ? " (flown)" : "").Append(": ").Append(u.parts.Count).Append(" parts, ").Append(withModel).Append(" with models, packed ").Append(u.packed)
                     .Append(", EVA ").Append(u.isEVA).Append(", speed ").Append(u.velocityD.magnitude.ToString("F1")).Append(", frame ").Append(Krakensbane.GetFrameVelocity().magnitude.ToString("F1"));
                    if (u.rootPart != null) foreach (Part p in u.parts) { b.Append("\n   ").Append(p.partInfo != null ? p.partInfo.name : p.name).Append(':'); foreach (Transform c in p.transform) b.Append(' ').Append(c.name).Append(c.GetComponent<Part>() != null ? "(part)" : ""); }
                    b.Append('\n');
                }
                b.Append("the camera's pivot:"); for (Transform c = FlightCamera.fetch != null ? FlightCamera.fetch.transform.parent : null; c != null; c = c.parent) b.Append(' ').Append(c.name).Append(" <");
                b.Append('\n');
                b.Append("Krakensbane: frame ").Append(Krakensbane.GetFrameVelocity().magnitude.ToString("F1")).Append(", MaxV ").Append(Math.Sqrt(Krakensbane.SqrThreshold).ToString("F1")).Append(", physics syncs transforms itself ").Append(Physics.autoSyncTransforms);
                return b.ToString();
            }
            if (a == "cost")
            {
                double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
                string r = carryFrames + " frames carried, " + carriedCount + " things each: carrying " + (carryFrames > 0 ? carryTicks / f / carryFrames : 0).ToString("F3") + " ms a frame, putting back " + (carryFrames > 0 ? backTicks / f / carryFrames : 0).ToString("F3") + " ms";
                carryTicks = backTicks = 0; carryFrames = 0;
                return r;
            }
            if (a.StartsWith("vsync "))
            {
                if (vsyncWas == int.MinValue) { vsyncWas = QualitySettings.vSyncCount; fpsWas = Application.targetFrameRate; }
                QualitySettings.vSyncCount = int.Parse(a.Substring(6).Trim());
                return "vsync " + QualitySettings.vSyncCount + " (every " + QualitySettings.vSyncCount + " refreshes of " + Screen.currentResolution.refreshRate + " Hz)";
            }
            if (a == "garbage")
            {
                // (how much the managed heap grows from frame to frame, the collections left out: what the frame makes for the collector)
                garbageFrames = 0; garbageBytes = 0; garbageLast = GC.GetTotalMemory(false); garbageOn = true;
                return "measuring the garbage";
            }
            if (a == "heap")
                return "managed heap in use " + (GC.GetTotalMemory(false) / 1048576.0).ToString("F0") + " MB; Unity's heap " + (UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576.0).ToString("F0") + " MB, used " + (UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576.0).ToString("F0") + " MB; " + GC.CollectionCount(0) + " collections so far";
            if (a == "hitch") { hitchDts.Clear(); hitchGc.Clear(); hitchCollections = GC.CollectionCount(0); hitchFrame = Time.frameCount; hitchOn = true; return "timing every frame"; }
            if (a == "hitch?") return HitchSay();
            if (a.StartsWith("slice ")) { UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(double.Parse(a.Substring(6).Trim(), System.Globalization.CultureInfo.InvariantCulture) * 1e6); return "the collector's slice " + (UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds / 1e6).ToString("F1") + " ms a frame"; }
            if (a == "gcmode")
                return "incremental " + UnityEngine.Scripting.GarbageCollector.isIncremental + ", mode " + UnityEngine.Scripting.GarbageCollector.GCMode + ", slice " + UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds + " ns; " + GC.CollectionCount(0) + " collections so far; Unity " + Application.unityVersion;
            if (a == "gc")
            {
                // (one collection made now, timed: what each the game makes by itself costs, as long as nothing is left to finalise)
                var watch = System.Diagnostics.Stopwatch.StartNew();
                GC.Collect();
                double ms = watch.Elapsed.TotalMilliseconds;
                return "a collection took " + ms.ToString("F1") + " ms; heap in use now " + (GC.GetTotalMemory(false) / 1048576.0).ToString("F0") + " MB";
            }
            if (a == "garbage?")
            {
                garbageOn = false;
                return garbageFrames + " frames: " + (garbageFrames > 0 ? garbageBytes / garbageFrames / 1024.0 : 0).ToString("F1") + " KB a frame made (" + GC.CollectionCount(0) + " collections so far)";
            }
            if (a == "screen") return "screen " + Screen.currentResolution.width + "x" + Screen.currentResolution.height + " at " + Screen.currentResolution.refreshRate + " Hz; vsync " + QualitySettings.vSyncCount + ", limit " + Application.targetFrameRate + ", max delta " + Time.maximumDeltaTime.ToString("F3") + ", fixed " + Time.fixedDeltaTime.ToString("F3");
            if (a == "state") return "hooked " + hooked + ", physics steps seen " + fixedCalls + ", broken " + Running.motionBroken + ", on " + (Running.motion != null && Running.motion) + ", this copy " + typeof(Motion).Assembly.GetName().Name;
            if (a == "dump")
            {
                var b = new System.Text.StringBuilder("dt,steps,since,ahead,time,sx,sy,cx,cy,dist,gx,gy\n");
                for (int i = 0; i < dts.Count; i++)
                    b.Append(dts[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(steps[i]).Append(',').Append(sinces[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(aheads[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(times[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                     .Append(sxs[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(sys[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(sxw[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(syw[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(dists[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                     .Append(gapx[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(gapy[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".ksp-ai-bridge/judder.csv");
                System.IO.File.WriteAllText(path, b.ToString());
                return dts.Count + " frames written to " + path;
            }
            if (a == "start") { gapx.Clear(); gapy.Clear(); sinces.Clear(); aheads.Clear(); times.Clear(); sxw.Clear(); syw.Clear(); dists.Clear(); dts.Clear(); sxs.Clear(); sys.Clear(); steps.Clear(); phys.Clear(); scripts.Clear(); draws.Clear(); marked = false; measuring = true; return "measuring"; }
            measuring = false;
            int n2 = dts.Count;
            if (n2 < 40) return "too few frames (" + n2 + ")";
            var speed = new List<double>();
            for (int i = 1; i < n2; i++) speed.Add(Math.Sqrt((sxs[i] - sxs[i - 1]) * (sxs[i] - sxs[i - 1]) + (sys[i] - sys[i - 1]) * (sys[i] - sys[i - 1])) / Math.Max(dts[i], 1e-4));
            const int half = 8;
            double devSum = 0, meanSum = 0; int count = 0;
            for (int i = half; i < speed.Count - half; i++)
            {
                double m = 0;
                for (int k = i - half; k <= i + half; k++) m += speed[k];
                m /= 2 * half + 1;
                devSum += (speed[i] - m) * (speed[i] - m); meanSum += m; count++;
            }
            double total = 0; foreach (float d in dts) total += d;
            int none = 0, many = 0; foreach (int k in steps) { if (k == 0) none++; if (k >= 2) many++; }
            double index = count > 0 && meanSum > 0 ? Math.Sqrt(devSum / count) / (meanSum / count) : double.NaN;
            return n2 + " frames, " + (n2 / total).ToString("F0") + " a second; after no step " + (100.0 * none / n2).ToString("F0") + "%, after two or more " + (100.0 * many / n2).ToString("F0") + "%; screen speed " + (meanSum / Math.Max(count, 1)).ToString("F0") + " px/s; judder index " + index.ToString("F3")
                 + "; the processor a frame (medians): physics " + Median(phys).ToString("F1") + " ms, scripts " + Median(scripts).ToString("F1") + " ms, drawing " + Median(draws).ToString("F1") + " ms, the whole frame " + (1000f * Median(new List<float>(dts))).ToString("F1") + " ms";
        }
#endif

#if DEV
        static long carryTicks, backTicks; static int carryFrames, carriedCount;
#endif
        static void PutBack()
        {
#if DEV
            long backFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            bool any = moved.Count > 0;
#endif
            if (pivotMoved != null) { pivotMoved.position = pivotWas; pivotMoved.rotation = pivotTurnWas; pivotMoved = null; }
            for (int i = 0; i < moved.Count; i++) if (moved[i] != null) moved[i].SetPositionAndRotation(movedWas[i], movedTurn[i]);
            moved.Clear(); movedWas.Clear(); movedTurn.Clear();
            Keystone.Motion.Clear();
#if DEV
            if (any) backTicks += System.Diagnostics.Stopwatch.GetTimestamp() - backFrom;
#endif
        }

        /// <summary>
        /// What of a part is drawn and moved with it: every child of the part's own object but other parts, the part's own body
        /// staying where physics has it. Its model, and with it what hangs from that (the game's effects at its nozzles), but also
        /// what does not: an engine's shroud on a decoupler, the game's effects put beside the model, all of a kerbal on foot (whose
        /// body and bones are not in his model). Looked up again when the part's children change.
        /// </summary>
        static Transform[] Carried(Part p)
        {
            Transform t = p.transform;
            int n = t.childCount;
            if (heldOf.TryGetValue(p.flightID, out Held h) && h.Count == n) return h.Children;
            var list = new List<Transform>(n);
            for (int i = 0; i < n; i++) { Transform c = t.GetChild(i); if (c.GetComponent<Part>() == null) list.Add(c); }
            heldOf[p.flightID] = h = new Held { Children = list.ToArray(), Count = n };
            return h.Children;
        }

        internal static string Said()
        {
            if (Running.motion == null || !Running.motion) return "Each frame at its own moment: off.";
            return "Each frame at its own moment: " + (steadyFrames > 0 ? "the craft and the camera carried on between the game's steps (the craft flown " + lastAhead.ToString("F2") + " m on the last frame)." : "waiting for a flight.");
        }
    }
}
