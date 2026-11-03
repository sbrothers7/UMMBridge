using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

// ── Transpiler bridge ───────────────────────────────────────────────
//
// UMM mods ship transpilers written against 0Harmony 2.3.6's
// HarmonyLib.CodeInstruction.  HarmonyX uses a *different* CodeInstruction
// type (same assembly name, different identity), so a transpiler cannot be
// handed to HarmonyX directly.
//
// This bridge wraps such a transpiler in a generated HarmonyX-signature
// method: instructions are converted on the way in, the original transpiler
// runs against 0Harmony_UMM's own CodeInstruction instances, and the result
// is converted back.  The transpiler then lives on HarmonyX together with
// every other patch, which is what makes cross-runtime coexistence possible.
//
// The class deliberately has no MelonLoader/Unity dependency so its
// conversion logic can be exercised outside the game.

namespace UMMBridge
{
    internal static class TranspilerBridge
    {
        /// <summary>
        /// Whether transpilers are bridged onto HarmonyX.  Read from the
        /// UMMBRIDGE_TRANSPILER_BRIDGE environment variable or UMMBridge.cfg
        /// next to the plugin; defaults to true.
        /// </summary>
        internal static bool Enabled { get; private set; } = true;

        /// <summary>Log sink, wired up by <see cref="Bridge"/> at startup.</summary>
        internal static Action<string> Log = _ => { };

        /// <summary>Warning sink, wired up by <see cref="Bridge"/> at startup.</summary>
        internal static Action<string> Warn = _ => { };

        private static readonly object Gate = new();
        private static readonly Dictionary<MethodInfo, MethodInfo> WrapperCache = new();
        private static readonly Dictionary<Type, InstructionShape> Shapes = new();
        private static readonly Dictionary<Type, BlockShape> BlockShapes = new();
        private static readonly List<Entry> Entries = new();

        private static ModuleBuilder _module;
        private static int _typeCounter;

        // ── Configuration ───────────────────────────────────────────────

        internal static void Initialize()
        {
            Enabled = ReadSetting("UMMBRIDGE_TRANSPILER_BRIDGE", "TranspilerBridge", true);
            Log($"Transpiler bridge: {(Enabled ? "enabled" : "disabled")}");
        }

        /// <summary>
        /// A boolean switch: the environment variable wins, then
        /// <c>key=value</c> in UMMBridge.cfg next to the plugin, then the fallback.
        /// </summary>
        internal static bool ReadSetting(string envName, string key, bool fallback)
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(envName);
                if (!string.IsNullOrEmpty(env))
                    return !IsFalse(env);
            }
            catch { }

            try
            {
                var dir = Path.GetDirectoryName(typeof(TranspilerBridge).Assembly.Location);
                var cfg = Path.Combine(dir ?? ".", "UMMBridge.cfg");
                if (File.Exists(cfg))
                {
                    foreach (var raw in File.ReadAllLines(cfg))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        if (!line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                        return !IsFalse(line.Substring(eq + 1).Trim());
                    }
                }
            }
            catch { }

            return fallback;
        }

        private static bool IsFalse(string value)
            => value == "0" ||
               value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("no", StringComparison.OrdinalIgnoreCase);

        // ── Public entry point used by Bridge ───────────────────────────

        /// <summary>
        /// Wraps a 0Harmony_UMM transpiler in a HarmonyX-compatible method.
        /// Returns false (with a reason) when the transpiler cannot be bridged,
        /// in which case the caller should fall back to the native runtime.
        /// </summary>
        internal static bool TryWrap(HarmonyMethod source, out HarmonyMethod wrapped, out string error)
        {
            wrapped = null;
            error = null;

            if (!Enabled) { error = "bridge disabled"; return false; }
            if (source == null) { error = "no transpiler"; return false; }

            var transpiler = source.method;
            if (transpiler == null) { error = "transpiler has no method"; return false; }
            if (!transpiler.IsStatic) { error = $"{transpiler.Name} is not static"; return false; }
            if (transpiler.ContainsGenericParameters) { error = $"{transpiler.Name} is generic"; return false; }

            try
            {
                var wrapper = GetOrCreateWrapper(transpiler, out error);
                if (wrapper == null) return false;

                wrapped = new HarmonyMethod(wrapper);
                CopyFields(source, wrapped);
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static void CopyFields(HarmonyMethod source, HarmonyMethod target)
        {
            var t = typeof(HarmonyMethod);
            Copy("priority");
            Copy("before");
            Copy("after");
            Copy("debug");

            void Copy(string name)
            {
                var field = t.GetField(name);
                if (field != null) field.SetValue(target, field.GetValue(source));
            }
        }

        // ── Wrapper generation ──────────────────────────────────────────

        private static MethodInfo GetOrCreateWrapper(MethodInfo transpiler, out string error)
        {
            error = null;
            lock (Gate)
            {
                if (WrapperCache.TryGetValue(transpiler, out var cached))
                    return cached;

                // The 0Harmony_UMM CodeInstruction type is found through the
                // transpiler's own signature: the mod assembly only references
                // it, it does not define it.
                var ummInstruction = FindInstructionType(transpiler);
                if (ummInstruction == null)
                {
                    error = "cannot locate the transpiler's CodeInstruction type";
                    return null;
                }

                var args = BindArguments(transpiler, ummInstruction, out error);
                if (args == null) return null;

                var entry = new Entry
                {
                    Transpiler = transpiler,
                    Args = args,
                    From = ShapeOf(typeof(CodeInstruction)),
                    To = ShapeOf(ummInstruction),
                    Name = $"{transpiler.DeclaringType?.FullName}.{transpiler.Name}"
                };

                var id = Entries.Count;
                Entries.Add(entry);

                MethodInfo wrapper;
                try
                {
                    wrapper = BuildWrapper(id);
                }
                catch (Exception ex)
                {
                    Entries.RemoveAt(id);
                    error = $"could not generate wrapper: {ex.GetType().Name}: {ex.Message}";
                    return null;
                }

                entry.Wrapper = wrapper;
                WrapperCache[transpiler] = wrapper;
                Log($"Transpiler bridge: wrapped {entry.Name} (wrapper #{id})");
                return wrapper;
            }
        }

        private static Type FindInstructionType(MethodInfo transpiler)
        {
            static Type From(Type type)
            {
                if (!type.IsGenericType) return null;

                var definition = type.GetGenericTypeDefinition();
                if (definition != typeof(IEnumerable<>) && definition != typeof(IEnumerator<>))
                    return null;

                var argument = type.GetGenericArguments()[0];
                return argument.Name == "CodeInstruction" && argument.Namespace == "HarmonyLib"
                    ? argument
                    : null;
            }

            foreach (var parameter in transpiler.GetParameters())
                if (From(parameter.ParameterType) is { } fromParameter)
                    return fromParameter;

            return From(transpiler.ReturnType);
        }

        private static ArgKind[] BindArguments(MethodInfo transpiler, Type ummInstruction, out string error)
        {
            error = null;
            var parameters = transpiler.GetParameters();
            var kinds = new ArgKind[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var pt = parameters[i].ParameterType;

                if (pt == typeof(ILGenerator))
                {
                    kinds[i] = ArgKind.Generator;
                }
                else if (typeof(MethodBase).IsAssignableFrom(pt))
                {
                    kinds[i] = ArgKind.Original;
                }
                else if (pt == typeof(IEnumerable) ||
                         (pt.IsGenericType &&
                          pt.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                          pt.GetGenericArguments()[0] == ummInstruction))
                {
                    kinds[i] = ArgKind.Instructions;
                }
                else
                {
                    error = $"unsupported transpiler parameter '{pt.Name} {parameters[i].Name}'";
                    return null;
                }
            }

            return kinds;
        }

        private static MethodInfo BuildWrapper(int id)
        {
            if (_module == null)
            {
                var assembly = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName("UMMBridge.Generated"), AssemblyBuilderAccess.Run);
                _module = assembly.DefineDynamicModule("UMMBridge.Generated");
            }

            var type = _module.DefineType(
                "BridgeTranspiler" + _typeCounter++,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

            var method = type.DefineMethod(
                "Invoke",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(IEnumerable<CodeInstruction>),
                new[] { typeof(IEnumerable<CodeInstruction>), typeof(ILGenerator), typeof(MethodBase) });

            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, id);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, typeof(TranspilerBridgeRuntime).GetMethod(
                nameof(TranspilerBridgeRuntime.Run), BindingFlags.Public | BindingFlags.Static));
            il.Emit(OpCodes.Ret);

            var created = type.CreateType();
            return created.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static);
        }

        /// <summary>
        /// Called from every generated wrapper.  Converts HarmonyX input to
        /// 0Harmony_UMM instructions, runs the mod's transpiler, and converts
        /// the result back.
        /// </summary>
        internal static IEnumerable<CodeInstruction> Run(
            int id, IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            Entry entry;
            lock (Gate) { entry = Entries[id]; }

            // Materialise the input once: the guards below may have to replay
            // the original stream, and the caller's sequence can be single-pass.
            var source = AsList(instructions);

            var args = new object[entry.Args.Length];
            IList ummInstructions = null;

            for (var i = 0; i < entry.Args.Length; i++)
            {
                switch (entry.Args[i])
                {
                    case ArgKind.Instructions:
                        ummInstructions ??= ConvertAll(source, entry.From, entry.To);
                        args[i] = ummInstructions;
                        break;
                    case ArgKind.Generator:
                        args[i] = generator;
                        break;
                    case ArgKind.Original:
                        args[i] = original;
                        break;
                }
            }

            object result;
            try
            {
                result = entry.Transpiler.Invoke(null, args);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                throw new InvalidOperationException(
                    $"UMM transpiler '{entry.Name}' failed: {inner.GetType().Name}: {inner.Message}", inner);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"UMM transpiler '{entry.Name}' could not be invoked: {ex.GetType().Name}: {ex.Message}", ex);
            }

            // A transpiler returning null does not modify the method — that is
            // valid Harmony behaviour and means "keep the original stream".
            // Emitting an empty sequence here would produce an empty method
            // body and silently delete the patched code, so replay instead.
            if (result == null)
            {
                if (source == null) yield break;
                foreach (var instruction in source) yield return instruction;
                yield break;
            }

            // Anything that is not enumerable is a bug in the mod, not a
            // "no change" signal: fail loudly instead of dropping the patch.
            if (result is not IEnumerable output)
            {
                throw new InvalidOperationException(
                    $"UMM transpiler '{entry.Name}' returned '{result.GetType().FullName}', " +
                    $"which is not an instruction sequence; expected an IEnumerable of " +
                    $"'{entry.To.Type.FullName}'.");
            }

            foreach (var item in output)
            {
                if (item == null) continue;
                if (!entry.To.Type.IsInstanceOfType(item))
                {
                    throw new InvalidOperationException(
                        $"UMM transpiler '{entry.Name}' produced an element of type " +
                        $"'{item.GetType().FullName}'; expected '{entry.To.Type.FullName}'.");
                }

                yield return (CodeInstruction)Convert(item, entry.To, entry.From);
            }
        }

        // ── Instruction conversion ──────────────────────────────────────

        /// <summary>
        /// Snapshot of the incoming stream.  <see cref="Run"/> has to be able to
        /// replay the original instructions after the transpiler has already
        /// consumed them, so a single-pass enumerable is not good enough.
        /// </summary>
        private static List<CodeInstruction> AsList(IEnumerable<CodeInstruction> source)
        {
            if (source == null) return null;
            if (source is List<CodeInstruction> list) return list;
            return source.Where(instruction => instruction != null).ToList();
        }

        private static IList ConvertAll(IEnumerable<CodeInstruction> source, InstructionShape from, InstructionShape to)
        {
            var list = to.NewList();
            if (source == null) return list;
            foreach (var instruction in source)
            {
                if (instruction == null) continue;
                list.Add(Convert(instruction, from, to));
            }
            return list;
        }

        private static object Convert(object source, InstructionShape from, InstructionShape to)
        {
            if (source == null) return null;

            var opcode = (OpCode)from.GetOpcode(source);
            var operand = from.GetOperand(source);
            var target = to.New(opcode, operand);

            CopyLabels(source, target, from, to);
            CopyBlocks(source, target, from, to);

            return target;
        }

        private static void CopyLabels(object source, object target, InstructionShape from, InstructionShape to)
        {
            if (from.GetLabels == null || to.GetLabels == null) return;
            if (from.GetLabels(source) is not IEnumerable labels) return;

            var list = to.GetLabels(target) as IList;
            if (list == null)
            {
                list = to.NewLabels();
                to.SetLabels(target, list);
            }

            foreach (var label in labels) list.Add(label);
        }

        private static void CopyBlocks(object source, object target, InstructionShape from, InstructionShape to)
        {
            if (from.GetBlocks == null || to.GetBlocks == null) return;
            if (from.GetBlocks(source) is not IEnumerable blocks) return;

            var fromShape = BlockShapeOf(from);
            var toShape = BlockShapeOf(to);
            if (fromShape == null || toShape == null) return;

            var list = to.GetBlocks(target) as IList;
            if (list == null)
            {
                list = to.NewBlocks();
                to.SetBlocks(target, list);
            }

            foreach (var block in blocks)
            {
                var converted = ConvertBlock(block, fromShape, toShape);
                if (converted != null) list.Add(converted);
            }
        }

        private static object ConvertBlock(object source, BlockShape from, BlockShape to)
        {
            if (source == null) return null;

            // Both runtimes build exception blocks through
            // ExceptionBlock(ExceptionBlockType, Type).  The catch type is a
            // plain System.Type instance, so the reference transfers as-is;
            // only the enum has to be re-parsed into the other assembly's type.
            var kind = ConvertBlockKind(source, from, to);
            if (kind == null)
            {
                Warn($"Transpiler bridge: could not convert exception block " +
                     $"'{DescribeBlockKind(from, source)}' — block dropped");
                return null;
            }

            try
            {
                if (to.New != null)
                    return to.New(kind, from.GetCatchType?.Invoke(source));

                // No ctor taking the enum: construct empty and assign the
                // field directly (both fields are public in both runtimes).
                var created = to.Create();
                to.SetKind(created, kind);
                return created;
            }
            catch (Exception ex)
            {
                Warn($"Transpiler bridge: could not rebuild exception block '{DescribeBlockKind(from, source)}' " +
                     $"({ex.GetType().Name}: {ex.Message}) — block dropped");
                return null;
            }
        }

        private static object ConvertBlockKind(object source, BlockShape from, BlockShape to)
        {
            if (from.GetKind == null || to.KindValues == null) return null;
            var raw = from.GetKind(source);
            if (raw == null) return null;

            return to.KindValues.TryGetValue(raw.ToString(), out var kind) ? kind : null;
        }

        private static string DescribeBlockKind(BlockShape shape, object source)
        {
            try { return shape?.GetKind?.Invoke(source)?.ToString() ?? "?"; }
            catch { return "?"; }
        }

        private static InstructionShape ShapeOf(Type type)
        {
            lock (Gate)
            {
                if (Shapes.TryGetValue(type, out var shape)) return shape;

                // Reflection is only used to discover the shape; every
                // per-instruction operation below is a compiled delegate.
                var ctor = type.GetConstructor(new[] { typeof(OpCode), typeof(object) });
                var opcode = type.GetField("opcode");
                var operand = type.GetField("operand");
                var labels = type.GetField("labels");
                var blocks = type.GetField("blocks");

                if (ctor == null || opcode == null || operand == null)
                    throw new InvalidOperationException($"Unsupported CodeInstruction type '{type.FullName}'");

                shape = new InstructionShape
                {
                    Type = type,
                    Ctor = ctor,
                    Opcode = opcode,
                    Operand = operand,
                    Labels = labels,
                    Blocks = blocks,
                    New = InstructionFactory(ctor),
                    NewList = ListFactory(typeof(List<>).MakeGenericType(type)),
                    GetOpcode = FieldGetter(opcode),
                    GetOperand = FieldGetter(operand),
                    GetLabels = FieldGetter(labels),
                    SetLabels = FieldSetter(labels),
                    NewLabels = ListFactory(labels?.FieldType),
                    GetBlocks = FieldGetter(blocks),
                    SetBlocks = FieldSetter(blocks),
                    NewBlocks = ListFactory(blocks?.FieldType)
                };

                if (shape.New == null || shape.GetOpcode == null || shape.GetOperand == null)
                    throw new InvalidOperationException($"Unsupported CodeInstruction type '{type.FullName}'");

                if (labels != null && (shape.GetLabels == null || shape.SetLabels == null || shape.NewLabels == null))
                    throw new InvalidOperationException($"Unsupported label storage on '{type.FullName}'");

                if (blocks != null && (shape.GetBlocks == null || shape.SetBlocks == null || shape.NewBlocks == null))
                    throw new InvalidOperationException($"Unsupported exception block storage on '{type.FullName}'");

                Shapes[type] = shape;
                return shape;
            }
        }

        private static BlockShape BlockShapeOf(InstructionShape shape)
        {
            if (shape.Blocks == null) return null;
            var element = shape.Blocks.FieldType.IsGenericType
                ? shape.Blocks.FieldType.GetGenericArguments()[0]
                : null;
            if (element == null) return null;

            lock (Gate)
            {
                if (BlockShapes.TryGetValue(element, out var cached)) return cached;

                var kind = element.GetField("blockType");
                var catchType = element.GetField("catchType");

                // ExceptionBlock has a (blockType, catchType) ctor in both
                // 0Harmony 2.3.6 and HarmonyX 2.10.2.  Prefer the ctor that can
                // carry the catch type, then fall back to the enum-only one.
                ConstructorInfo ctor = null;
                foreach (var candidate in element.GetConstructors())
                {
                    var parameters = candidate.GetParameters();
                    if (parameters.Length < 1 || parameters[0].ParameterType != kind?.FieldType)
                        continue;

                    if (ctor == null) ctor = candidate;
                    if (parameters.Length >= 2) { ctor = candidate; break; }
                }

                var block = new BlockShape
                {
                    Type = element,
                    Kind = kind,
                    CatchType = catchType,
                    Ctor = ctor,
                    New = BlockFactory(ctor),
                    Create = ObjectFactory(element),
                    GetKind = FieldGetter(kind),
                    GetCatchType = FieldGetter(catchType),
                    SetKind = FieldSetter(kind),
                    KindValues = EnumValues(kind)
                };

                if (block.GetKind == null)
                    throw new InvalidOperationException($"Unsupported exception block type '{element.FullName}'");

                if (block.New == null && block.Create == null)
                    throw new InvalidOperationException($"Unsupported exception block constructor on '{element.FullName}'");

                BlockShapes[element] = block;
                return block;
            }
        }

        // ── Compiled shape accessors ────────────────────────────────────

        // Reflecting on every instruction is by far the hottest path in the
        // bridge: a mod transpiler that rewrites a 200-instruction method
        // performs thousands of gets/sets.  Each shape therefore compiles its
        // accessors once; afterwards the per-instruction cost is a direct
        // delegate call with no reflection metadata lookups.

        private static Func<OpCode, object, object> InstructionFactory(ConstructorInfo ctor)
        {
            if (ctor == null) return null;

            var parameters = ctor.GetParameters();
            var opcode = Expression.Parameter(typeof(OpCode), "opcode");
            var operand = Expression.Parameter(typeof(object), "operand");
            var body = Expression.New(
                ctor,
                Expression.Convert(opcode, parameters[0].ParameterType),
                Expression.Convert(operand, parameters[1].ParameterType));

            return Expression.Lambda<Func<OpCode, object, object>>(
                Expression.Convert(body, typeof(object)), opcode, operand).Compile();
        }

        private static Func<object, object, object> BlockFactory(ConstructorInfo ctor)
        {
            if (ctor == null) return null;

            var parameters = ctor.GetParameters();
            var kind = Expression.Parameter(typeof(object), "kind");
            var catchType = Expression.Parameter(typeof(object), "catchType");

            Expression body = parameters.Length >= 2
                ? Expression.New(
                    ctor,
                    Expression.Convert(kind, parameters[0].ParameterType),
                    Expression.Convert(catchType, parameters[1].ParameterType))
                : Expression.New(ctor, Expression.Convert(kind, parameters[0].ParameterType));

            return Expression.Lambda<Func<object, object, object>>(
                Expression.Convert(body, typeof(object)), kind, catchType).Compile();
        }

        private static Func<object> ObjectFactory(Type type)
        {
            var ctor = type?.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return null;

            return Expression.Lambda<Func<object>>(
                Expression.Convert(Expression.New(ctor), typeof(object))).Compile();
        }

        private static Func<object, object> FieldGetter(FieldInfo field)
        {
            if (field == null) return null;

            var target = Expression.Parameter(typeof(object), "target");
            var instance = field.IsStatic ? null : Expression.Convert(target, field.DeclaringType);
            var body = Expression.Convert(Expression.Field(instance, field), typeof(object));

            return Expression.Lambda<Func<object, object>>(body, target).Compile();
        }

        private static Action<object, object> FieldSetter(FieldInfo field)
        {
            if (field == null || field.IsInitOnly) return null;

            var target = Expression.Parameter(typeof(object), "target");
            var value = Expression.Parameter(typeof(object), "value");
            var instance = field.IsStatic ? null : Expression.Convert(target, field.DeclaringType);
            var body = Expression.Assign(
                Expression.Field(instance, field),
                Expression.Convert(value, field.FieldType));

            return Expression.Lambda<Action<object, object>>(body, target, value).Compile();
        }

        private static Func<IList> ListFactory(Type fieldType)
        {
            var element = fieldType != null && fieldType.IsGenericType
                ? fieldType.GetGenericArguments()[0]
                : null;
            if (element == null) return null;

            var listType = typeof(List<>).MakeGenericType(element);
            var ctor = listType.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return null;

            return Expression.Lambda<Func<IList>>(
                Expression.Convert(Expression.New(ctor), typeof(IList))).Compile();
        }

        private static Dictionary<string, object> EnumValues(FieldInfo kind)
        {
            if (kind == null) return null;

            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var value in Enum.GetValues(kind.FieldType))
                map[value.ToString()] = value;

            return map;
        }

        // ── Shapes ──────────────────────────────────────────────────────

        private enum ArgKind { Instructions, Generator, Original }

        private sealed class Entry
        {
            public MethodInfo Transpiler;
            public ArgKind[] Args;
            public InstructionShape From;   // HarmonyX
            public InstructionShape To;     // 0Harmony_UMM
            public MethodInfo Wrapper;
            public string Name;
        }

        private sealed class InstructionShape
        {
            public Type Type;
            public ConstructorInfo Ctor;
            public FieldInfo Opcode;
            public FieldInfo Operand;
            public FieldInfo Labels;
            public FieldInfo Blocks;

            public Func<OpCode, object, object> New;
            public Func<IList> NewList;
            public Func<object, object> GetOpcode;
            public Func<object, object> GetOperand;
            public Func<object, object> GetLabels;
            public Action<object, object> SetLabels;
            public Func<IList> NewLabels;
            public Func<object, object> GetBlocks;
            public Action<object, object> SetBlocks;
            public Func<IList> NewBlocks;
        }

        private sealed class BlockShape
        {
            public Type Type;
            public ConstructorInfo Ctor;
            public FieldInfo Kind;
            public FieldInfo CatchType;

            public Func<object, object, object> New;
            public Func<object> Create;
            public Func<object, object> GetKind;
            public Func<object, object> GetCatchType;
            public Action<object, object> SetKind;
            public Dictionary<string, object> KindValues;
        }
    }

    /// <summary>
    /// Public entry point for the generated wrapper methods.  The generated
    /// assembly lives outside UMMBridge, so it cannot call the internal bridge
    /// directly; this forwarder keeps the bridge itself internal.
    /// </summary>
    public static class TranspilerBridgeRuntime
    {
        public static IEnumerable<HarmonyLib.CodeInstruction> Run(
            int id,
            IEnumerable<HarmonyLib.CodeInstruction> instructions,
            System.Reflection.Emit.ILGenerator generator,
            System.Reflection.MethodBase original)
            => TranspilerBridge.Run(id, instructions, generator, original);
    }
}
