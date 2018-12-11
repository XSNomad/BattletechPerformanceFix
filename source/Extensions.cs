using System;
using RSG;
using UnityEngine;
using Harmony;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;

namespace BattletechPerformanceFix {
    public static class Extensions {
        public static void LogDebug(string msg, params object[] values)
        {
            if (Main.LogLevel == "Debug")
                Main.__Log("[Debug] " + msg, values);
            // Far too much data passes through here to hit the HBS log
            //     it's simply too slow to handle it
        }

        public static void Log(string msg, params object[] values) {
            Main.__Log("[Info]" + msg, values);
            Trap(() => Main.HBSLogger.Log(string.Format(msg, values)));
        }

        public static void LogError(string msg, params object[] values)
        {
            Main.__Log("[Error] " + msg, values);
            Trap(() => Main.HBSLogger.LogError(string.Format(msg, values)));
        }

        public static void LogWarning(string msg, params object[] values)
        {
            Main.__Log("[Warning] " + msg, values);
            Trap(() => Main.HBSLogger.LogWarning(string.Format(msg, values)));
        }

        public static void LogException(Exception e)
        {
            Main.__Log("[Exception] {0}", e);
            Trap(() => Main.HBSLogger.LogException(e));
        }


        public static void Trap(Action f)
        { try { f(); } catch (Exception e) { Main.__Log("Exception {0}", e); } }

        public static T Trap<T>(Func<T> f)
        {
            try { return f(); } catch (Exception e) { Main.__Log("Exception {0}", e); return default(T);  }
        }

        public static T TrapAndTerminate<T>(string msg, Func<T> f)
        {
            try {
                return f();
            } catch (Exception e) {
                Main.__Log("PANIC {0} {1}", msg, e);
                TerminateImmediately();
                return default(T);
            }
        }

        public static K SafeCast<K>(this object t) where K : class
            => (t as K).NullCheckError($"Safe cast failed of {t.GetType().FullName} to {typeof(K).FullName}");

        public static T NullCheckError<T>(this T t, string msg) {
            if (t == null) LogError("{0} from {1}", msg, new StackTrace(1).ToString());
            return t;
        }

        public static T[] Array<T>(params T[] p) => p;
        public static List<T> List<T>(params T[] p) => p.ToList();
        public static IEnumerable<T> Sequence<T>(params T[] p) => p;
        public static void ForEach<T>(this IEnumerable<T> xs, Action<T> f) {
           foreach (var x in xs) f(x);
        }

        public static T GetWithDefault<K,T>(this Dictionary<K,T> d, K key, Func<T> lazyDefault)
            => d.TryGetValue(key, out var val) ? val : d[key] = lazyDefault();

        public static void TrapAndTerminate(string msg, Action f) => TrapAndTerminate<int>(msg, () => { f(); return 0; });

        // Do not let BattleTech recover anything. Forcibly close.
        // Only for use in dangerous patches which may need to prevent bad save data from being written.
        public static void TerminateImmediately()
            => System.Diagnostics.Process.GetCurrentProcess().Kill();


        public static IPromise AsPromise(this IEnumerator coroutine) {
            var prom = new Promise();
            BPF_CoroutineInvoker.Invoke(coroutine, prom.Resolve);
            return prom;
        }

        public static IPromise AsPromise(this AsyncOperation operation) {
            IEnumerator TillDone() { var timer = Stopwatch.StartNew();
                                     while (!operation.isDone && operation.progress < .9f) { yield return null; }
                                     var loadTime = timer.Elapsed.TotalSeconds;
                                     while (!operation.allowSceneActivation) { yield return null; }
                                     LogDebug("Scene activation -------");
                                     timer.Reset(); timer.Start();
                                     while (!operation.isDone) { yield return null; }
                                     yield return null;  // Let scene run Awake/Start
                                     var initTime = timer.Elapsed.TotalSeconds;
                                     LogDebug($"Scene fetched in {loadTime + initTime} seconds. :load ({loadTime} seconds) :init ({initTime} seconds)"); }
            return TillDone().AsPromise();
        }

        public static IPromise WaitAFrame(this Promise p) {
            IEnumerator OneFrame() { yield return null; }
            var next = new Promise();
            p.Done(() => BPF_CoroutineInvoker.Invoke(OneFrame(), next.Resolve));
            return next;
        }

        public static void Instrument(this MethodBase meth)
            => SimpleMetrics.Instrument(meth);

        public static void Track(this MethodBase meth)
            => SimpleMetrics.Track(meth);


        public static HarmonyMethod Drop = new HarmonyMethod(AccessTools.Method(typeof(Extensions), nameof(__Drop)));
        public static bool  __Drop() {
            LogDebug($"Dropping call to {new StackFrame(1).ToString()}");
            return false;
        }

        public static HarmonyMethod Yes = new HarmonyMethod(AccessTools.Method(typeof(Extensions), nameof(__Yes)));
        public static bool  __Yes(ref bool __result) {
            LogDebug($"Saying yes to to {new StackFrame(1).ToString()}");
            __result = true;
            return false;
        }
        public static HarmonyMethod No = new HarmonyMethod(AccessTools.Method(typeof(Extensions), nameof(__No)));
        public static bool  __No(ref bool __result) {
            LogDebug($"Saying yes to to {new StackFrame(1).ToString()}");
            __result = false;
            return false;
        }
    }

    class BPF_CoroutineInvoker : UnityEngine.MonoBehaviour {
        static BPF_CoroutineInvoker instance = null;
        public static BPF_CoroutineInvoker Instance { get => instance ?? Init(); }

        static BPF_CoroutineInvoker Init() {
            Extensions.Log("[BattletechPerformanceFix: Initializing a new coroutine proxy");
            var go = new UnityEngine.GameObject();
            go.name = "BattletechPerformanceFix:CoroutineProxy";
            instance = go.AddComponent<BPF_CoroutineInvoker>();
            UnityEngine.GameObject.DontDestroyOnLoad(go);

            return instance;
        }

        public static void Invoke(IEnumerator coroutine, Action done) {
            Instance.StartCoroutine(Proxy(coroutine, done));
        }

        static IEnumerator Proxy(IEnumerator coroutine, Action done) {
            yield return Instance.StartCoroutine(coroutine);
            done();
        }
    }
}
    
