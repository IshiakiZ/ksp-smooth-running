using System;
using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

namespace SmoothRunning
{
    /// <summary>
    /// Frame blending (the owner, 2026-10-11: "If your computer runs at, say, 10 FPS, it'll blend the frames together as well
    /// as it can"; 2026-10-17: "I want the frame blending to be even better"). The game cannot be made to show frames it does
    /// not run: every frame shown is a whole round of the game, and on a Mac the round itself, not the drawing, holds the
    /// frame (measured 2026-10-18). What makes a film at 24 frames a second look like motion is its shutter: each frame holds
    /// what passed while the shutter was open, smeared along the way it went. Here each frame is blended so, after the scene and
    /// its clouds and smoke are drawn and before the drops on the glass: each pixel along the way its point moved against the
    /// camera since the last frame, for the shutter's share of the frame. That way is reckoned from where the point stands (the
    /// near camera's depth picture; beyond it the planet's own round ground, or the sky) and from how the camera, and the craft
    /// it shows, moved against the planet: the ground streams past and blurs, the craft the camera follows moves with it and
    /// stays sharp, a turn of the view sweeps all of it. At sixty frames a second it is slight; at ten, long.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Blending : MonoBehaviour
    {
        static Shader shader;
        static bool tried;
        static string why = "";
        static Mesh sheet;
        static readonly int idScene = Shader.PropertyToID("_BlendScene");
        static readonly int idRight = Shader.PropertyToID("_BlendRight"), idUp = Shader.PropertyToID("_BlendUp"), idAhead = Shader.PropertyToID("_BlendAhead"),
            idPrevRight = Shader.PropertyToID("_BlendPrevRight"), idPrevUp = Shader.PropertyToID("_BlendPrevUp"), idPrevAhead = Shader.PropertyToID("_BlendPrevAhead"),
            idMoved = Shader.PropertyToID("_BlendMoved"), idCam = Shader.PropertyToID("_BlendCam"), idShutter = Shader.PropertyToID("_BlendShutter");

        Material material;
        CommandBuffer pass;
        Camera on;
        bool askedDepth;

        // (the camera as it stood at the last frame blended, against the planet: where, and which way it looked)
        bool had;
        int hadFrame = -10;
        CelestialBody hadBody;
        Vector3d camWas;
        QuaternionD lookWas;
        float tanXWas, tanYWas;
        readonly Craft first = new Craft("A"), second = new Craft("B");

        /// <summary>For telling the player: what it did in the last frame.</summary>
        internal static string Said = "";
#if DEV
        /// <summary>(Development build: what the frame blending did in the last frame.)</summary>
        public static string Says() => Said;
        // (Development build: what the blending did frame by frame, for the craft smeared in space (2026-10-19): Trace("frames") begins,
        // TraceRead() gives a line a frame: what it said, and the craft's words.)
        static readonly System.Text.StringBuilder trace = new System.Text.StringBuilder();
        static int traceLeft;
        static string traceCut = "";
        public static string Trace(string frames) { int n = 300; if (!string.IsNullOrEmpty(frames)) int.TryParse(frames.Trim(), out n); trace.Length = 0; traceLeft = Mathf.Clamp(n, 1, 20000); return "tracing the blending for " + traceLeft + " frames"; }
        public static string TraceRead() { traceLeft = 0; return trace.ToString(); }
        static void TraceLine(string craft)
        {
            if (traceLeft <= 0) return;
            traceLeft--;
            trace.Append(Time.frameCount).Append(' ').Append((Time.unscaledDeltaTime * 1000f).ToString("F1")).Append(" | ").Append(Said).Append(" | ").Append(craft).Append('\n');
        }
#endif

        void Awake() { Camera.onPreCull += BeforeCamera; }

        void OnDestroy()
        {
            Camera.onPreCull -= BeforeCamera;
            Off();
            if (pass != null) { pass.Release(); pass = null; }
            if (material != null) Destroy(material);
        }

        static void Load()
        {
            tried = true;
            try
            {
                // (OpenGL, the Mac's and Linux's: the hand-packed shader; Direct3D, Windows: the one built in the Unity editor from
                // tools/unityshaders, beside it where the release has it)
                string folder = KSPUtil.ApplicationRootPath + "GameData/SmoothRunning/PluginData/";
                string path = folder + (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore ? "blend.bundle" : "blend-windows.bundle");
                if (!System.IO.File.Exists(path)) why = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore ? "its shader's file is not there" : "no shader for " + SystemInfo.graphicsDeviceType + " in this build yet";
                else
                {
                    AssetBundle bundle = AssetBundle.LoadFromFile(path);
                    if (bundle != null)
                    {
                        foreach (Shader one in bundle.LoadAllAssets<Shader>()) if (one != null && one.name == "SmoothRunning/Blend" && one.isSupported) shader = one;
                        bundle.Unload(false);
                    }
                    if (shader == null) why = "its shader did not load";
                }
            }
            catch (Exception ex) { shader = null; why = "its shader did not load (" + ex.Message + ")"; }
            Kit.Log("Smooth Running", "frame blending: " + (shader != null ? "shader loaded" : "not drawn: " + why));
            if (sheet == null)
            {
                sheet = new Mesh { name = "Smooth Running: over the picture" };
                sheet.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
                sheet.triangles = new[] { 0, 1, 2 };
                sheet.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
        }

        void Off()
        {
            if (on != null)
            {
                if (pass != null) on.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, pass);
                if (askedDepth) on.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            on = null; askedDepth = false;
        }

        /// <summary>
        /// Its pass laid on the near flight camera after the transparent things (the clouds, the smoke), first among the passes
        /// there: the drops on the glass, laid on at the same point, ride with the camera and are not blended.
        /// </summary>
        void Attach(Camera cam)
        {
            Off();
            if (pass == null) pass = new CommandBuffer { name = "Smooth Running: frame blending" };
            CommandBuffer[] there = cam.GetCommandBuffers(CameraEvent.AfterForwardAlpha);
            foreach (CommandBuffer cb in there) cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, cb);
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, pass);
            foreach (CommandBuffer cb in there) if (cb != pass) cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, cb);
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0) { cam.depthTextureMode |= DepthTextureMode.Depth; askedDepth = true; }
            on = cam;
        }

        void BeforeCamera(Camera cam)
        {
            if (!Names.Is(cam, "Camera 00")) return;
            if (Running.blend == null || !Running.blend.Value || MapView.MapIsEnabled)
            {
                if (on != null) Off();
                had = false; Said = "off";
#if DEV
                TraceLine("-");
#endif
                return;
            }
            // (a lens already smearing what moves, as Cinema Camera's shutter does: the two together would smear twice)
            if (Lens.Smearing)
            {
                if (pass != null) pass.Clear();
                had = false; Said = "a lens's own shutter smears the picture (Cinema Camera's): frames not blended twice";
#if DEV
                TraceLine("-");
#endif
                return;
            }
            // (time warp on rails: a frame there jumps by minutes or days, which no shutter sees)
            if (TimeWarp.fetch != null && TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRateIndex > 0)
            {
                if (pass != null) pass.Clear();
                had = false; Said = "time warp: frames drawn as they are";
#if DEV
                TraceLine("-");
#endif
                return;
            }
            if (!tried) Load();
            if (shader == null) { Said = "not drawn: " + why; return; }
            if (material == null) material = new Material(shader) { name = "Smooth Running: frame blending" };
            if (on != cam) Attach(cam);
            pass.Clear();

            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) { had = false; return; }
            Transform t = cam.transform;
            // (the camera against the planet: its place and its turn in the planet's own frame, so that the game moving the
            // world under the craft, or its origin, or turning the planet, changes nothing)
            // (Its turning frame only while the game turns the world with it, near the ground, where it is the ground's own frame
            // (and does not turn in the scene); else a frame that does not turn, centred on it. Far from a world, its turning frame
            // goes fast (188 km/s at Duna's distance from the Sun) and the game turns it at its physics steps: there the camera in
            // it moved 115 m and 3900 m by turns, every other frame was taken for a cut, the craft was forgotten each time, and in
            // the frames between it was smeared into a streak (the owner's blurred rocket against black, 2026-10-19).)
            bool turning = FlightGlobals.RefFrameIsRotating;
            QuaternionD planet = turning ? (QuaternionD)body.transform.rotation : QuaternionD.identity, unplanet = QuaternionD.Inverse(planet);
            Vector3d camRel = (Vector3d)t.position - body.position;
            Vector3d camHere = unplanet * camRel;
            QuaternionD lookHere = unplanet * (QuaternionD)t.rotation;
            float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tanX = tanY * cam.aspect;
            bool continues = had && hadBody == body && hadFrame == Time.frameCount - 1;
            Vector3d moved = continues ? planet * (camHere - camWas) : Vector3d.zero;
            // (a cut, not a motion: the camera put somewhere else, or turned by more than a third of a right angle in one frame (the
            // view changed, or another craft taken); moved further than the craft it shows could have gone, with room to spare)
            if (continues)
            {
                Vessel flown = FlightGlobals.ActiveVessel;
                double could = (flown != null ? (turning ? flown.srfSpeed : flown.obt_speed) : 0.0) * Math.Max(Time.deltaTime, 0.001) * 2.0 + 30.0;
                QuaternionD lookThenCheck = planet * lookWas;
                double turnedBy = Vector3.Angle((Vector3)(lookThenCheck * Vector3d.forward), t.forward);
                if (moved.magnitude > could || moved.sqrMagnitude > 4e6 || turnedBy > 30.0) continues = false;
#if DEV
                traceCut = "moved " + moved.magnitude.ToString("F1") + " m of " + could.ToString("F1") + " (" + (turning ? "surface" : "orbital") + " speed " + (flown != null ? (turning ? flown.srfSpeed : flown.obt_speed) : 0.0).ToString("F0") + " m/s, frame " + (Time.deltaTime * 1000f).ToString("F1") + " ms), turned " + turnedBy.ToString("F2") + " deg";
#endif
            }
            float shutter = Running.shutter != null ? Running.shutter.Value / 360f : 0.5f;
            bool craftMoved = false;
            if (continues)
            {
                craftMoved |= first.Track(FlightGlobals.ActiveVessel, body, planet, unplanet, t.position);
                craftMoved |= second.Track(Second(), body, planet, unplanet, t.position);
            }
            else { first.Forget(); second.Forget(); }

            if (continues)
            {
                QuaternionD lookThen = planet * lookWas;
                Vector3 prevRight = (Vector3)(lookThen * Vector3d.right), prevUp = (Vector3)(lookThen * Vector3d.up), prevAhead = (Vector3)(lookThen * Vector3d.forward);
                bool turned = Vector3.Angle(prevAhead, t.forward) > 0.01f || Vector3.Angle(prevUp, t.up) > 0.01f || Mathf.Abs(tanY - tanYWas) > 1e-5f;
                if (moved.sqrMagnitude > 1e-6 || turned || craftMoved)
                {
                    bool depth = (cam.depthTextureMode & DepthTextureMode.Depth) != 0;
                    Vector3 r = t.right * tanX, u = t.up * tanY;
                    double ground = body.Radius + (body.pqsController != null ? Math.Max(0.0, FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.terrainAltitude : 0.0) : 0.0);
                    material.SetVector(idRight, new Vector4(r.x, r.y, r.z, depth ? 1f : 0f));
                    material.SetVector(idUp, new Vector4(u.x, u.y, u.z, Time.frameCount % 977));
                    material.SetVector(idAhead, new Vector4(t.forward.x, t.forward.y, t.forward.z, SystemInfo.usesReversedZBuffer ? 1f : 0f));
                    material.SetVector(idPrevRight, new Vector4(prevRight.x, prevRight.y, prevRight.z, tanXWas));
                    material.SetVector(idPrevUp, new Vector4(prevUp.x, prevUp.y, prevUp.z, tanYWas));
                    material.SetVector(idPrevAhead, new Vector4(prevAhead.x, prevAhead.y, prevAhead.z, cam.pixelHeight));
                    material.SetVector(idMoved, new Vector4((float)moved.x, (float)moved.y, (float)moved.z, (float)ground));
                    material.SetVector(idCam, new Vector4((float)camRel.x, (float)camRel.y, (float)camRel.z, body.pqsController != null || body.ocean ? 1f : 0f));
                    material.SetVector(idShutter, new Vector4(shutter, Running.Looks, 0.1f, cam.aspect));
                    first.Give(material);
                    second.Give(material);
                    pass.GetTemporaryRT(idScene, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
                    pass.Blit(BuiltinRenderTextureType.CameraTarget, idScene);
                    pass.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    pass.DrawMesh(sheet, Matrix4x4.identity, material, 0, 0);
                    pass.ReleaseTemporaryRT(idScene);
                    Said = "blending: the camera moved " + moved.magnitude.ToString("F2") + " m and turned " + Vector3.Angle(prevAhead, t.forward).ToString("F2") + " degrees since the last frame; shutter " + Mathf.RoundToInt(shutter * 360f) + " degrees"
                        + (first.Says.Length > 0 ? "; " + first.Says : "") + (second.Present ? "; " + second.Says : "");
                }
                else Said = "still: nothing to blend";
            }
            else Said = "a cut: this frame drawn as it is";
#if DEV
            TraceLine(first.Says + " (followed " + first.Present + ") | " + traceCut);
            traceCut = "";
#endif
            had = true; hadFrame = Time.frameCount; hadBody = body;
            camWas = camHere; lookWas = lookHere; tanXWas = tanX; tanYWas = tanY;
        }

        /// <summary>A second craft worth following: the nearest other one loaded, within 300 m of the camera.</summary>
        static Vessel Second()
        {
            Vessel best = null;
            float bestSq = 300f * 300f;
            Vector3 cam = FlightCamera.fetch != null ? FlightCamera.fetch.transform.position : Vector3.zero;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == FlightGlobals.ActiveVessel || v.packed || v.rootPart == null) continue;
                float d = (v.rootPart.transform.position - cam).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = v; }
            }
            return best;
        }

        /// <summary>
        /// A craft's own motion since the last frame, against the planet: where its root part went and how it turned, and the box
        /// it fills (in its root part's frame, from its parts' drawn bounds, made again every second or when its parts change).
        /// </summary>
        sealed class Craft
        {
            readonly int idC, idX, idY, idZ, idPivot, idMove, idSpin;
            Vessel vessel;
            bool had;
            Vector3d rootWas;
            QuaternionD turnWas;
            Vector3 boxMiddle, boxHalf;
            int boxFrame = -1000, boxParts = -1;
            Vector4 c, x, y, z, pivot, move, spin;
            internal bool Present;
            internal string Says = "";

            internal Craft(string letter)
            {
                idC = Shader.PropertyToID("_Blend" + letter + "C"); idX = Shader.PropertyToID("_Blend" + letter + "X"); idY = Shader.PropertyToID("_Blend" + letter + "Y");
                idZ = Shader.PropertyToID("_Blend" + letter + "Z"); idPivot = Shader.PropertyToID("_Blend" + letter + "Pivot"); idMove = Shader.PropertyToID("_Blend" + letter + "Move");
                idSpin = Shader.PropertyToID("_Blend" + letter + "Spin");
            }

            internal void Forget() { had = false; Present = false; }

            /// <summary>Where it is now against where it was; whether it moved.</summary>
            internal bool Track(Vessel v, CelestialBody body, QuaternionD planet, QuaternionD unplanet, Vector3 camPos)
            {
                if (v == null || v.rootPart == null || !v.loaded || v.packed || v.mainBody != body)
                {
                    Says = v == null ? "no craft" : v.GetDisplayName() + " not followed: " + (v.rootPart == null ? "no root part" : !v.loaded ? "not loaded" : v.packed ? "packed (on rails)" : "around " + (v.mainBody != null ? v.mainBody.bodyName : "nothing") + ", not " + (body != null ? body.bodyName : "nothing"));
                    vessel = null; had = false; Present = false; return false;
                }
                if (v != vessel) { vessel = v; had = false; boxParts = -1; }
                Transform root = v.rootPart.transform;
                if (Time.frameCount - boxFrame > 60 || v.parts.Count != boxParts) Box(v, root);
                // (The craft as it is drawn this frame: carried on from its last physics step, as Motion carries its models and the
                // camera that follows it (Keystone.Motion has how far). Its physics pose goes in steps, a step in some frames and two
                // in others, where the drawn craft and its camera go evenly. Taken from the physics pose, as until 2026-10-19, a
                // rocket followed by the camera moved against it by a step every other frame, by the reckoning here, and was
                // smeared up and down its length: the owner's "it makes the rocket look super blurry" (filmed at 12 s after lift-off:
                // the camera 15.0 m a frame, the rocket's root 8.7 and 17.5 m).)
                Vector3 rootAt = Keystone.Motion.CarryWith(v, root.position);
                Quaternion rootTurn = Keystone.Motion.TurnWith(v, root.rotation);
                Vector3d rootHere = unplanet * ((Vector3d)rootAt - body.position);
                QuaternionD turnHere = unplanet * (QuaternionD)rootTurn;
                bool moved = false;
                Present = false;
                if (had)
                {
                    Vector3d went = planet * (rootHere - rootWas);
                    // (the turn since the last frame, against the planet, as an axis times its angle)
                    QuaternionD turned = planet * (turnHere * QuaternionD.Inverse(turnWas)) * QuaternionD.Inverse(planet);
                    double w = Math.Max(-1.0, Math.Min(1.0, turned.w));
                    if (w < 0) { turned = new QuaternionD(-turned.x, -turned.y, -turned.z, -turned.w); w = -w; }
                    double angle = 2.0 * Math.Acos(w), sinHalf = Math.Sqrt(Math.Max(0.0, 1.0 - w * w));
                    Vector3d axis = sinHalf > 1e-9 ? new Vector3d(turned.x, turned.y, turned.z) / sinHalf : Vector3d.zero;
                    Vector3 turn = (Vector3)(axis * angle);
                    Vector3 middle = Keystone.Motion.CarryWith(v, root.TransformPoint(boxMiddle)) - camPos;
                    Vector3 ax = rootTurn * Vector3.right / Mathf.Max(boxHalf.x, 0.5f), ay = rootTurn * Vector3.up / Mathf.Max(boxHalf.y, 0.5f), az = rootTurn * Vector3.forward / Mathf.Max(boxHalf.z, 0.5f);
                    Vector3 p = rootAt - camPos;
                    c = new Vector4(middle.x, middle.y, middle.z, 1f);
                    x = ax; y = ay; z = az;
                    pivot = p;
                    move = new Vector4((float)went.x, (float)went.y, (float)went.z, 0f);
                    spin = turn;
                    Present = true;
                    moved = went.sqrMagnitude > 1e-6 || angle > 1e-5;
                    Says = v.GetDisplayName() + " moved " + went.magnitude.ToString("F2") + " m, turned " + (angle * 180.0 / Math.PI).ToString("F2") + " degrees, its box " + (2f * boxHalf.x).ToString("F1") + " x " + (2f * boxHalf.y).ToString("F1") + " x " + (2f * boxHalf.z).ToString("F1") + " m";
                }
                had = true; rootWas = rootHere; turnWas = turnHere;
                return moved;
            }

            void Box(Vessel v, Transform root)
            {
                boxFrame = Time.frameCount; boxParts = v.parts.Count;
                Vector3 least = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), most = -least;
                // (from the parts' colliders, their solid shape: their drawn bounds take in what rides on them, an engine's smoke
                // trail kilometres long among it, and a box that size took in the sky behind the craft and turned it with the craft)
                foreach (Part part in v.parts)
                {
                    if (part == null) continue;
                    bool any = false;
                    foreach (Collider col in part.GetComponentsInChildren<Collider>(false))
                    {
                        if (col == null || col.isTrigger || !col.enabled) continue;
                        Bounds b = col.bounds;
                        if (b.extents.sqrMagnitude > 1e6f) continue;
                        any = true;
                        Enclose(b, root, ref least, ref most);
                    }
                    if (!any) Enclose(new Bounds(part.transform.position, Vector3.one), root, ref least, ref most);
                }
                if (least.x > most.x) { least = -Vector3.one; most = Vector3.one; }
                // (a little larger than the craft: its edges are not cut off from it)
                boxMiddle = (least + most) * 0.5f;
                boxHalf = (most - least) * 0.5f + Vector3.one * 0.3f;
            }

            static void Enclose(Bounds b, Transform root, ref Vector3 least, ref Vector3 most)
            {
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) != 0 ? 1f : -1f, (k & 2) != 0 ? 1f : -1f, (k & 4) != 0 ? 1f : -1f));
                    Vector3 local = root.InverseTransformPoint(corner);
                    least = Vector3.Min(least, local); most = Vector3.Max(most, local);
                }
            }

            internal void Give(Material m)
            {
                m.SetVector(idC, Present ? c : Vector4.zero);
                m.SetVector(idX, x); m.SetVector(idY, y); m.SetVector(idZ, z);
                m.SetVector(idPivot, pivot); m.SetVector(idMove, move); m.SetVector(idSpin, spin);
            }
        }
    }
}
