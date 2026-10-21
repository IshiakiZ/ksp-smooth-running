#if DEV
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SmoothRunning
{
    /// <summary>
    /// (Development build: which methods the runtime compiles, frame by frame, for finding the game's first-time stalls
    /// (2026-10-18). The noting is tools/native/jitlog.c, built into ~/.ksp-ai-bridge/jitlog.dylib by tools/dev/jitlog.sh; this
    /// loads it, marks each frame's beginning for it, and has it write what it noted. "start" begins noting afresh, "take"
    /// writes it all to ~/.ksp-ai-bridge/jitlog.txt (tools/dev/jitlog.py reads it), "stop" ends it.)
    /// </summary>
    public static class JitLog
    {
        delegate int Starts();
        delegate void Plain();
        delegate int Takes([MarshalAs(UnmanagedType.LPStr)] string path);
        static Starts start;
        static Plain stop, frame;
        static Takes take;
        static GameObject marker;

        [DllImport("libSystem.dylib", EntryPoint = "dlopen")] static extern IntPtr Open(string path, int mode);
        [DllImport("libSystem.dylib", EntryPoint = "dlsym")] static extern IntPtr Find(IntPtr library, string name);

        static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".ksp-ai-bridge");

        static T Get<T>(IntPtr library, string name) where T : class
        {
            IntPtr at = Find(library, name);
            return at == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(at, typeof(T)) as T;
        }

        static string Load()
        {
            if (start != null) return null;
            if (Application.platform != RuntimePlatform.OSXPlayer) return "only on a Mac";
            string path = Path.Combine(Home, "jitlog.dylib");
            if (!File.Exists(path)) return "no " + path + " (tools/dev/jitlog.sh makes it)";
            IntPtr library = Open(path, 2);
            if (library == IntPtr.Zero) return "could not load " + path;
            start = Get<Starts>(library, "jl_start"); stop = Get<Plain>(library, "jl_stop"); frame = Get<Plain>(library, "jl_frame"); take = Get<Takes>(library, "jl_take");
            return start != null && stop != null && frame != null && take != null ? null : "the library has not all of jl_start, jl_stop, jl_frame and jl_take";
        }

        public static string Do(string what)
        {
            string problem = Load();
            if (problem != null) return problem;
            switch ((what ?? "").Trim())
            {
                case "start":
                    if (start() == 0) return "Mono's profiler interface is not in this game";
                    if (marker == null)
                    {
                        marker = new GameObject("Smooth Running: the compiling noted");
                        UnityEngine.Object.DontDestroyOnLoad(marker);
                        marker.AddComponent<Marker>();
                    }
                    return "noting what is compiled";
                case "take":
                    string to = Path.Combine(Home, "jitlog.txt");
                    int n = take(to);
                    return n < 0 ? "could not write " + to : n + " methods compiled, written to " + to;
                case "stop":
                    stop();
                    if (marker != null) UnityEngine.Object.Destroy(marker);
                    marker = null;
                    return "stopped";
            }
            return "start, take or stop";
        }

        [DefaultExecutionOrder(-32000)]
        sealed class Marker : MonoBehaviour
        {
            void Update() => frame?.Invoke();
        }
    }
}
#endif
