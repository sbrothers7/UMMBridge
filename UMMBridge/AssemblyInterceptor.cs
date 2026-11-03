using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Mono.Cecil;
using Mono.Cecil.Cil;

// ── Assembly interceptor ────────────────────────────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        private static bool _initialized;
        internal static Assembly _ummHarmony;
        private static Assembly _harmonyX;

        /// <summary>
        /// Directory name under the game root where UMM mods reside.
        /// Used for path checks, Cecil IL rewriting, and runtime resolution.
        /// </summary>
        internal const string ModsDirName = "UMMMods";

        /// <summary>
        /// Full path to the UMM mods directory.
        /// Derived from ModsDirName at first access.
        /// </summary>
        internal static string ModsRootPath =>
            Path.Combine(MelonEnvironment.GameRootDirectory, ModsDirName);

        private static readonly Dictionary<string, string> _assemblyToDir = new();
        private static readonly Dictionary<string, Assembly> _assemblyCache = new();

        static AssemblyInterceptor()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = args.Name.Split(',')[0].Trim();

            if (name == "0Harmony_UMM" && _ummHarmony != null)
                return _ummHarmony;

            if (name == "0Harmony")
            {
                // A rewritten mod asks for 0Harmony_UMM, so this only happens
                // when the rewrite was missed.  The mod was compiled against
                // vanilla 0Harmony and HarmonyX is not a superset of it: it has
                // no PatchClassProcessor.Unpatch, no HarmonyLib.AccessToolsExtensions
                // and no AccessTools.MethodDelegate(MethodInfo, object, bool, Type[]).
                // Serve the 2.3.6 surface the mod was built against instead of
                // letting it bind HarmonyX and die on MissingMethodException.
                // In HarmonyX-only mode an unrewritten reference is the point.
                if (_ummHarmony != null && args.RequestingAssembly != null &&
                    IsRegisteredModAssembly(args.RequestingAssembly))
                {
                    Melon<Bridge>.Logger.Warning(
                        $"0Harmony requested by mod assembly " +
                        $"'{args.RequestingAssembly.GetName().Name}' was not rewritten " +
                        "to 0Harmony_UMM; serving the vanilla 2.3.6 surface");

                    if (_ummHarmony != null)
                        return _ummHarmony;
                }
                if (_harmonyX != null)
                    return _harmonyX;
            }

            // Core runtime assemblies must never be served out of a mods folder:
            // mods routinely bundle their own 0Harmony/Newtonsoft copies and a
            // stray copy there cannot satisfy the vanilla typing this bridge relies on.
            if (IsCoreAssemblyName(name))
            {
                // Serve the live copy when there is one: UMM itself lives in
                // Managed\UnityModManager, which the default probe does not
                // cover, so mods referencing UnityModManager only bind because
                // the already-loaded instance is found by simple name.
                return FindLoadedAssembly(name);
            }

            // Return an already-loaded assembly if one exists.  Prevents
            // duplicate loads of game assemblies from UMMMods directories,
            // which would create second copies of the same generic types
            // and break cross-runtime as casts (Type#1 ≠ Type#2).
            var existing = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
            if (existing != null)
                return existing;

            lock (_assemblyToDir)
            {
                if (_assemblyCache.TryGetValue(name, out var cached))
                    return cached;

                if (_assemblyToDir.TryGetValue(name, out var dir))
                {
                    var path = Path.Combine(dir, name + ".dll");
                    if (File.Exists(path))
                    {
                        // Load through the Cecil pipeline: a mod's compile-time
                        // dependency can itself reference 0Harmony (e.g. a UMM
                        // loader whose core DLL lives next to it), and binding
                        // it raw leaves it on HarmonyX.
                        var asm = LoadModAssembly(path);
                        if (asm != null)
                        {
                            _assemblyCache[name] = asm;
                            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} from {dir}");
                        }
                        return asm;
                    }
                    return null;
                }
            }

            if (Directory.Exists(ModsRootPath))
            {
                try
                {
                    var found = Directory.GetFiles(ModsRootPath, name + ".dll", SearchOption.AllDirectories)
                                        .FirstOrDefault();
                    if (found != null)
                    {
                        var modDir = Path.GetDirectoryName(found);
                        var asm = LoadModAssembly(found);
                        if (asm != null)
                        {
                            lock (_assemblyToDir)
                            {
                                _assemblyToDir[name] = modDir;
                                _assemblyCache[name] = asm;
                            }
                            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} (scanned from {modDir})");
                        }
                        return asm;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Loads a mod-owned assembly through the Cecil rewrite, then from bytes.
        ///
        /// <para>Bytes, not LoadFrom: LoadFrom would publish the file into the
        /// LoadFrom context and let the CLR satisfy the mod's compile-time
        /// dependencies by probing sibling files itself, returning them raw.
        /// That is exactly how JipperOverlayer.dll kept binding HarmonyX
        /// 0Harmony and blew up on PatchClassProcessor.Unpatch().  With the
        /// bytes load the dependency is unresolvable, so it comes back through
        /// <see cref="OnAssemblyResolve"/> and gets rewritten here.</para>
        /// </summary>
        private static Assembly LoadModAssembly(string path)
        {
            try
            {
                RegisterModDirectory(path);

                var patched = PatchAssemblyFile(path);
                if (patched == null)
                {
                    // Nothing to rewrite.  Still load from bytes so that the
                    // assembly's own dependencies cannot be probed from disk
                    // behind the rewriter's back.
                    try { patched = File.ReadAllBytes(path); }
                    catch (Exception e)
                    {
                        Melon<Bridge>.Logger.Warning(
                            $"AssemblyResolve: cannot read {Path.GetFileName(path)}: {e.Message}");
                        return null;
                    }
                }

                _currentLoadingPath = path;
                try
                {
                    var asm = Assembly.Load(patched);
                    _assemblyOriginalPath.Add(asm, path);
                    return asm;
                }
                finally
                {
                    _currentLoadingPath = null;
                }
            }
            catch (Exception e)
            {
                Melon<Bridge>.Logger.Warning(
                    $"AssemblyResolve: failed to load {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Assemblies owned by the loader/runtime.  A copy of one of these inside
        /// a mods folder must not be loaded by the bridge: it would create a
        /// second identity (and HarmonyX's 0Harmony does not carry the vanilla
        /// APIs UMM mods are compiled against).
        /// </summary>
        private static bool IsCoreAssemblyName(string name)
        {
            switch (name)
            {
                case "0Harmony":
                case "0Harmony_UMM":
                case "0HarmonyX":
                case "UnityModManager":
                case "dnlib":
                case "MelonLoader":
                case "Assembly-CSharp":
                case "Assembly-CSharp-firstpass":
                case "mscorlib":
                case "netstandard":
                    return true;
            }

            return name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Mono", StringComparison.Ordinal)
                || name.StartsWith("Unity.", StringComparison.Ordinal)
                || name.StartsWith("UnityEngine", StringComparison.Ordinal)
                || name.StartsWith("Il2Cpp", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("Newtonsoft.Json", StringComparison.Ordinal);
        }

        /// <summary>
        /// Assembly simple name of a path, tolerating UMM's cache naming
        /// (<c>Foo.dll.1234.cache</c> names the assembly <c>Foo</c>).
        /// </summary>
        private static string GetModuleName(string path)
        {
            var file = Path.GetFileName(path);
            var cut = file.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
            return cut >= 0 ? file.Substring(0, cut) : Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>
        /// True when an assembly with this simple name is already live in the
        /// domain.  Re-loading it from a mods folder would fork its identity.
        /// </summary>
        private static bool IsAlreadyLoaded(string name)
            => FindLoadedAssembly(name) != null;

        /// <summary>The already-loaded assembly with this simple name, if any.</summary>
        private static Assembly FindLoadedAssembly(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    return asm;
            }
            return null;
        }

        /// <summary>
        /// True when the assembly was seen in a UMMMods directory.  Mods are
        /// registered here before their Cecil rewrite runs, so this is also
        /// how the bridge tells a mod call apart from UMM's own core calls.
        /// </summary>
        internal static bool IsRegisteredModAssembly(Assembly assembly)
        {
            if (assembly == null) return false;
            var name = assembly.GetName().Name;
            lock (_assemblyToDir)
                return _assemblyToDir.ContainsKey(name);
        }

        private static void RegisterModDirectory(string modAssemblyPath)
        {
            var dir = Path.GetDirectoryName(modAssemblyPath);
            if (dir == null) return;

            lock (_assemblyToDir)
            {
                var mainName = Path.GetFileNameWithoutExtension(modAssemblyPath);
                if (!_assemblyToDir.ContainsKey(mainName))
                    _assemblyToDir[mainName] = dir;

                foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                {
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!_assemblyToDir.ContainsKey(name))
                        _assemblyToDir[name] = dir;
                }
            }
        }

        // ── Initialization ──────────────────────────────────────────────

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _harmonyX = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "0Harmony" &&
                                     a.GetName().Version.Major >= 2);

            string harmonyXOnlyReason = null;
            if (TranspilerBridge.ReadSetting("UMMBRIDGE_HARMONYX_ONLY", "HarmonyXOnly", false))
            {
                harmonyXOnlyReason = "HarmonyXOnly is set";
            }
            else if (LoadUmmHarmonyAs_UMM() is not { } ummHarmonyBytes)
            {
                harmonyXOnlyReason = "UMM 0Harmony.dll not found next to UnityModManager.dll";
            }
            else
            {
                _ummHarmony = Assembly.Load(ummHarmonyBytes);
                Melon<Bridge>.Logger.Msg($"Loaded UMM 0Harmony as 0Harmony_UMM v{_ummHarmony.GetName().Version}");
                ProbeNativeDetours();
                if (!NativeDetoursWork)
                {
                    // Every 0Harmony_UMM call would fail here — routed patches
                    // too, since PatchClassProcessor still records them through
                    // its own HarmonySharedState, which needs MonoMod.Core.
                    _ummHarmony = null;
                    harmonyXOnlyReason = "0Harmony_UMM cannot detour on this platform";
                }
            }

            if (harmonyXOnlyReason != null)
            {
                HarmonyXOnly = true;
                Melon<Bridge>.Logger.Msg(
                    $"HarmonyX-only mode ({harmonyXOnlyReason}): UMM mods keep their 0Harmony reference " +
                    "and patch through MelonLoader's HarmonyX");
            }

            try
            {
                var h = new HarmonyLib.Harmony("UMMBridge.AssemblyInterceptor");
                var loadFrom = AccessTools.Method(typeof(Assembly), "LoadFrom", new[] { typeof(string) });
                var prefix = AccessTools.Method(typeof(AssemblyInterceptor), nameof(Prefix_LoadFrom));

                if (loadFrom != null && prefix != null)
                {
                    h.Patch(loadFrom, new HarmonyMethod(prefix));
                    Melon<Bridge>.Logger.Msg(
                        "Assembly.LoadFrom interceptor active");
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning($"Interceptor initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True when UMM mods run on MelonLoader's HarmonyX alone: their
        /// 0Harmony reference is left as it is, nothing is routed and
        /// 0Harmony_UMM is not used.  Set when it is asked for, when UMM's
        /// 0Harmony.dll is missing, or when 0Harmony_UMM cannot detour on this
        /// machine (Apple Silicon).  Mods that call into Harmony internals
        /// (JALib) or the few 2.3 APIs HarmonyX lacks do not work in this mode.
        /// </summary>
        internal static bool HarmonyXOnly { get; private set; }

        private static byte[] LoadUmmHarmonyAs_UMM()
        {
            Assembly ummAssembly;
            try
            {
                ummAssembly = typeof(UnityModManagerNet.UnityModManager).Assembly;
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"UnityModManagerNet.UnityModManager type not found. UnityModManager may be missing: {ex.Message}");
                return null;
            }

            var ummDir = Path.GetDirectoryName(ummAssembly.Location);
            var path = Path.Combine(ummDir, "0Harmony.dll");
            if (!File.Exists(path)) return null;

            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(path); }
            catch { return null; }

            using (var ms = new MemoryStream(originalBytes))
            using (var asmDef = AssemblyDefinition.ReadAssembly(ms))
            {
                var originalVersion = asmDef.Name.Version;
                asmDef.Name = new AssemblyNameDefinition("0Harmony_UMM", originalVersion);
                asmDef.Name.PublicKeyToken = Array.Empty<byte>();
                asmDef.Name.PublicKey = Array.Empty<byte>();

                // ── Only Cecil injection: PatchFunctions.UpdateWrapper() ──
                // CreateClassProcessor/PatchAll calls bypass Harmony.Patch()
                // and reach UpdateWrapper via ProcessPatchJob.  This injection
                // routes a conflicting job through RouteConflictUpdateWrapper,
                // which applies it on HarmonyX instead of writing a second
                // native detour.
                var patchFunctionsType = asmDef.MainModule.GetType("HarmonyLib.PatchFunctions");
                if (patchFunctionsType == null)
                {
                    Melon<Bridge>.Logger.Warning(
                        "0Harmony_UMM: HarmonyLib.PatchFunctions not found — PatchAll() jobs will bypass the conflict router");
                }
                else
                {
                    var tracker3 = typeof(Bridge).GetMethod(
                        nameof(Bridge.IsMethodPatchedByProxy),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase) }, null);
                    var routeMethod = typeof(Bridge).GetMethod(
                        nameof(Bridge.RouteConflictUpdateWrapper),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase), typeof(object) }, null);

                    if (tracker3 == null || routeMethod == null)
                    {
                        Melon<Bridge>.Logger.Warning(
                            "0Harmony_UMM: Bridge conflict-routing methods not found — PatchAll() jobs will bypass the conflict router");
                    }
                    else
                    {
                        var trackerRef3 = asmDef.MainModule.ImportReference(tracker3);
                        var routeRef = asmDef.MainModule.ImportReference(routeMethod);
                        var injected = 0;

                        foreach (var m in patchFunctionsType.Methods)
                        {
                            if (m.Name != "UpdateWrapper" || !m.IsStatic || !m.HasBody) continue;

                            // The hand-written IL below assumes exactly
                            //   static MethodInfo UpdateWrapper(MethodBase, PatchInfo)
                            // Anything else (extra parameter, PatchInfo turned into
                            // a value type, different return) would emit IL that is
                            // invalid for the callee and only fails when
                            // 0Harmony_UMM is loaded — check the shape up front.
                            if (m.ReturnType.FullName != "System.Reflection.MethodInfo") continue;
                            if (m.Parameters.Count != 2) continue;
                            if (m.Parameters[0].ParameterType.FullName != "System.Reflection.MethodBase") continue;
                            if (m.Parameters[1].ParameterType.FullName != "HarmonyLib.PatchInfo") continue;
                            if (m.Body.Instructions.Count == 0) continue;

                            // A second rewrite of the same assembly must not inject
                            // the router twice (it would recurse).
                            if (m.Body.Instructions.Any(inst =>
                                    inst.OpCode == OpCodes.Call &&
                                    inst.Operand is MethodReference call &&
                                    call.Name == nameof(Bridge.IsMethodPatchedByProxy)))
                                continue;

                            var il = m.Body.GetILProcessor();
                            var first = m.Body.Instructions[0];

                            // ldarg.0 → IsMethodPatchedByProxy → brfalse FIRST
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, trackerRef3));
                            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));

                            // Conflict → let RouteConflictUpdateWrapper apply the
                            // job on HarmonyX (it returns the wrapper method).
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[1]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, routeRef));
                            il.InsertBefore(first, il.Create(OpCodes.Ret));

                            injected++;
                        }

                        if (injected == 0)
                        {
                            // Silently shipping without the router would let
                            // PatchAll() write a second native detour onto a
                            // HarmonyX-owned method, so say so loudly.
                            Melon<Bridge>.Logger.Warning(
                                "0Harmony_UMM: no PatchFunctions.UpdateWrapper(MethodBase, PatchInfo) overload found — " +
                                "PatchAll() jobs will bypass the conflict router");
                        }
                        else
                        {
                            Melon<Bridge>.Logger.Msg(
                                $"0Harmony_UMM: conflict router injected into {injected} UpdateWrapper overload(s)");
                        }
                    }
                }

                InjectPatchInfoSync(asmDef);
                RedirectNativeDetours(asmDef);

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Cecil-inject a call to Bridge.OnPatchInfoUpdated at the top of
        /// 0Harmony_UMM's <c>HarmonySharedState.UpdatePatchInfo(MethodBase,
        /// MethodInfo, PatchInfo)</c>, where every change to its registry is
        /// stored: Patch, PatchAll, Unpatch — and JALib's Unpatch, which calls
        /// it through reflection after its own copy of UpdateWrapper.  For a
        /// method routed to HarmonyX the bridge brings HarmonyX in step there.
        /// </summary>
        private static void InjectPatchInfoSync(AssemblyDefinition asmDef)
        {
            var update = asmDef.MainModule.GetType("HarmonyLib.HarmonySharedState")?.Methods.FirstOrDefault(m =>
                m.Name == "UpdatePatchInfo" && m.IsStatic && m.HasBody &&
                m.Parameters.Count == 3 &&
                m.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase" &&
                m.Parameters[2].ParameterType.FullName == "HarmonyLib.PatchInfo");
            if (update == null)
            {
                Melon<Bridge>.Logger.Warning(
                    "0Harmony_UMM: HarmonySharedState.UpdatePatchInfo not found — unpatching a mod patch " +
                    "that lives on HarmonyX will leave it active");
                return;
            }

            // Short forms on purpose: JALib reverse-patches this method into a
            // two-parameter copy (original, patchInfo) whose transpiler turns
            // each ldarg.2 into ldarg.1.  A long-form ldarg 2 would slip past
            // it and read past the end of that copy's arguments.
            var il = update.Body.GetILProcessor();
            var first = update.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
            il.InsertBefore(first, il.Create(OpCodes.Call,
                asmDef.MainModule.ImportReference(typeof(Bridge).GetMethod(nameof(Bridge.OnPatchInfoUpdated)))));
            Melon<Bridge>.Logger.Msg("0Harmony_UMM: registry sync injected into HarmonySharedState.UpdatePatchInfo");
        }

        /// <summary>
        /// Cecil-replace the only two places where 0Harmony_UMM reaches its own
        /// MonoMod.Core: <c>PatchTools.DetourMethod(MethodBase, MethodBase)</c>
        /// and <c>AccessTools.Identifiable(MethodInfo)</c>.  Both now call into
        /// Bridge, which writes the detour with MelonLoader's
        /// MonoMod.RuntimeDetour — the engine HarmonyX uses.  0Harmony_UMM keeps
        /// its own 2.3 internals (what JALib needs) but no longer needs a
        /// MonoMod.Core backend for the CPU, which 2.3.x lacks for Arm64.
        ///
        /// <para>Every native detour ends in DetourMethod: UpdateWrapper,
        /// ReversePatch, and also code that never reaches the routed
        /// UpdateWrapper.  JALib applies [JAPatch] through its own reverse-patched
        /// copy of UpdateWrapper (the router prologue cut off) and records the
        /// result through its own copies of HarmonySharedState.UpdatePatchInfo,
        /// so neither of those ever sees its patches — but the copy still calls
        /// the real DetourMethod.  Bridge.DetourNative records native ownership
        /// there, so later mod patches join the native table, and refuses a
        /// native detour on an entry HarmonyX already owns.</para>
        /// </summary>
        private static void RedirectNativeDetours(AssemblyDefinition asmDef)
        {
            var module = asmDef.MainModule;
            var detour = module.GetType("HarmonyLib.PatchTools")?.Methods.FirstOrDefault(m =>
                m.Name == "DetourMethod" && m.IsStatic && m.HasBody &&
                m.ReturnType.FullName == "System.Void" &&
                m.Parameters.Count == 2 &&
                m.Parameters.All(p => p.ParameterType.FullName == "System.Reflection.MethodBase"));
            var identifiable = module.GetType("HarmonyLib.AccessTools")?.Methods.FirstOrDefault(m =>
                m.Name == "Identifiable" && m.IsStatic && m.HasBody &&
                m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.Reflection.MethodInfo");

            if (detour == null || identifiable == null)
            {
                Melon<Bridge>.Logger.Warning(
                    "0Harmony_UMM: PatchTools.DetourMethod or AccessTools.Identifiable not found — " +
                    "native detours use 0Harmony_UMM's own MonoMod.Core and go untracked");
                return;
            }

            ReplaceBody(detour, typeof(Bridge).GetMethod(nameof(Bridge.DetourNative)));
            ReplaceBody(identifiable, typeof(Bridge).GetMethod(nameof(Bridge.Identifiable)));
            _detoursRedirected = true;
            Melon<Bridge>.Logger.Msg("0Harmony_UMM: native detours redirected to MelonLoader's MonoMod.RuntimeDetour");
        }

        private static bool _detoursRedirected;

        /// <summary>Body becomes: load every argument, call target, ret.</summary>
        private static void ReplaceBody(MethodDefinition method, MethodInfo target)
        {
            var body = method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            var il = body.GetILProcessor();
            foreach (var p in method.Parameters) il.Emit(OpCodes.Ldarg, p);
            il.Emit(OpCodes.Call, method.Module.ImportReference(target));
            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// False when 0Harmony_UMM cannot write a detour on this machine.
        /// Only checked when the redirect above could not be applied: then
        /// 0Harmony_UMM's own MonoMod.Core does the detours, and in 0Harmony
        /// 2.3.x it only supports x86/x64 (MacOSSystem throws
        /// NotImplementedException for anything but x86_64, and
        /// PlatformTriple.CreateCurrentArchitecture does the same for
        /// Arm/Arm64).
        /// </summary>
        internal static bool NativeDetoursWork { get; private set; } = true;

        private static void ProbeNativeDetours()
        {
            if (_detoursRedirected) return;
            try
            {
                _ummHarmony.GetType("MonoMod.Core.DetourFactory")
                    ?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null, null);
            }
            catch (Exception e)
            {
                NativeDetoursWork = false;
                Melon<Bridge>.Logger.Warning(
                    $"0Harmony_UMM cannot detour on this platform ({(e.InnerException ?? e).GetType().Name}) — " +
                    "JALib and JALib-based mods will not work");
            }
        }

        // ── Harmony Prefix: LoadFrom ────────────────────────────────────

        /// <summary>
        /// Tracks the original file path for assemblies loaded via
        /// Assembly.Load(byte[]) so Bridge.GetAssemblyLocation() can
        /// recover the real path at runtime (used by Cecil-rewritten
        /// get_Location calls in patched mods).
        /// </summary>
        internal static readonly ConditionalWeakTable<Assembly, string> _assemblyOriginalPath = new();

        /// <summary>
        /// Set before Assembly.Load() so that static constructors
        /// can still resolve their original path via Bridge.GetAssemblyLocation()
        /// before the CWT entry is populated.
        /// </summary>
        [ThreadStatic]
        internal static string _currentLoadingPath;

        private static bool Prefix_LoadFrom(string assemblyFile, ref Assembly __result)
        {
            if (!assemblyFile.Contains(ModsDirName))
                return true;

            try
            {
                var moduleName = GetModuleName(assemblyFile);

                // A runtime assembly that a mod happens to ship next to itself is
                // not a mod.  Letting the CLR satisfy it keeps a single identity
                // for it; loading it from here would fork the type system.
                if (IsCoreAssemblyName(moduleName) || IsAlreadyLoaded(moduleName))
                    return true;

                RegisterModDirectory(assemblyFile);

                var patched = PatchAssemblyFile(assemblyFile);
                if (patched == null)
                {
                    // Nothing to rewrite in this file, but still take over the
                    // load.  Returning true lets UMM's LoadFrom publish the file
                    // into the LoadFrom context, and the CLR then satisfies the
                    // mod's own compile-time dependencies by probing that same
                    // folder — handing them back raw and unrewritten.
                    // JipperOverlayer.dll (the core DLL sitting next to its UMM
                    // loader) was bound to HarmonyX 0Harmony exactly that way.
                    try { patched = File.ReadAllBytes(assemblyFile); }
                    catch { return true; }
                }

                // Load from bytes, never LoadFrom.  The bytes stay patched, but
                // more importantly a no-context assembly cannot satisfy its own
                // compile-time references by probing sibling files — each of them
                // comes back through OnAssemblyResolve and gets rewritten too.
                // (The no-context copy of a *game* assembly would fork its
                // generic types, so the resolve handler always prefers a live
                // copy.)
                _currentLoadingPath = assemblyFile;
                try
                {
                    var asm = Assembly.Load(patched);
                    _assemblyOriginalPath.Add(asm, assemblyFile);
                    __result = asm;
                    return false;
                }
                finally
                {
                    _currentLoadingPath = null;
                }
            }
            catch (Exception e)
            {
                // Whatever goes wrong in the rewrite, the mod still has to load.
                // Falling back to its own bytes (not to LoadFrom) keeps the
                // no-context guarantee — and therefore keeps the rewrite
                // guarantee for its dependencies — and only leaves this one file
                // unpatched.
                Melon<Bridge>.Logger.Warning(
                    $"LoadFrom interception failed for {Path.GetFileName(assemblyFile)} " +
                    $"({e.GetType().Name}: {e.Message}) — loading it unrewritten");

                _currentLoadingPath = assemblyFile;
                try
                {
                    var raw = Assembly.Load(File.ReadAllBytes(assemblyFile));
                    _assemblyOriginalPath.Add(raw, assemblyFile);
                    __result = raw;
                    return false;
                }
                catch
                {
                    return true;
                }
                finally
                {
                    _currentLoadingPath = null;
                }
            }
        }
    }
}
