using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
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
    /// Enable with the UMMBRIDGE_VERBOSE=1 environment variable.
    /// </summary>
    internal static readonly bool Verbose =
        Environment.GetEnvironmentVariable("UMMBRIDGE_VERBOSE") == "1";

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

    /// <summary>
    /// Called from the Cecil-injected guard at the top of 0Harmony_UMM's
    /// PatchTools.DetourMethod(), which every native detour goes through —
    /// including ones that bypass Harmony.Patch() and the routed UpdateWrapper
    /// (JALib's [JAPatch]).  Returns false to skip the detour when HarmonyX
    /// already owns the method entry; otherwise records native ownership so
    /// later mod patches join the native table instead of adding a second
    /// detour.
    /// </summary>
    public static bool AllowNativeDetour(MethodBase original)
    {
        // Where 0Harmony_UMM cannot detour at all the detour is about to throw;
        // claiming the entry would only pull later mod patches off HarmonyX.
        if (original == null || !AssemblyInterceptor.NativeDetoursWork || IsNativeOwned(original)) return true;

        if (HasHarmonyXPatches(original))
        {
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

    /// <summary>
    /// True when the nearest non-internal frame comes from an assembly that
    /// was loaded out of UMMMods.  Used to keep the transpiler bridge
    /// scoped to mods: UMM's own core patches keep the native runtime.
    /// </summary>
    internal static bool IsUmmModCaller() => GetCaller().IsMod;

    /// <summary>Name of the assembly that triggered the conflict check.</summary>
    internal static string GetCallingModName() => GetCaller().Name;

    /// <summary>
    /// Called from the Cecil-injected check in PatchFunctions.UpdateWrapper()
    /// when IsMethodPatchedByProxy already returned true (conflict detected).
    /// Applies the queued patch through HarmonyX so both runtimes' patches
    /// coexist on a single detour.  A transpiler is converted by
    /// <see cref="TranspilerBridge"/>; it never needs the native runtime.
    /// </summary>
    public static MethodInfo RouteConflictUpdateWrapper(MethodBase original, object patchInfoObj)
    {
        if (original == null || patchInfoObj == null)
            return original as MethodInfo;

        try
        {
            var piType = patchInfoObj.GetType();

            var prefixes = FieldOf(piType, "prefixes")?.GetValue(patchInfoObj) as Array;
            var postfixes = FieldOf(piType, "postfixes")?.GetValue(patchInfoObj) as Array;
            var transpilers = FieldOf(piType, "transpilers")?.GetValue(patchInfoObj) as Array;
            var finalizers = FieldOf(piType, "finalizers")?.GetValue(patchInfoObj) as Array;

            // A HarmonyLib.PatchInfo we cannot read means the injected call is
            // passing the wrong object (0Harmony_UMM changed its signature).
            // Applying an empty patch set would silently disable the mod, so say
            // exactly what went wrong and leave the method alone.
            if (prefixes == null && postfixes == null && transpilers == null && finalizers == null)
            {
                Melon<Bridge>.Logger.Warning(
                    $"RouteUpdateWrapper: no patch lists on '{piType.FullName}' while routing " +
                    $"\"{Describe(original)}\" — the mod's patch was NOT applied");
                return original as MethodInfo;
            }

            // A transpiler can only live on HarmonyX when the bridge is on: the
            // native calling convention cannot execute a HarmonyX-typed
            // transpiler, and two detours cannot share a method entry.
            if (transpilers is { Length: > 0 } && !TranspilerBridge.Enabled)
            {
                Melon<Bridge>.Logger.Warning(
                    $"RouteUpdateWrapper: \"{Describe(original)}\" needs the transpiler bridge, " +
                    "which is disabled — the mod's patch was NOT applied (enable TranspilerBridge)");
                return original as MethodInfo;
            }

            Trace($"routing {Describe(original)} to HarmonyX");

            var hx = new HarmonyLib.Harmony(
                "UMMBridge.Route." + original.DeclaringType?.Name + "." + original.Name);
            var proc = hx.CreateProcessor(original);

            // Fetch HarmonyX's registry once instead of once per patch: a mod can
            // register dozens of patches on one method.
            Patches existing;
            try { existing = HarmonyLib.Harmony.GetPatchInfo(original); }
            catch { existing = null; }

            bool IsOnHarmonyX(HarmonyLib.HarmonyMethod hm)
            {
                var method = PatchMethodOf(hm);
                if (method == null || existing == null) return false;

                return (existing.Prefixes?.Any(p => p.PatchMethod == method) ?? false)
                    || (existing.Postfixes?.Any(p => p.PatchMethod == method) ?? false)
                    || (existing.Transpilers?.Any(p => p.PatchMethod == method) ?? false)
                    || (existing.Finalizers?.Any(p => p.PatchMethod == method) ?? false);
            }

            var skipped = 0;
            var added = 0;
            var index = 0;

            void Add(Array patches, Action<HarmonyLib.HarmonyMethod> add, string kind)
            {
                if (patches == null) return;

                foreach (var patch in patches)
                {
                    index++;

                    var hm = PatchToHx(patch);
                    if (hm == null)
                    {
                        skipped++;
                        Melon<Bridge>.Logger.Warning(
                            $"RouteUpdateWrapper: could not read the {kind} patch of " +
                            $"\"{Describe(original)}\" — that patch was NOT applied");
                        continue;
                    }

                    if (IsOnHarmonyX(hm)) continue;
                    add(hm);
                    added++;
                }
            }

            Add(prefixes, hm => proc.AddPrefix(hm), "prefix");
            Add(postfixes, hm => proc.AddPostfix(hm), "postfix");
            Add(finalizers, hm => proc.AddFinalizer(hm), "finalizer");

            if (transpilers is { Length: > 0 })
            {
                foreach (var patch in transpilers)
                {
                    index++;

                    var hm = PatchToHx(patch);
                    if (hm == null)
                    {
                        skipped++;
                        Melon<Bridge>.Logger.Warning(
                            $"RouteUpdateWrapper: could not read a transpiler of \"{Describe(original)}\" — " +
                            "the mod's transpiler was NOT applied");
                        continue;
                    }

                    if (!TranspilerBridge.TryWrap(hm, out var wrapped, out var error))
                    {
                        // Keep going: dropping the prefixes/postfixes as well would
                        // turn one unbridgeable transpiler into a completely dead
                        // mod patch.  The warning above names the consequence.
                        skipped++;
                        Melon<Bridge>.Logger.Warning(
                            $"RouteUpdateWrapper: transpiler '{PatchMethodOf(hm)?.Name}' of " +
                            $"\"{Describe(original)}\" could not be bridged ({error}) — " +
                            "that transpiler did NOT run");
                        continue;
                    }

                    if (IsOnHarmonyX(wrapped)) continue;
                    proc.AddTranspiler(wrapped);
                    added++;
                }
            }

            if (added == 0)
            {
                // Nothing new to install: every patch is either already on
                // HarmonyX or could not be converted.  Patching with an empty
                // processor would create a detour for no reason.
                if (skipped > 0)
                    Melon<Bridge>.Logger.Warning(
                        $"RouteUpdateWrapper: none of the {index} patch(es) for \"{Describe(original)}\" " +
                        "could be applied — the mod's patch is inactive");
                else
                    Trace($"{Describe(original)}: patches already on HarmonyX, nothing to add");

                return original as MethodInfo;
            }

            var result = proc.Patch();
            if (result == null)
            {
                Melon<Bridge>.Logger.Warning(
                    $"RouteUpdateWrapper: HarmonyX returned no replacement for \"{Describe(original)}\"");
                return original as MethodInfo;
            }

            SetOwnership(original, false);
            return result;
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"RouteUpdateWrapper failed for \"{Describe(original)}\": {ex.GetType().Name}: {ex.Message}");
            return original as MethodInfo;
        }
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

    private static MethodInfo PatchMethodOf(HarmonyLib.HarmonyMethod hm)
        => hm == null ? null : FieldOf(hm.GetType(), "method")?.GetValue(hm) as MethodInfo;

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

    /// <summary>
    /// HarmonyX prefix on 0Harmony_UMM's Harmony.Patch().  A method entry that
    /// HarmonyX already owns is never detoured natively, and a UMM mod patch
    /// that carries a transpiler is bridged to HarmonyX (see
    /// <see cref="TranspilerBridge"/>) instead of claiming a detour of its own,
    /// so every patch for the method shares one MonoMod runtime.
    /// </summary>
    public static bool Prefix_UMMPatch(
        MethodBase original, object prefix, object postfix, object transpiler, object finalizer)
    {
        if (transpiler == null)
        {
            // The native Patch() below claims this method entry.  If HarmonyX
            // already detoured the same prologue, letting it proceed would put
            // two JMPs on one entry point — refuse instead of corrupting it.
            // When 0Harmony_UMM already owns the entry there is no second JMP
            // to create: the patch only joins the existing native table, so a
            // HarmonyX table (possible only via an out-of-bridge patch) must
            // not veto it.
            if (!IsNativeOwned(original) && HasHarmonyXPatches(original))
            {
                Melon<Bridge>.Logger.Warning(
                    $"Prefix_UMMPatch: HarmonyX owns \"{Describe(original)}\" — native detour skipped");
                return false;
            }

            MarkNativeOwner(original);
            return true;
        }

        // ── Bridge path ────────────────────────────────────────────────
        // A UMM mod's transpiler no longer has to claim a native detour:
        // it is converted to HarmonyX and applied next to every other patch.
        // UMM's own core patches keep the native path — except when HarmonyX
        // already owns the method entry (a MelonLoader mod patched it through
        // ProxyPatch), because no native detour can exist on such a method any
        // more, so the transpiler has to be bridged instead of stacked on top.
        //
        // There is deliberately no "migrate back to native" fallback here:
        // HarmonyX's ManagedMethodPatcher.DetourTo() ignores its target and
        // re-applies the ILHook even after every patch was unpatched, so a
        // migration would leave that detour in place and stack a second JMP on
        // the same prologue — the exact corruption this bridge exists to avoid.
        var harmonyXOwns = HasHarmonyXPatches(original);
        if (TranspilerBridge.Enabled && !IsNativeOwned(original) && (IsUmmModCaller() || harmonyXOwns))
        {
            var bridged = ApplyToHarmonyX(original, prefix, postfix, transpiler, finalizer, out var bridgeError);
            if (bridged != null)
            {
                Melon<Bridge>.Logger.Msg(
                    $"Prefix_UMMPatch: bridged \"{original.DeclaringType?.Name}.{original.Name}\" to HarmonyX");
                return false;
            }

            Melon<Bridge>.Logger.Warning(
                $"Prefix_UMMPatch: bridge failed for \"{original.DeclaringType?.Name}.{original.Name}\" " +
                $"({bridgeError}) — falling back to native");
        }

        // HarmonyX owns the method entry, so a native detour would be a second
        // JMP on the same prologue — refuse it.  HasHarmonyXPatches() also
        // reports "owned" when the patch table cannot be read, which is the safe
        // answer for that case too.  If 0Harmony_UMM already owns the entry the
        // patch merely joins the existing native table, so refusing it would
        // drop the transpiler for no benefit.
        if (harmonyXOwns && !IsNativeOwned(original))
        {
            Melon<Bridge>.Logger.Warning(
                $"Prefix_UMMPatch: HarmonyX owns \"{Describe(original)}\" — native detour skipped");
            return false;
        }

        MarkNativeOwner(original);
        return true;
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
    /// Reflectively invoke 0Harmony_UMM's Harmony.Patch() with the given
    /// parameters.  Used when 0Harmony_UMM already owns the method entry,
    /// when <see cref="TranspilerBridge"/>.Enabled is false, or as the
    /// fallback for a transpiler that could not be converted while HarmonyX
    /// does not own the method.
    /// </summary>
    private static MethodInfo InvokeNativePatch(
        object harmonyIgnored, MethodBase original,
        object prefix, object postfix, object transpiler, object finalizer)
    {
        // Never add a second detour on top of a HarmonyX one; that is the
        // corruption this whole bridge exists to prevent.  When 0Harmony_UMM
        // already owns the entry there is no second detour to create: the
        // patch merely joins the existing native patch table, so a HarmonyX
        // patch table (only possible if something patched outside the bridge)
        // must not veto it.
        if (!IsNativeOwned(original) && HasHarmonyXPatches(original))
        {
            Melon<Bridge>.Logger.Warning(
                $"InvokeNativePatch: HarmonyX owns \"{Describe(original)}\" — native patch skipped");
            return null;
        }

        try
        {
            var harmonyType = harmonyIgnored.GetType();
            var hmType = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod");
            var nativePatch = harmonyType.GetMethod("Patch", new[] {
                typeof(MethodBase), hmType, hmType, hmType, hmType
            });
            if (nativePatch == null)
            {
                Melon<Bridge>.Logger.Warning(
                    $"InvokeNativePatch: 0Harmony_UMM Harmony.Patch(MethodBase, HarmonyMethod x4) not found for \"{Describe(original)}\"");
                return null;
            }

            var nativeResult = (MethodInfo)nativePatch.Invoke(harmonyIgnored, new object[] {
                original, prefix, postfix, transpiler, finalizer
            });

            if (nativeResult != null) SetOwnership(original, true);
            return nativeResult;
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"InvokeNativePatch failed for \"{Describe(original)}\": {ex.Message}");
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
    /// in UMM mod assemblies.  Per-call routing:
    ///   - transpiler == null → translate HarmonyMethod to HarmonyX and apply
    ///     through HarmonyX (single MonoMod for non-transpiler patches).
    ///   - transpiler != null and the bridge is enabled → the transpiler is
    ///     wrapped by <see cref="TranspilerBridge"/> and also applied through
    ///     HarmonyX, so the method keeps exactly one MonoMod detour.
    ///   - transpiler != null with the bridge disabled → 0Harmony_UMM native
    ///     Patch(), because the two transpiler calling conventions differ.
    /// </summary>
    public static MethodInfo ProxyPatch(
        object harmonyIgnored,  // 0Harmony_UMM's Harmony instance
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

        // ── Native detour path ──────────────────────────────────────────
        // If 0Harmony_UMM already owns this method, route ALL further
        // patches (transpiler or not) through 0Harmony_UMM so there's
        // only one MonoMod writing JMP detours.
        if (PatchedMethods.TryGetValue(original, out var fromNative) && fromNative)
        {
            Melon<Bridge>.Logger.Msg(
                $"ProxyPatch: \"{original.DeclaringType?.Name}.{original.Name}\" already native — routing to 0Harmony_UMM");
            return InvokeNativePatch(harmonyIgnored, original, prefix, postfix, transpiler, finalizer);
        }

        // ── HarmonyX path ──────────────────────────────────────────────
        // Transpilers are converted by TranspilerBridge, so every patch for
        // this method can live on HarmonyX — one MonoMod, no cross-runtime
        // detour conflict.  Only an explicitly disabled bridge still needs
        // the native runtime for transpilers.
        if (transpiler != null && !TranspilerBridge.Enabled)
        {
            return InvokeNativePatch(harmonyIgnored, original, prefix, postfix, transpiler, finalizer);
        }

        var result = ApplyToHarmonyX(original, prefix, postfix, transpiler, finalizer, out var error);
        if (result == null && transpiler != null)
        {
            // Never write a native detour over an existing HarmonyX one: that
            // is exactly the corruption this bridge exists to prevent.  Skip
            // the patch (with a reason) when HarmonyX already owns the method.
            if (HasHarmonyXPatches(original))
            {
                Melon<Bridge>.Logger.Warning(
                    $"ProxyPatch: transpiler bridge failed for \"{original.DeclaringType?.Name}.{original.Name}\" " +
                    $"({error}) and HarmonyX already owns the method — patch skipped");
                return null;
            }

            Melon<Bridge>.Logger.Warning(
                $"ProxyPatch: transpiler bridge failed for \"{original.DeclaringType?.Name}.{original.Name}\" " +
                $"({error}) — falling back to 0Harmony_UMM");
            return InvokeNativePatch(harmonyIgnored, original, prefix, postfix, transpiler, finalizer);
        }

        if (result == null)
        {
            Melon<Bridge>.Logger.Warning(
                $"ProxyPatch: HarmonyX patch failed for \"{original.DeclaringType?.Name}.{original.Name}\": {error}");
        }

        return result;
    }

    /// <summary>
    /// Applies a UMM patch through HarmonyX.  A UMM transpiler is wrapped by
    /// <see cref="TranspilerBridge"/> so it can run against HarmonyX's
    /// CodeInstruction stream, which lets every patch for the method share a
    /// single MonoMod runtime.  Returns null (with a reason) on failure.
    /// </summary>
    internal static MethodInfo ApplyToHarmonyX(
        MethodBase original, object prefix, object postfix, object transpiler, object finalizer,
        out string error)
    {
        error = null;

        try
        {
            var id = "UMMBridge.Proxy." + original.DeclaringType?.Name + "." + original.Name;
            var harmony = new HarmonyLib.Harmony(id);
            var proc = harmony.CreateProcessor(original);

            var prefixHx = ToHx(prefix);
            if (prefixHx != null) proc.AddPrefix(prefixHx);
            else if (prefix != null)
                Melon<Bridge>.Logger.Warning(
                    $"ProxyPatch: could not read the prefix of \"{Describe(original)}\" — it was NOT applied");

            var postfixHx = ToHx(postfix);
            if (postfixHx != null) proc.AddPostfix(postfixHx);
            else if (postfix != null)
                Melon<Bridge>.Logger.Warning(
                    $"ProxyPatch: could not read the postfix of \"{Describe(original)}\" — it was NOT applied");

            var finalizerHx = ToHx(finalizer);
            if (finalizerHx != null) proc.AddFinalizer(finalizerHx);
            else if (finalizer != null)
                Melon<Bridge>.Logger.Warning(
                    $"ProxyPatch: could not read the finalizer of \"{Describe(original)}\" — it was NOT applied");

            if (transpiler != null)
            {
                var transpilerHx = ToHx(transpiler);
                if (transpilerHx == null)
                {
                    error = "transpiler method could not be read";
                    return null;
                }

                if (!TranspilerBridge.TryWrap(transpilerHx, out var wrapped, out error))
                    return null;

                proc.AddTranspiler(wrapped);
            }

            var result = proc.Patch();
            if (result == null)
            {
                error = "HarmonyX returned no replacement method";
                return null;
            }

            SetOwnership(original, false);
            return result;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
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
    /// Translates a 0Harmony_UMM HarmonyMethod to a HarmonyX one by reading
    /// its fields by name.
    /// </summary>
    private static HarmonyLib.HarmonyMethod ToHx(object hm)
    {
        if (hm == null) return null;

        var mi = FieldOf(hm.GetType(), "method")?.GetValue(hm) as MethodInfo;
        if (mi == null) return null;

        var hx = new HarmonyLib.HarmonyMethod(mi);

        // 0Harmony_UMM and HarmonyX share the same field names/CLR types for
        // priority, before, after and debug.  Fields that exist on only one
        // side (beforeHard, afterHard, mergedBefore/After) are never read, so
        // no cross-assembly type comparison is needed.
        CopySchedulingFields(hm, hx);
        return hx;
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
