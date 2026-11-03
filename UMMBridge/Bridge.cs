using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityModManagerNet;

namespace UMMBridge;

public class Bridge : MelonPlugin
{
    /// <summary>
    /// Tracks which methods are patched by which Harmony runtime.
    /// true  = 0Harmony_UMM native Patch() wrote a native detour.
    /// false = ProxyPatch (HarmonyX) wrote the detour.
    /// Used by the dual-MonoMod conflict detection to ensure at most
    /// one runtime writes a JMP at any given method entry.
    /// </summary>
    internal static readonly ConcurrentDictionary<MethodBase, bool> PatchedMethods = new();

    /// <summary>
    /// Extra routing diagnostics.  Off by default: the conflict check runs for
    /// every patch job, and one message per job floods the MelonLoader log.
    /// Enable with the UMMBRIDGE_VERBOSE=1 environment variable, or
    /// Verbose=true in UMMBridge.cfg.
    /// </summary>
    internal static readonly bool Verbose =
        TranspilerBridge.ReadSetting("UMMBRIDGE_VERBOSE", "Verbose", false);

    internal static void Trace(string message)
    {
        if (Verbose) Melon<Bridge>.Logger.Msg("[DBG] " + message);
    }

    internal static string Describe(MethodBase method)
        => method == null ? "<null>" : $"{method.DeclaringType?.Name}.{method.Name}";

    /// <summary>
    /// Records which runtime owns the method entry.  This marker is the only
    /// input to the conflict decision, so every state change goes through here.
    /// </summary>
    internal static void SetOwnership(MethodBase original, bool native)
    {
        PatchedMethods[original] = native;
        Trace($"own {Describe(original)}=(native:{native})");
    }

    private static readonly Dictionary<MethodBase, NativeDetour> NativeDetours = new();

    /// <summary>
    /// The body of 0Harmony_UMM's PatchTools.DetourMethod() (Cecil-replaced),
    /// which every native detour goes through — including ones that bypass
    /// Harmony.Patch() and the routed UpdateWrapper (JALib's [JAPatch]).  Same
    /// job as the original: drop the previous detour on this method and point
    /// its entry at the new replacement, but written by MelonLoader's
    /// MonoMod.RuntimeDetour, which also works on Arm64.
    /// </summary>
    public static void DetourNative(MethodBase original, MethodBase replacement)
    {
        if (!AllowNativeDetour(original)) return;
        // ponytail: a raw jump like 2.3's own detour, so a HarmonyX ILHook on
        // the same method is still trampled (AllowNativeDetour stops that case).
        // A RuntimeDetour Detour would join HarmonyX's chain instead.
        lock (NativeDetours)
        {
            if (NativeDetours.TryGetValue(original, out var previous))
            {
                Trace($"detour {Describe(original)}: undoing previous");
                previous.Dispose();
            }
            Trace($"detour {Describe(original)}: writing");
            NativeDetours[original] = new NativeDetour(original, replacement);
            Trace($"detour {Describe(original)}: done");
        }
    }

    /// <summary>
    /// The body of 0Harmony_UMM's AccessTools.Identifiable() (Cecil-replaced).
    /// Same two steps as MonoMod.Core's: map a method to the instance the
    /// detour engine pinned for it — now RuntimeDetour's map — then drop the
    /// ReflectedType, so a method looked up through a derived type keys the
    /// same patch state.
    ///
    /// <para>The first step also creates a DynamicMethod's runtime handle.
    /// 2.3's UpdatePatchInfo calls this on a fresh replacement and right after
    /// reads replacement.MethodHandle.GetFunctionPointer(); on Mono a
    /// never-created DynamicMethod has a zero handle there and the runtime
    /// segfaults.  A detour normally creates it first, but a refused one does
    /// not — JALib unpatching a HarmonyX-owned method crashed the game.</para>
    /// </summary>
    public static MethodInfo Identifiable(MethodInfo method)
    {
        var id = DetourHelper.GetIdentifiable(method) as MethodInfo ?? method;
        return id.DeclaringType is { } type && id.ReflectedType != type
            ? MethodBase.GetMethodFromHandle(id.MethodHandle, type.TypeHandle) as MethodInfo ?? id
            : id;
    }

    /// <summary>
    /// Returns false to skip a native detour when HarmonyX already owns the
    /// method entry; otherwise records native ownership so later mod patches
    /// join the native table instead of adding a second detour.
    /// </summary>
    internal static bool AllowNativeDetour(MethodBase original)
    {
        // Where 0Harmony_UMM cannot detour at all the detour is about to throw;
        // claiming the entry would only pull later mod patches off HarmonyX.
        if (original == null || !AssemblyInterceptor.NativeDetoursWork || IsNativeOwned(original)) return true;

        if (HasHarmonyXPatches(original))
        {
            // A routed method's UMM patches are applied from 0Harmony_UMM's
            // registry by SyncRouted, so JALib's Unpatch rebuilding it natively
            // loses nothing.  Only JALib's own patch types (Replace, TryPrefix,
            // ...) have no HarmonyX form.
            if (IsRouted(original))
                Trace($"native detour skipped: \"{Describe(original)}\" is routed to HarmonyX " +
                      $"(caller=[{GetCallingModName()}])");
            else
                Melon<Bridge>.Logger.Warning(
                    $"Native detour refused: HarmonyX owns \"{Describe(original)}\" " +
                    $"(caller=[{GetCallingModName()}]) — that patch was NOT applied");
            Trace(new StackTrace(1, false).ToString());
            return false;
        }

        SetOwnership(original, true);
        return true;
    }

    /// <summary>
    /// Marks a method native before anything patched it, so every mod patch
    /// that arrives later joins the native table.  Never takes an entry
    /// HarmonyX already owns.
    /// </summary>
    internal static bool ClaimNative(MethodBase method)
    {
        if (HasHarmonyXPatches(method))
        {
            Melon<Bridge>.Logger.Warning(
                $"Claim: HarmonyX already owns \"{Describe(method)}\" — left on HarmonyX");
            return false;
        }

        MarkNativeOwner(method);
        return IsNativeOwned(method);
    }

    /// <summary>
    /// Called from the Cecil-injected check in 0Harmony_UMM's
    /// PatchFunctions.UpdateWrapper().  Returns true when the patch must not
    /// claim a native detour — either because HarmonyX already owns the method
    /// entry, or because bridge mode routes mod patches there.
    /// </summary>
    public static bool IsMethodPatchedByProxy(MethodBase original)
    {
        if (original == null) return false;

        // One stack walk answers both "is this a UMM mod?" and "which one?".
        // Walking the stack per log line was the hottest part of this check.
        var caller = GetCaller();

        // 0) Bridge mode: a mod patch belongs on HarmonyX whether or not a
        //    conflict exists, so a transpiler can be converted instead of
        //    claiming a second native detour.  Native ownership still wins.
        if (TranspilerBridge.Enabled && !IsNativeOwned(original) && caller.IsMod)
        {
            Trace($"bridged {Describe(original)} to HarmonyX (caller=[{caller.Name}])");
            return true;
        }

        // 1) Ownership already known
        if (PatchedMethods.TryGetValue(original, out var nativeOwned))
        {
            Trace($"tracked {Describe(original)} native={nativeOwned} (caller=[{caller.Name}])");
            return !nativeOwned;
        }

        // 2) Ask HarmonyX — this catches MelonLoader mods that patch directly
        //    instead of going through ProxyPatch.
        //
        //    Deliberately NOT cached: any assembly may add a patch at any
        //    time, so a "clean" answer is only valid for the instant it was
        //    taken.  Reusing an old one let a native detour be written on top
        //    of a HarmonyX detour, which corrupts the method entry.
        try
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(original);
            if (info != null &&
                (info.Prefixes?.Count > 0 ||
                 info.Postfixes?.Count > 0 ||
                 info.Transpilers?.Count > 0 ||
                 info.Finalizers?.Count > 0))
            {
                Trace($"HarmonyX owns {Describe(original)} — skip native detour (caller=[{caller.Name}])");
                SetOwnership(original, false);
                return true;
            }
        }
        catch (Exception ex)
        {
            // Cannot tell whether a detour exists, so refuse to add a second one.
            Melon<Bridge>.Logger.Warning(
                $"ConflictCheck: GetPatchInfo failed for \"{Describe(original)}\": {ex.Message} — " +
                "assuming HarmonyX owns the method entry");
            return true;
        }

        Trace($"no conflict for {Describe(original)} (caller=[{caller.Name}])");
        return false;
    }

    /// <summary>Nearest stack frame outside the bridge: who is patching.</summary>
    private readonly struct Caller
    {
        public readonly bool IsMod;
        public readonly string Name;

        public Caller(bool isMod, string name)
        {
            IsMod = isMod;
            Name = name;
        }
    }

    private static Caller GetCaller()
    {
        try
        {
            var stack = new StackTrace(false);
            for (var i = 0; i < stack.FrameCount; i++)
            {
                var assembly = stack.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                if (assembly == null) continue;

                var name = assembly.GetName().Name;
                if (name == "0Harmony_UMM" || name == "0Harmony" || name == "UMMBridge")
                    continue;

                return new Caller(AssemblyInterceptor.IsRegisteredModAssembly(assembly), name);
            }
        }
        catch { }

        return new Caller(false, "?");
    }

    /// <summary>Name of the assembly that triggered the conflict check.</summary>
    internal static string GetCallingModName() => GetCaller().Name;

    /// <summary>
    /// Called from the Cecil-injected check in PatchFunctions.UpdateWrapper()
    /// when IsMethodPatchedByProxy returned true: this method's entry belongs
    /// to HarmonyX.  0Harmony_UMM keeps the record — its PatchInfo stays the
    /// one registry every UMM patch, unpatch and owner query goes through —
    /// and <see cref="SyncRouted"/> applies those patches on HarmonyX.  No
    /// native detour is written.
    /// </summary>
    public static MethodInfo RouteConflictUpdateWrapper(MethodBase original, object patchInfoObj)
    {
        if (original == null || patchInfoObj == null)
            return original as MethodInfo;

        SetOwnership(original, false);
        return SyncRouted(original, patchInfoObj) ?? original as MethodInfo;
    }

    /// <summary>
    /// Called from the Cecil-injected prologue of 0Harmony_UMM's
    /// HarmonySharedState.UpdatePatchInfo(), where every change to its
    /// registry lands — including JALib's Unpatch, which uses its own copy of
    /// UpdateWrapper and so never reaches the routed one.  Keeps HarmonyX in
    /// step for a method whose entry HarmonyX owns.
    /// </summary>
    public static void OnPatchInfoUpdated(MethodBase original, object patchInfoObj)
    {
        if (original == null || patchInfoObj == null || IsNativeOwned(original) || !HasHarmonyXPatches(original))
            return;
        SyncRouted(original, patchInfoObj);
    }

    private static readonly HarmonyPatchType[] RoutedKinds =
        { HarmonyPatchType.Prefix, HarmonyPatchType.Postfix, HarmonyPatchType.Transpiler, HarmonyPatchType.Finalizer };

    private static readonly string[] RoutedLists = { "prefixes", "postfixes", "transpilers", "finalizers" };

    /// <summary>
    /// What the bridge itself put on HarmonyX, per method and per (owner,
    /// kind).  Lets <see cref="SyncRouted"/> touch only its own entries, never
    /// a MelonLoader mod's.
    /// </summary>
    private static readonly Dictionary<MethodBase, Dictionary<(string Owner, HarmonyPatchType Kind), MethodInfo[]>> Routed = new();

    private static readonly Dictionary<MethodBase, MethodInfo> RoutedReplacement = new();
    private static readonly HashSet<string> ReportedRouteFailures = new();

    private static bool IsRouted(MethodBase original)
    {
        lock (Routed) return Routed.ContainsKey(original);
    }

    /// <summary>
    /// Makes HarmonyX's patches on <paramref name="original"/> match
    /// 0Harmony_UMM's PatchInfo: each patch under its real owner id, so
    /// before/after ordering, owner queries and Unpatch by id all work, and
    /// transpilers through <see cref="TranspilerBridge"/>.  Only (owner, kind)
    /// groups that changed are re-applied; one that disappeared is unpatched.
    /// Returns the latest HarmonyX replacement, or null when there is none.
    /// </summary>
    internal static MethodInfo SyncRouted(MethodBase original, object patchInfoObj)
    {
        lock (Routed)
        {
            var want = DesiredPatches(original, patchInfoObj);
            if (want == null) return null;

            if (!Routed.TryGetValue(original, out var have))
                Routed[original] = have = new();

            foreach (var key in ChangedGroups(want, have))
            {
                var hx = new HarmonyLib.Harmony(key.Owner);
                var applied = new List<MethodInfo>();
                try
                {
                    if (have.TryGetValue(key, out var old))
                    {
                        Trace($"unrouting {old.Length} {key.Kind}(es) of [{key.Owner}] on {Describe(original)}");
                        hx.Unpatch(original, key.Kind, key.Owner);
                        have.Remove(key);
                    }
                    if (!want.TryGetValue(key, out var patches)) continue;

                    // A HarmonyX PatchProcessor holds one patch per kind (Add*
                    // replaces, it does not append), so each patch gets its own.
                    foreach (var hm in patches)
                    {
                        var proc = hx.CreateProcessor(original);
                        switch (key.Kind)
                        {
                            case HarmonyPatchType.Prefix: proc.AddPrefix(hm); break;
                            case HarmonyPatchType.Postfix: proc.AddPostfix(hm); break;
                            case HarmonyPatchType.Transpiler: proc.AddTranspiler(hm); break;
                            default: proc.AddFinalizer(hm); break;
                        }

                        var replacement = proc.Patch();
                        applied.Add(hm.method);
                        if (replacement != null) RoutedReplacement[original] = replacement;
                    }
                    Trace($"routed {applied.Count} {key.Kind}(es) of [{key.Owner}] on {Describe(original)} to HarmonyX");
                }
                catch (Exception ex)
                {
                    ReportRouteFailure(
                        $"Route: a {key.Kind} of [{key.Owner}] on \"{Describe(original)}\" could not be applied on " +
                        $"HarmonyX ({ex.GetType().Name}: {ex.Message}) — it and the ones after it are NOT active");
                }
                finally
                {
                    // Exactly what is on HarmonyX now, so the next sync diffs against the truth.
                    if (applied.Count > 0) have[key] = applied.ToArray();
                }
            }

            return RoutedReplacement.TryGetValue(original, out var latest) ? latest : null;
        }
    }

    /// <summary>
    /// 0Harmony_UMM's PatchInfo as HarmonyX patch groups, in registry order.
    /// Null when the PatchInfo cannot be read (0Harmony_UMM changed shape):
    /// HarmonyX is then left as it is rather than emptied.
    /// </summary>
    private static Dictionary<(string Owner, HarmonyPatchType Kind), List<HarmonyLib.HarmonyMethod>> DesiredPatches(
        MethodBase original, object patchInfoObj)
    {
        var piType = patchInfoObj.GetType();
        var lists = RoutedLists.Select(name => FieldOf(piType, name)?.GetValue(patchInfoObj) as Array).ToArray();
        if (lists.All(list => list == null))
        {
            ReportRouteFailure(
                $"Route: no patch lists on '{piType.FullName}' for \"{Describe(original)}\" — " +
                "HarmonyX left unchanged");
            return null;
        }

        var want = new Dictionary<(string Owner, HarmonyPatchType Kind), List<HarmonyLib.HarmonyMethod>>();
        for (var k = 0; k < RoutedKinds.Length; k++)
        {
            if (lists[k] == null) continue;

            foreach (var patch in lists[k])
            {
                var hm = PatchToHx(patch);
                if (hm == null)
                {
                    ReportRouteFailure(
                        $"Route: could not read a {RoutedKinds[k]} of \"{Describe(original)}\" — that patch is NOT active");
                    continue;
                }

                if (RoutedKinds[k] == HarmonyPatchType.Transpiler)
                {
                    if (!TranspilerBridge.TryWrap(hm, out var wrapped, out var error))
                    {
                        ReportRouteFailure(
                            $"Route: transpiler '{hm.method?.Name}' of \"{Describe(original)}\" could not be " +
                            $"bridged ({error}) — that transpiler does NOT run");
                        continue;
                    }
                    hm = wrapped;
                }

                var owner = Read(patch, "owner") as string;
                var key = (string.IsNullOrEmpty(owner) ? "UMMBridge.Unowned" : owner, RoutedKinds[k]);
                if (!want.TryGetValue(key, out var group)) want[key] = group = new();
                group.Add(hm);
            }
        }

        return want;
    }

    /// <summary>(Owner, kind) groups whose patch methods differ between the two sides.</summary>
    internal static List<(string Owner, HarmonyPatchType Kind)> ChangedGroups(
        Dictionary<(string Owner, HarmonyPatchType Kind), List<HarmonyLib.HarmonyMethod>> want,
        Dictionary<(string Owner, HarmonyPatchType Kind), MethodInfo[]> have)
        => want.Keys.Union(have.Keys)
            .Where(key => !(want.TryGetValue(key, out var w) ? w.Select(hm => hm.method) : Enumerable.Empty<MethodInfo>())
                .SequenceEqual(have.TryGetValue(key, out var h) ? h : Enumerable.Empty<MethodInfo>()))
            .ToList();

    /// <summary>A routing problem repeats on every later sync of the method; say it once.</summary>
    private static void ReportRouteFailure(string message)
    {
        lock (ReportedRouteFailures)
            if (!ReportedRouteFailures.Add(message)) return;
        Melon<Bridge>.Logger.Warning(message);
    }

    /// <summary>
    /// Field/property lookups into 0Harmony_UMM are cached per (type, name).
    /// Patch application used to re-resolve the same handful of members for
    /// every patch on every call; caching also lets a renamed member be
    /// reported once at startup instead of silently reading null forever.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type Type, string Name), FieldInfo> FieldCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo> PropertyCache = new();

    private static FieldInfo FieldOf(Type type, string name)
        => type == null
            ? null
            : FieldCache.GetOrAdd((type, name), key => key.Type.GetField(
                key.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

    private static PropertyInfo PropertyOf(Type type, string name)
        => type == null
            ? null
            : PropertyCache.GetOrAdd((type, name), key => key.Type.GetProperty(
                key.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

    private static object Read(object instance, string name)
        => instance == null ? null : FieldOf(instance.GetType(), name)?.GetValue(instance);

    /// <summary>
    /// Converts a 0Harmony_UMM Patch object (reflection, cross-assembly) to a
    /// HarmonyX HarmonyMethod by reading its fields by name.
    /// </summary>
    private static HarmonyLib.HarmonyMethod PatchToHx(object patch)
    {
        if (patch == null) return null;
        var srcType = patch.GetType();

        // Harmony 2.3.x exposes the patched method as a property; some builds
        // only carry the backing field, so accept either.
        var mi = (PropertyOf(srcType, "PatchMethod")?.GetValue(patch, null)
                  ?? FieldOf(srcType, "patchMethod")?.GetValue(patch)) as MethodInfo;
        if (mi == null) return null;

        var hx = new HarmonyLib.HarmonyMethod(mi);
        CopySchedulingFields(patch, hx);
        return hx;
    }

    /// <summary>
    /// Copies priority/debug/before/after from a 0Harmony_UMM patch descriptor to
    /// a HarmonyX one.  Both runtimes store these as the same CLR types, so the
    /// values transfer as-is.
    /// </summary>
    private static void CopySchedulingFields(object source, HarmonyLib.HarmonyMethod target)
    {
        if (source == null || target == null) return;

        var type = source.GetType();

        if (Read(source, "priority") is int priority)
            FieldOf(target.GetType(), "priority")?.SetValue(target, priority);

        if (Read(source, "debug") is bool debug)
            FieldOf(target.GetType(), "debug")?.SetValue(target, debug);

        if (Read(source, "before") is string[] before)
            FieldOf(target.GetType(), "before")?.SetValue(target, before);

        if (Read(source, "after") is string[] after)
            FieldOf(target.GetType(), "after")?.SetValue(target, after);
    }

    private static void MarkNativeOwner(MethodBase original)
    {
        // Do not overwrite an existing ProxyPatch marker: false means HarmonyX
        // owns the detour and the native runtime must stay away from it.
        if (!PatchedMethods.TryAdd(original, true))
        {
            Trace($"native ownership already set for {Describe(original)} (marker={(IsNativeOwned(original) ? "native" : "harmonyx")})");
        }
    }

    /// <summary>
    /// Reflectively invoke 0Harmony_UMM's Harmony.Patch(), so the patch is
    /// recorded in its registry like any other UMM patch; its UpdateWrapper
    /// then decides which runtime applies it.
    /// </summary>
    private static MethodInfo PatchThroughUmmHarmony(
        object harmony, MethodBase original,
        object prefix, object postfix, object transpiler, object finalizer)
    {
        try
        {
            var harmonyType = harmony.GetType();
            var hmType = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod");
            var nativePatch = harmonyType.GetMethod("Patch", new[] {
                typeof(MethodBase), hmType, hmType, hmType, hmType
            });
            if (nativePatch == null)
            {
                Melon<Bridge>.Logger.Warning(
                    $"ProxyPatch: 0Harmony_UMM Harmony.Patch(MethodBase, HarmonyMethod x4) not found for \"{Describe(original)}\"");
                return null;
            }

            return (MethodInfo)nativePatch.Invoke(harmony, new object[] {
                original, prefix, postfix, transpiler, finalizer
            });
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"ProxyPatch failed for \"{Describe(original)}\": {(ex.InnerException ?? ex).Message}");
        }

        return null;
    }

    private static readonly MethodInfo OriginalModsPathGetter = typeof(UnityModManager).GetProperty(nameof(UnityModManager.modsPath), BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
    private static readonly MethodInfo RedirectMethod = typeof(Bridge).GetMethod(nameof(RedirectModsPath), BindingFlags.Static | BindingFlags.NonPublic);

    public override void OnApplicationStarted()
    {
        TranspilerBridge.Log = message => Melon<Bridge>.Logger.Msg(message);
        TranspilerBridge.Warn = message => Melon<Bridge>.Logger.Warning(message);
        TranspilerBridge.Initialize();

        AssemblyInterceptor.Initialize();

        var harmony = new HarmonyLib.Harmony("UMMBridge");
        harmony.Patch(OriginalModsPathGetter, new HarmonyMethod(RedirectMethod));

        AssemblyInterceptor.ClaimJALibPatchTargets();

        LoggerInstance.Msg("Starting UMM");
        UnityModManager.Start();
        LoggerInstance.Msg("Loading UI");
        AccessTools.Method(typeof(Injector), "RunUI").Invoke(null, null);

        PatchUI();
        PatchCreplayTriggerMessage();
    }

    /// <summary>
    /// Creplay.TriggerMessage(string, float) calls UMM UI notifications
    /// which aren't fully functional under MelonLoader — the underlying
    /// singleton or UI component is null, throwing NRE.  This prefix
    /// intercepts the call, logs the message via MelonLoader instead,
    /// and skips the original to prevent the crash.
    /// </summary>
    private static void PatchCreplayTriggerMessage()
    {
        try
        {
            var creplayType = AccessTools.TypeByName("CREPLAY.Creplay");
            if (creplayType == null) return;

            var triggerMethod = AccessTools.Method(creplayType, "TriggerMessage",
                new[] { typeof(string), typeof(float) });
            if (triggerMethod == null) return;

            new HarmonyLib.Harmony("UMMBridge.CreplayFix")
                .Patch(triggerMethod, prefix: new HarmonyMethod(
                    typeof(Bridge), nameof(Pre_TriggerMessage)));

            Melon<Bridge>.Logger.Msg("Patched Creplay.TriggerMessage → MelonLoader log");
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning($"Failed to patch Creplay.TriggerMessage: {ex.Message}");
        }
    }


    private static string _displayMessage = null;
    private static float _messageEndTime = 0f;

    // 在 Pre_TriggerMessage 中设置消息（并启动显示）
    public static bool Pre_TriggerMessage(string message, float dur = 5)
    {
        // 设置屏幕显示
        _displayMessage = message;
        _messageEndTime = Time.unscaledTime + dur; // dur 是显示时长（秒）

        // 如果尚未创建显示 GameObject，则创建
        if (_displayInstance == null)
        {
            var go = new GameObject("UMMBridge_MessageDisplay");
            GameObject.DontDestroyOnLoad(go);
            _displayInstance = go.AddComponent<MessageDisplay>();
        }

        return false; // 跳过原始方法
    }

    // MonoBehaviour 负责 OnGUI 绘制
    private class MessageDisplay : MonoBehaviour
    {
        // OnGUI runs every frame while a message is visible.  Building the style
        // (and its nested GUIStyleState) per frame produced steady garbage, so
        // it is created once on the GUI thread and reused.
        private GUIStyle _style;

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_displayMessage) || Time.unscaledTime > _messageEndTime)
            {
                _displayMessage = null;
                return;
            }

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Normal,
                    normal = { textColor = Color.white },
                    alignment = TextAnchor.UpperLeft
                };
            }

            var rect = new Rect(10, 10, Screen.width - 20, 60);
            // 黑色阴影
            var shadowRect = new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height);
            GUI.Label(shadowRect, _displayMessage, _style);
            GUI.Label(rect, _displayMessage, _style);
        }
    }

    private static MessageDisplay _displayInstance;

    private static bool RedirectModsPath(ref string __result)
    {
        var ummModsDir = AssemblyInterceptor.ModsRootPath;
        Directory.CreateDirectory(ummModsDir);
        __result = ummModsDir;
        return false;
    }

    /// <summary>
    /// Called from Cecil-rewritten mod assemblies to resolve
    /// Assembly.get_Location() for byte-loaded assemblies.
    /// Returns the original file path if cached, otherwise falls
    /// back to the real Location.
    /// </summary>
    public static string GetAssemblyLocation(Assembly asm)
    {
        // Fast path — most calls happen after CWT is populated
        if (AssemblyInterceptor._assemblyOriginalPath.TryGetValue(asm, out var path))
            return path;

        // Fallback for static constructors that run inside Assembly.Load():
        // the CWT entry hasn't been added yet, but the thread-local
        // _currentLoadingPath is set before Assembly.Load().
        var loadingPath = AssemblyInterceptor._currentLoadingPath;
        if (loadingPath != null)
            return loadingPath;

        return asm.Location;
    }

    /// <summary>
    /// Proxy for Harmony 2.3.x Patch() calls intercepted via Cecil rewriting
    /// in UMM mod assemblies.  Fixes a cross-runtime MethodBase, then patches
    /// through 0Harmony_UMM itself: one registry for every UMM patch, so
    /// Unpatch, UnpatchAll and owner queries see this one too.  Routing to
    /// HarmonyX happens in its UpdateWrapper, like for PatchAll().
    /// </summary>
    public static MethodInfo ProxyPatch(
        object harmony,  // 0Harmony_UMM's Harmony instance
        MethodBase original,
        object prefix,
        object postfix,
        object transpiler,
        object finalizer)
    {
        // ── Cross-runtime MethodBase redirect ──────────────────────────
        // When a UMM mod (loaded via LoadFrom context) patches a method
        // on a generic type whose closed instantiation exists in two CLR
        // copies (LoadFrom copy vs default Load copy), redirect to the
        // already-loaded instance that the game actually calls.
        if (NeedsMethodRedirect(original, out var corrected))
        {
            Melon<Bridge>.Logger.Msg(
                $"ProxyPatch: redirect {original.DeclaringType?.Name}.{original.Name} " +
                $"[0x{original.DeclaringType.TypeHandle.Value:X}] → " +
                $"[0x{corrected.DeclaringType.TypeHandle.Value:X}]");
            original = corrected;
        }

        return PatchThroughUmmHarmony(harmony, original, prefix, postfix, transpiler, finalizer);
    }

    private static bool IsNativeOwned(MethodBase original)
        => PatchedMethods.TryGetValue(original, out var native) && native;

    /// <summary>
    /// True when HarmonyX has any patch (including a UMMBridge proxy) on the
    /// method.  Used to refuse native fallbacks that would overwrite a
    /// HarmonyX detour.
    /// </summary>
    private static bool HasHarmonyXPatches(MethodBase original)
    {
        if (PatchedMethods.TryGetValue(original, out var native) && !native)
            return true;

        try
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(original);
            if (info == null) return false;

            return (info.Prefixes?.Count ?? 0) +
                   (info.Postfixes?.Count ?? 0) +
                   (info.Transpilers?.Count ?? 0) +
                   (info.Finalizers?.Count ?? 0) > 0;
        }
        catch
        {
            // If the patch table cannot be read, assume a detour exists and
            // do not risk a second one.
            return true;
        }
    }

    /// <summary>
    /// Detect and redirect cross-runtime generic type identity splits.
    /// When a mod assembly is loaded via LoadFrom context, its references
    /// to generic type instantiations can resolve to different CLR types
    /// than the game's already-loaded copies — even when both claim to
    /// be from the same assembly (e.g. Assembly-CSharp loaded twice).
    /// Harmony detours on the wrong copy are silent no-ops.
    /// </summary>
    private static bool NeedsMethodRedirect(MethodBase original, out MethodBase corrected)
    {
        corrected = null;

        // Only handle PriorityQueue<SkyHookEvent, ulong>.Enqueue for now
        if (original.Name != "Enqueue") return false;
        if (original.DeclaringType?.Name != "PriorityQueue`2") return false;

        try
        {
            var scrCtrl = AccessTools.TypeByName("scrController");
            if (scrCtrl == null) return false;
            var field = scrCtrl.GetField("sortedKeyQueue",
                BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public | BindingFlags.Static);
            if (field == null) return false;
            var gameType = field.FieldType;

            // Same CLR type identity — no redirect needed
            if (original.DeclaringType == gameType) return false;

            // Find Enqueue on the game's actual type
            corrected = AccessTools.Method(gameType, "Enqueue");
            return corrected != null;
        }
        catch { return false; }
    }

    private void PatchUI()
    {
        try
        {
            var uiType = typeof(UnityModManager).Assembly.GetType("UnityModManagerNet.UnityModManager+UI");
            if (uiType == null)
            {
                LoggerInstance.Warning("UI nested type not found");
                return;
            }
            var windowFunc = AccessTools.Method(uiType, "WindowFunction");
            if (windowFunc == null)
            {
                LoggerInstance.Warning("UI.WindowFunction not found");
                return;
            }
            new HarmonyLib.Harmony("UMMBridge.UI").Patch(windowFunc, postfix: new HarmonyMethod(typeof(Bridge), nameof(Post_WindowFunction)));
            LoggerInstance.Msg("Mods Dir button added to UMM window");
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Failed to patch UMM UI: {ex.Message}");
        }
    }

    private static void Post_WindowFunction(object __instance)
    {
        var rect = Traverse.Create(__instance).Field("mWindowRect").GetValue<Rect>();
        var btnRect = new Rect(rect.width - 105, 3, 100, 22);
        if (GUI.Button(btnRect, "Mods Dir"))
        {
            var path = AssemblyInterceptor.ModsRootPath;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
    }
}
