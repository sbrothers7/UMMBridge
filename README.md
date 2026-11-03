# UMMBridge

Run [UnityModManager](https://github.com/newman55/unity-mod-manager) (UMM) mods under [MelonLoader](https://github.com/LavaGang/MelonLoader).

## Overview

UMMBridge loads UMM inside MelonLoader, transparently bridging the two mod ecosystems. It intercepts `UnityModManager.modsPath`, redirecting to `UMMMods/`, then boots UMM and its UI alongside MelonLoader's plugin pipeline.

## Dual-Harmony Architecture

UMM ships **0Harmony 2.3.6** (with its own MonoMod) while MelonLoader uses **HarmonyX** (a different MonoMod). Two MonoMod runtimes cannot write JMP detours on the same method entry. UMMBridge resolves this by keeping every UMM **mod** patch on a single runtime — HarmonyX — including transpilers, which are converted at the boundary. The one exception is a method whose entry a UMM core patch already claimed natively: later mod patches (transpilers included) join that native table instead of getting a detour of their own.

### Three-layer resolution

| Layer | Trigger | Behavior |
|-------|---------|----------|
| **Cecil rewriting** | Assembly load (`LoadFrom` interception) | Renames `0Harmony`→`0Harmony_UMM`, rewrites `Patch()` calls to `ProxyPatch` (transpiler assemblies included) |
| **UpdateWrapper injection** | `CreateClassProcessor`/`PatchAll` path | IL injection at `PatchFunctions.UpdateWrapper()`: in bridge mode a mod patch job is routed to HarmonyX unless that method is already claimed natively |
| **Harmony.Patch() prefix** | Direct `Harmony.Patch()` calls | HarmonyX prefix on `0Harmony_UMM.Harmony.Patch()`: converts a mod transpiler to HarmonyX, keeps UMM's own core patches native, and joins the native patch table instead of adding a second detour |

### Patch routing

- **Transpilers (bridge enabled, default)** — `TranspilerBridge` converts `IEnumerable<CodeInstruction>` in both directions: the mod's transpiler still runs against 0Harmony_UMM's own `CodeInstruction` objects, while its patch is registered on **HarmonyX** together with every other patch for that method. Labels and operands are preserved; exception blocks are converted on a best-effort basis. If the method entry is *already* owned natively (a core patch got there first), the patch joins that native table instead — one detour either way.
- **Non-transpiler patches** — Cecil-rewritten via `ProxyPatch`, applied through **HarmonyX** on a free entry, or joined to the existing native table when the entry is already native.
- **Native runtime** — UMM's own core patches, methods already claimed natively by a core patch, or any patch when the bridge is disabled.
- **No migration** — there is no path that moves patches between the two runtimes. HarmonyX's `ManagedMethodPatcher.DetourTo()` re-applies its ILHook even after the method is unpatched, so a migration would leave that detour behind and stack a second JMP on the same prologue. The bridge picks a runtime per method up front instead.
- **Fallback** — If a transpiler cannot be wrapped (unsupported parameter, non-static method, generation failure), the reason is logged; the patch then joins the native table when 0Harmony_UMM already owns the entry, and is skipped with that reason when only HarmonyX owns it (writing a second detour is never an option).

### Configuration

Transpiler bridging is enabled by default. To restore the previous build-time routing, disable it with either:

- `UMMBridge.cfg` next to `UMMBridge.dll`, containing `TranspilerBridge=false`
- the environment variable `UMMBRIDGE_TRANSPILER_BRIDGE=0`

When disabled, assemblies containing transpilers keep their `Patch()` calls native and are handled by 0Harmony_UMM, exactly like earlier versions.

#### HarmonyX-only mode

UMM mods can also run on MelonLoader's HarmonyX alone, like MelonLoader mods: their `0Harmony` reference is left as it is, nothing is routed, and 0Harmony_UMM is not used. The rest of the assembly rewriting (`Mods` → `UMMMods` paths, `Assembly.Location`) still applies. One runtime means every patch stacks and unpatching works.

It turns on by itself when UMM's `0Harmony.dll` is missing, or when 0Harmony_UMM cannot detour on the machine: MonoMod.Core in 0Harmony 2.3.x supports only x86/x64, so this includes Apple Silicon running the arm64 game. To force it, use either:

- `HarmonyXOnly=true` in `UMMBridge.cfg`
- the environment variable `UMMBRIDGE_HARMONYX_ONLY=1`

Mods that reach into Harmony's internals (JALib and the mods built on it) do not work in this mode, and neither do mods that call the few 2.3 APIs HarmonyX lacks (`PatchClassProcessor.Unpatch`, `HarmonyLib.AccessToolsExtensions`, `AccessTools.MethodDelegate(MethodInfo, object, bool, Type[])`).

### Coexistence

The following table summarizes what happens when two patches target the same method:

| Scenario | Patch Type | Result |
|----------|------------|--------|
| UMM mod patch + MelonLoader mod patch (bridge on, free entry) | Any, incl. transpilers | ✅ **Works** — every mod patch lives on HarmonyX, so patches stack. |
| UMM mod patch + UMM mod patch (bridge on, free entry) | Any, incl. transpilers | ✅ **Works** — no second runtime, no load-order dependency. |
| UMM **core** patch first, then UMM mod patches (bridge on) | Any, incl. transpilers | ✅ **Works** — the core patch owns the entry natively, later mod patches join that native table; still one detour. |
| Native core patch + a patch written straight into HarmonyX (bridge bypassed) | Any | ⚠️ **One wins** — two detours on one prologue: whichever was written second tramples the other. |
| Bridge disabled: UMM transpiler + HarmonyX patch | Transpiler | ❌ **Never works** — the native transpiler cannot move to HarmonyX (previous behaviour). |

**Key limitation:**  
Two MonoMod runtimes (HarmonyX and 0Harmony_UMM) still **cannot** both write JMP detours on the same method. The bridge removes that conflict for **mods** by converting transpilers at the boundary and by routing each method to exactly one runtime: patches that arrive through the bridge join whichever runtime already owns the entry. The remaining one-wins case is a patch that reaches HarmonyX *outside* the bridge on a method a core patch already owns natively.

### Limitations

- **UMM core patches stay native** — A method already claimed by a native core patch cannot also receive a HarmonyX detour. Later mod patches join that native table (transpilers included) instead of getting a detour of their own; a patch whose transpiler cannot be wrapped is skipped with a logged reason when only HarmonyX owns the entry.
- **Bridge scope** — `TranspilerBridge` converts `CodeInstruction` streams (opcode, operand, labels, exception blocks). The transpiler still executes against 0Harmony_UMM objects by design; a transpiler whose signature cannot be bound is logged and handled by the same runtime rule: it joins the native table when 0Harmony_UMM already owns the entry, and is skipped with that reason when only HarmonyX owns it.
- **No perfect coexistence** — There is no way to make a HarmonyX‑based patch and a 0Harmony_UMM‑based patch both apply to the same method entry simultaneously. The design prioritizes stability over universal coexistence.

## Usage

1. Drop the compiled DLL into ADOFAI's `Plugins/` directory
2. Place UMM mods into `UMMMods/` (created automatically on first launch)
3. Open the UMM window and click **Mods Dir** at the top-right

## Build

```
dotnet build UMMBridge\UMMBridge.csproj -c Release
```

Or open `UMMBridge.slnx` in Visual Studio 2022. The `Deploy` configuration outputs directly to the game's Plugins directory.

<img width="1282" height="752" alt="image" src="https://github.com/user-attachments/assets/04447190-7fb8-4254-8091-63512e2deda0" />
