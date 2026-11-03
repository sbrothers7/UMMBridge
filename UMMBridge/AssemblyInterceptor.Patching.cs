using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Cecil IL rewriting for UMM mod assemblies ─────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        /// <summary>
        /// Cecil-rewrite a UMM mod assembly: replace 0Harmony→0Harmony_UMM,
        /// optionally redirect Harmony.Patch() calls through ProxyPatch
        /// (when no transpiler is present), rewrite get_Location() calls
        /// and hardcoded "Mods" paths.
        /// </summary>
        private static byte[] PatchAssemblyFile(string filePath)
        {
            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(filePath); }
            catch { return null; }

            // Cecil resolves external assemblies lazily: writing the module forces
            // it to the type behind every const field, and a module read from a
            // Stream carries no search directory at all (only a path read adds
            // one).  Give it a resolver that knows this mod's neighbourhood, or a
            // const of a sibling DLL's enum fails the whole rewrite.
            using var resolver = CreateModCecilResolver(filePath);

            AssemblyDefinition asmDef;
            try
            {
                asmDef = AssemblyDefinition.ReadAssembly(
                    new MemoryStream(originalBytes),
                    new ReaderParameters { AssemblyResolver = resolver });
            }
            catch
            {
                Melon<Bridge>.Logger.Warning(
                    $"Skipping (not a .NET assembly): {Path.GetFileName(filePath)}");
                return null;
            }

            using (asmDef)
            {
                bool modified = false;
                var harmonyRef = asmDef.MainModule.AssemblyReferences
                    .FirstOrDefault(r => r.Name == "0Harmony");

                if (harmonyRef != null && HarmonyXOnly)
                {
                    Melon<Bridge>.Logger.Msg($"{Path.GetFileName(filePath)} → HarmonyX");
                }
                else if (harmonyRef != null)
                {
                    harmonyRef.Name = "0Harmony_UMM";
                    harmonyRef.PublicKeyToken = Array.Empty<byte>();
                    modified = true;

                    // Assembly-level transpiler check.  With the transpiler
                    // bridge enabled, Patch() calls stay rewritten even for
                    // assemblies that contain transpilers: ProxyPatch converts
                    // each transpiler to HarmonyX instead of letting it claim a
                    // second native detour.  Only a disabled bridge keeps such
                    // assemblies on the native runtime.
                    var bridgeTranspilers = TranspilerBridge.Enabled;
                    var hasTranspiler = ModUsesTranspiler(asmDef);
                    if (bridgeTranspilers || !hasTranspiler)
                    {
                        var rewrittenPatchCalls = RewriteHarmonyPatchCalls(asmDef);
                        modified |= rewrittenPatchCalls;
                        Melon<Bridge>.Logger.Msg(
                            $"{Path.GetFileName(filePath)} → " +
                            (rewrittenPatchCalls
                                ? "ProxyPatch" + (bridgeTranspilers && hasTranspiler ? " (transpilers bridged)" : "")
                                : "0Harmony_UMM (no Patch() calls rewritten)"));
                    }
                    else
                    {
                        Melon<Bridge>.Logger.Msg(
                            $"{Path.GetFileName(filePath)} → 0Harmony_UMM (transpiler assembly)");
                    }
                }

                // Creplay: fix cross-runtime as cast.  PriorityQueue<SkyHookEvent,ulong>
                // lives on Mono's heap; isinst in .NET 4.8 fails type-identity check.
                // Change isinst PriorityQueue`2 → isinst object so the cast always passes.
                if (asmDef.Name.Name == "Creplay")
                    modified |= FixCreplayCrossRuntime(asmDef);

                // Import Bridge.GetAssemblyLocation for Cecil rewriting
                MethodReference getLocationHelper = null;
                try
                {
                    var helper = typeof(Bridge).GetMethod(
                        nameof(Bridge.GetAssemblyLocation),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(Assembly) }, null);
                    if (helper != null)
                        getLocationHelper = asmDef.MainModule.ImportReference(helper);
                }
                catch { }

                // The "Mods" rewrite adapts mods that assume UMM's folder is
                // Mods/.  A mod that names UMMMods itself does not: it tells
                // native UMM's Mods/ and this layout's UMMMods/ apart (Topaz's
                // and Sapphire's duplicate-install check), so its "Mods" means
                // the real Mods/ folder and is left as written.
                var rewritePaths = !NamesModsDir(asmDef);
                if (!rewritePaths)
                    Melon<Bridge>.Logger.Msg(
                        $"{Path.GetFileName(filePath)} names {ModsDirName} itself — its \"Mods\" paths are left as written");

                foreach (var type in asmDef.MainModule.GetAllTypes())
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        if (rewritePaths) modified |= RewriteHardcodedPaths(method);
                        if (getLocationHelper != null)
                            modified |= RewriteAssemblyGetLocation(method, getLocationHelper);
                    }

                if (!modified) return null;

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                Melon<Bridge>.Logger.Msg($"Patched assembly: {Path.GetFileName(filePath)}");
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Replace calls to Assembly.get_Location() with
        /// Bridge.GetAssemblyLocation(Assembly) so that byte-loaded
        /// UMM mods can recover their original file paths at runtime.
        /// HarmonyX's patching of this virtual getter is unreliable
        /// (see: HarmonyX detour limitations).
        /// </summary>
        private static bool RewriteAssemblyGetLocation(MethodDefinition method, MethodReference helper)
        {
            bool modified = false;
            foreach (var inst in method.Body.Instructions)
            {
                if ((inst.OpCode != OpCodes.Call && inst.OpCode != OpCodes.Callvirt) ||
                    inst.Operand is not MethodReference mr ||
                    mr.Name != "get_Location" ||
                    mr.DeclaringType.FullName != "System.Reflection.Assembly")
                    continue;

                // get_Location is a virtual instance getter → callvirt;
                // our helper is a static method → call.
                inst.OpCode = OpCodes.Call;
                inst.Operand = helper;
                modified = true;
            }
            return modified;
        }

        private static bool NamesModsDir(AssemblyDefinition asmDef)
            => asmDef.MainModule.GetAllTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Any(inst => inst.OpCode == OpCodes.Ldstr && inst.Operand is string s && s.Contains(ModsDirName));

        private static bool RewriteHardcodedPaths(MethodDefinition method)
        {
            bool modified = false;
            foreach (var inst in method.Body.Instructions)
            {
                // A URL is never a filesystem path, whatever its segments are called.
                if (inst.OpCode != OpCodes.Ldstr || inst.Operand is not string s || s.Contains("://"))
                    continue;

                string replaced = null;
                if (s == "Mods")
                    replaced = ModsDirName;
                else if (s.EndsWith("/Mods"))
                    replaced = s.Substring(0, s.Length - 4) + ModsDirName;
                else if (s.EndsWith("\\Mods"))
                    replaced = s.Substring(0, s.Length - 4) + ModsDirName;
                else if (s.EndsWith("/Mods/"))
                    replaced = s.Substring(0, s.Length - 5) + ModsDirName + "/";
                else if (s.EndsWith("\\Mods\\"))
                    replaced = s.Substring(0, s.Length - 5) + ModsDirName + "\\";
                else if (s.StartsWith("Mods/"))
                    replaced = ModsDirName + "/" + s.Substring(5);
                else if (s.StartsWith("Mods\\"))
                    replaced = ModsDirName + "\\" + s.Substring(5);
                else if (s.Contains("/Mods/"))
                    replaced = s.Replace("/Mods/", "/" + ModsDirName + "/");
                else if (s.Contains("\\Mods\\"))
                    replaced = s.Replace("\\Mods\\", "\\" + ModsDirName + "\\");

                if (replaced != null && replaced != s)
                {
                    inst.Operand = replaced;
                    modified = true;
                }
            }
            return modified;
        }

        /// <summary>
        /// Creplay's sortedKeyQueue_instance is populated via reflection at
        /// start_recording().  The PriorityQueue&lt;SkyHookEvent,ulong&gt;
        /// instance lives on Mono's heap; isinst in .NET 4.8 fails because
        /// CLR type identity differs between runtimes.  Fix:
        ///   - Field type: PriorityQueue&lt;...&gt; → System.Object
        ///   - isinst target: PriorityQueue`2 → System.Object
        /// This lets the reference pass through; later == comparison works.
        /// </summary>
        private static bool FixCreplayCrossRuntime(AssemblyDefinition asmDef)
        {
            bool modified = false;
            var objectType = asmDef.MainModule.TypeSystem.Object;

            // Change field type to object
            var creplayType = asmDef.MainModule.GetType("CREPLAY.Creplay");
            if (creplayType == null) return false;

            var skqField = creplayType.Fields.FirstOrDefault(f => f.Name == "sortedKeyQueue_instance");
            if (skqField != null && skqField.FieldType.MetadataType != MetadataType.Object)
            {
                skqField.FieldType = objectType;
                modified = true;
            }

            // Change every isinst PriorityQueue`2 to isinst object
            foreach (var type in asmDef.MainModule.GetAllTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var inst in method.Body.Instructions)
                    {
                        if (inst.OpCode == OpCodes.Isinst &&
                            inst.Operand is TypeReference tr &&
                            tr.FullName.StartsWith("System.Collections.Generic.PriorityQueue`2"))
                        {
                            inst.Operand = objectType;  // isinst System.Object
                            modified = true;
                        }
                    }
                }

            if (modified)
                Melon<Bridge>.Logger.Msg(
                    "  Creplay fix: sortedKeyQueue_instance → object");
            return modified;
        }

        // ── JALib pre-claim ─────────────────────────────────────────────

        /// <summary>
        /// Claims every game method a JALib mod targets with [JAPatch] for the
        /// native runtime, before UMM loads any mod.
        ///
        /// <para>JALib boots on a worker thread after UMM has loaded every other
        /// mod, so a bridged mod has usually put a HarmonyX detour on a shared
        /// method (scrFloor.Start, scnGame.Play, ...) before JALib gets there,
        /// and the DetourMethod guard can then only refuse JALib's patch.
        /// Claiming the entry up front sends those earlier patches to the native
        /// table instead; JALib reads that table back through
        /// HarmonySharedState.GetPatchInfo and keeps them when it re-detours —
        /// one runtime, one detour.</para>
        /// </summary>
        internal static void ClaimJALibPatchTargets()
        {
            // Claiming for a runtime that cannot detour would break mods that
            // work today on HarmonyX.
            if (_ummHarmony == null || !NativeDetoursWork || !Directory.Exists(ModsRootPath)) return;

            string[] files;
            try { files = Directory.GetFiles(ModsRootPath, "*.dll", SearchOption.AllDirectories); }
            catch { return; }

            // One index of the loaded types for the whole scan.  AccessTools.TypeByName
            // walks every loaded type on a lookup that is not assembly-qualified, and
            // logs a warning on a miss — and misses are normal here (another game
            // version's class, a mod not installed or not loaded yet), so per-attribute
            // lookups cost ~0.4 s each: 18 s for PACL2 + BetterCalibration.
            var types = new Lazy<Dictionary<string, Type>>(IndexLoadedTypes);
            var claimed = new HashSet<string>();
            foreach (var file in files)
            {
                try
                {
                    // Decoding [JAPatch] resolves JALib for its PatchType enum.
                    // Both are disposed per file: a held handle on JALib.dll would
                    // block JALib's own updater on Windows.
                    using var resolver = CreateModCecilResolver(file);
                    using var asmDef = AssemblyDefinition.ReadAssembly(
                        file, new ReaderParameters { AssemblyResolver = resolver });

                    if (asmDef.Name.Name != "JALib" &&
                        asmDef.MainModule.AssemblyReferences.All(r => r.Name != "JALib"))
                        continue;

                    foreach (var type in asmDef.MainModule.GetAllTypes())
                        foreach (var method in type.Methods)
                            foreach (var attr in method.CustomAttributes)
                            {
                                if (attr.AttributeType.FullName != "JALib.Core.Patch.JAPatchAttribute") continue;

                                // [JAPatch(Type or string class, string method, PatchType, bool)]
                                var args = attr.ConstructorArguments;
                                if (args[1].Value is not string methodName) continue;
                                var target = args[0].Value switch
                                {
                                    TypeReference t => ResolveClaimType(t, types.Value),
                                    string name => ResolveClaimType(name, types.Value),
                                    _ => null,
                                };
                                if (target == null) continue;

                                // ponytail: claims every overload of that name (ArgumentTypes and
                                // GenericName ignored); an extra claim only moves that method's
                                // later mod patches to the native table.
                                foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(target))
                                    if (m.Name == methodName && Bridge.ClaimNative(m))
                                        claimed.Add(Bridge.Describe(m));
                            }
                }
                catch (BadImageFormatException)
                {
                    // A native library shipped next to a mod.
                }
                catch (Exception e)
                {
                    Melon<Bridge>.Logger.Warning(
                        $"Claim: could not read [JAPatch] targets from {Path.GetFileName(file)}: {e.Message}");
                }
            }

            if (claimed.Count > 0)
                Melon<Bridge>.Logger.Msg(
                    $"Claimed {claimed.Count} method(s) for JALib on 0Harmony_UMM: " +
                    string.Join(", ", claimed));
        }

        /// <summary>
        /// Every loaded type by full name, then by short name where that is free:
        /// the same two passes AccessTools.TypeByName makes, done once.
        /// </summary>
        private static Dictionary<string, Type> IndexLoadedTypes()
        {
            var all = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { all.AddRange(assembly.GetTypes()); }
                catch (ReflectionTypeLoadException e) { all.AddRange(e.Types.Where(t => t != null)); }
                catch { }
            }

            var index = new Dictionary<string, Type>();
            foreach (var type in all)
                if (type.FullName != null && !index.ContainsKey(type.FullName)) index[type.FullName] = type;
            foreach (var type in all)
                if (!index.ContainsKey(type.Name)) index[type.Name] = type;
            return index;
        }

        /// <summary>A [JAPatch] typeof() target; null when it is not loaded.</summary>
        internal static Type ResolveClaimType(TypeReference type, Dictionary<string, Type> index)
        {
            if (type is GenericInstanceType generic)
            {
                var definition = ResolveClaimType(generic.ElementType, index);
                var arguments = generic.GenericArguments.Select(a => ResolveClaimType(a, index)).ToArray();
                if (definition == null || arguments.Any(a => a == null)) return null;
                try { return definition.MakeGenericType(arguments); }
                catch (ArgumentException) { return null; }
            }

            return index.TryGetValue(type.FullName.Replace('/', '+'), out var found) ? found : null;
        }

        /// <summary>A [JAPatch] class-name target; null when it is not loaded.</summary>
        internal static Type ResolveClaimType(string name, Dictionary<string, Type> index)
            => index.TryGetValue(name, out var found) ? found
                : name.Contains(',') ? Type.GetType(name, false) : null;

        // ── Cecil assembly resolution ───────────────────────────────────

        /// <summary>
        /// Resolver handed to Cecil for a mod-owned file.
        ///
        /// <para>Writing a module back makes Cecil resolve types lazily — a
        /// <c>const</c> field forces <c>MetadataBuilder.GetConstantType</c> to
        /// call <c>TypeReference.Resolve()</c>.  Modules are read from a
        /// <see cref="MemoryStream"/> here, and only a path read teaches
        /// <see cref="DefaultAssemblyResolver"/> where to look, so without this
        /// the rewrite dies on any const typed by a sibling DLL's enum:
        /// CheryTools.Sonnet.Framework has a const of an ImGui.NET enum next to
        /// ImGui.NET.dll, and the failure surfaced as
        /// <c>AssemblyResolutionException: Failed to resolve assembly: 'ImGui.NET'</c>
        /// while loading the mod.</para>
        /// </summary>
        private sealed class ModCecilResolver : DefaultAssemblyResolver
        {
            public override AssemblyDefinition Resolve(AssemblyNameReference name)
                => Resolve(name, new ReaderParameters { AssemblyResolver = this });

            public override AssemblyDefinition Resolve(
                AssemblyNameReference name, ReaderParameters parameters)
            {
                // 0Harmony_UMM only exists as an in-memory rename of vanilla
                // 0Harmony, so a reference to it can only be resolved from disk
                // under its original name.
                if (name.Name == "0Harmony_UMM")
                    name = new AssemblyNameReference("0Harmony", name.Version);

                try
                {
                    return base.Resolve(name, new ReaderParameters
                    {
                        AssemblyResolver = this,
                        ReadingMode = ReadingMode.Deferred,
                    });
                }
                catch (AssemblyResolutionException)
                {
                }

                // Last chance: a copy the runtime already loaded.  This covers
                // assemblies whose folder we never guessed, and mod assemblies
                // that were byte-loaded from a cache file.
                var loaded = FindLoadedAssembly(name.Name);
                if (loaded != null && !string.IsNullOrEmpty(loaded.Location))
                {
                    try
                    {
                        return AssemblyDefinition.ReadAssembly(loaded.Location, new ReaderParameters
                        {
                            AssemblyResolver = this,
                            ReadingMode = ReadingMode.Deferred,
                        });
                    }
                    catch
                    {
                    }
                }

                throw new AssemblyResolutionException(name);
            }
        }

        /// <summary>
        /// A Cecil resolver that can see everything a mod might reference: its
        /// own folder (UMM cache files and the DLLs shipped beside them), every
        /// UMM mod folder, the game's Managed folder, UMM's folder (vanilla
        /// 0Harmony) and MelonLoader's (HarmonyX, Cecil itself).
        /// </summary>
        private static ModCecilResolver CreateModCecilResolver(string filePath)
        {
            var resolver = new ModCecilResolver();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                try
                {
                    if (!Directory.Exists(dir)) return;
                    var full = Path.GetFullPath(dir);
                    if (!seen.Add(full)) return;
                    resolver.AddSearchDirectory(full);
                }
                catch
                {
                }
            }

            // The file itself first: a mod keeps its dependencies next to it
            // (CheryTools holds ImGui.NET.dll there and its own DLLs one level
            // down in Modules\).
            var ownDir = Path.GetDirectoryName(filePath);
            Add(ownDir);
            if (ownDir != null)
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(ownDir))
                        Add(sub);
                }
                catch
                {
                }
            }

            // Every mod folder, so one mod can resolve a dependency shipped by
            // another.
            Add(ModsRootPath);
            try
            {
                foreach (var dir in Directory.GetDirectories(ModsRootPath, "*", SearchOption.AllDirectories))
                    Add(dir);
            }
            catch
            {
            }

            // Runtime assemblies.
            try { Add(Path.GetDirectoryName(typeof(UnityEngine.GameObject).Assembly.Location)); }
            catch { }
            try { Add(Path.GetDirectoryName(typeof(UnityModManagerNet.UnityModManager).Assembly.Location)); }
            catch { }
            try
            {
                var melonDir = Path.GetDirectoryName(typeof(AssemblyDefinition).Assembly.Location);
                Add(melonDir);

                var melonRoot = Path.GetDirectoryName(melonDir);
                Add(melonRoot);
                if (melonRoot != null)
                {
                    foreach (var sub in new[] { "net35", "net472", "net6", "Dependencies" })
                        Add(Path.Combine(melonRoot, sub));

                    var deps = Path.Combine(melonRoot, "Dependencies");
                    Add(deps);
                    Add(Path.Combine(deps, "SupportModules"));
                }
            }
            catch
            {
            }
            try { Add(Path.GetDirectoryName(typeof(Bridge).Assembly.Location)); }
            catch { }

            return resolver;
        }
    }
}
