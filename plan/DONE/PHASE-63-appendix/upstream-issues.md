# Phase 63 — upstream issue drafts (for the owner to file; NOT filed by the implementation wave)

Both were found while implementing [issue #203](https://github.com/kaltinril/ShadowDusk/issues/203)
(the Content Builder library shape). Neither is a substitute for ShadowDusk's own fix — the
`Vortice.Direct3D12` dependency was removed from `ShadowDusk.HLSL` (Area A1) and the regression is
guarded by `DependencyGraphScanTests` — but each is a real defect in its own project and a consumer
of *any* package with one unloadable type will hit the first one. File them as-is or edit freely.

---

## 1. MonoGame — `ContentBuilderHelper.LoadAssemblies` calls `Assembly.GetTypes()` unguarded, so one unloadable type in any dependency kills the whole Content Builder

**Repository:** https://github.com/MonoGame/MonoGame
**Area:** `MonoGame.Framework.Content.Pipeline/Builder/ContentBuilderHelper.cs` (v3.8.5 and `develop` as of 2026-09-09)

### Summary

`ContentBuilderHelper.LoadAssemblies()` — the importer/processor discovery step the new 3.8.5
`ContentBuilder` runs first thing in `Run()` — walks every assembly in the AppDomain plus every
transitively referenced assembly (skipping only `System.*`) and calls `a.GetTypes()` on each with
**no exception handling**. If *any* assembly in the consumer's dependency graph contains a single
type the CLR cannot load, `GetTypes()` throws `ReflectionTypeLoadException` and the entire content
build dies before it has looked at one asset.

MGCB's own scanner (`PipelineManager.ResolveAssemblies`) already guards this correctly:

```csharp
try { ... a.GetTypes() ... }
catch (Exception e) { Logger.LogWarning(null, null, "Failed to load assembly '{0}': {1}", ...); continue; }
```

so the same package works from a `.mgcb` and crashes from a Content Builder project.

### Reproduction

1. `dotnet new mgcb` (the 3.8.5 `MonoGame.ContentBuilder.CSharp` template).
2. Add `<PackageReference Include="Vortice.Direct3D12" Version="3.5.0" />` (3.8.3 behaves the
   same) to the Builder project. Nothing needs to *use* it.
3. `dotnet run -- build -p DesktopGL`.

```
Unhandled exception. System.Reflection.ReflectionTypeLoadException: Unable to load one or more of the requested types.
Could not load type 'Union' from assembly 'Vortice.Direct3D12, Version=3.5.0.0, ...' because it contains an
object field at offset 0 that is incorrectly aligned or overlapped by a non-object field.
   at System.Reflection.RuntimeModule.GetTypes(RuntimeModule module)
   at MonoGame.Framework.Content.Pipeline.Builder.ContentBuilderHelper.LoadAssemblies()
   at MonoGame.Framework.Content.Pipeline.Builder.ContentBuilder.Run(ContentBuilderParams parameters)
```

The unloadable type is `Vortice.Direct3D12.VersionedDeviceRemovedExtendedData+Union` (reported
separately to Vortice, below), but that is incidental: the Builder should not be one bad type in one
transitive dependency away from an unrecoverable crash, and the consumer often cannot remove the
offending package (it arrived transitively through something they need).

### Suggested fix

Mirror `PipelineManager`'s guard:

```csharp
Type[] types;
try
{
    types = a.GetTypes();
}
catch (ReflectionTypeLoadException e)
{
    // Keep the types that did load; the ones that did not cannot be importers/processors anyway.
    types = e.Types.Where(t => t is not null).ToArray()!;
}
catch (Exception)
{
    continue;
}
```

optionally logging a warning naming the assembly, the same way `PipelineManager` does. Also worth
considering: scanning only assemblies that actually reference `MonoGame.Framework.Content.Pipeline`
(an importer/processor cannot exist without that reference), which removes most of the graph from
the scan entirely.

### Environment

`MonoGame.Framework.Content.Pipeline` 3.8.5 (net8.0), .NET 8 and .NET 10 hosts, Windows 11.
Cross-checked against the `v3.8.5` tag and `develop` sources.

---

## 2. Vortice.Windows — `VersionedDeviceRemovedExtendedData.Union` cannot be loaded by the CLR (explicit-layout overlap of managed fields)

**Repository:** https://github.com/amerkoleci/Vortice.Windows
**Package:** `Vortice.Direct3D12` 3.5.0 and 3.8.3 (the newest as of 2026-09-09) — identical behaviour

### Summary

`Vortice.Direct3D12.VersionedDeviceRemovedExtendedData+Union` fails type load:

```
System.TypeLoadException: Could not load type 'Union' from assembly 'Vortice.Direct3D12, Version=3.5.0.0, ...'
because it contains an object field at offset 0 that is incorrectly aligned or overlapped by a non-object field.
```

The nested `Union` is `[StructLayout(LayoutKind.Explicit)]` with `Dred_1_0 : DeviceRemovedExtendedData`
and `Dred_1_1 : DeviceRemovedExtendedData1` both at `[FieldOffset(0)]`, and both DRED structs carry
managed reference fields (the breadcrumb / page-fault data). The CLR rejects
any explicit layout where an object reference overlaps a non-object field, so the type can never
load. The other eight nested `Union` types in the assembly load fine.

It is invisible in ordinary use (nothing touches DRED unless you ask for it), but it surfaces the
moment anything enumerates the assembly's types — `Assembly.GetTypes()`, reflection-based plugin
discovery, some analyzers — and the newest example is MonoGame 3.8.5's Content Builder, which
`GetTypes()`-scans every assembly in a project's dependency graph (unguarded, reported to MonoGame
separately) and therefore crashes for every project that has `Vortice.Direct3D12` anywhere in its
graph, used or not.

### Reproduction

```csharp
// net8.0 console, <PackageReference Include="Vortice.Direct3D12" Version="3.8.3" />
try { typeof(Vortice.Direct3D12.ID3D12Device).Assembly.GetTypes(); }
catch (System.Reflection.ReflectionTypeLoadException e)
{
    foreach (var le in e.LoaderExceptions) Console.WriteLine(le?.Message);
}
```

### Suggested fix

Make the union's managed projection a class or a struct with the two members side by side (the
`__Native` overlay can stay explicit-layout, since it holds only blittable pointers/ints), or drop
the managed `Union` and expose `Dred_1_0`/`Dred_1_1` through accessors that marshal from the
native union on demand. Any shape where no managed reference sits under `LayoutKind.Explicit`
overlap loads.

### Environment

.NET 8.0.x and .NET 10.0.x, Windows 11 x64. The rejection comes from the type loader, not from
D3D12, so it is expected on every OS (not verified off Windows here).
