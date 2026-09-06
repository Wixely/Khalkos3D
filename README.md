# StannumFab

A small, managed-first 3D engine for .NET. Load models, render them anywhere.

```csharp
var scene = ModelReader.ReadFile("bracket.stl");

Console.WriteLine($"{scene.TriangleCount:N0} triangles, {scene.Bounds}");
if (scene.Report.Summary is { } warning) Console.WriteLine(warning);
```

**Status: the asset layer works and is tested. There is no renderer yet** — see [the plan](docs/PLAN.md).

---

## What it is

An engine for **basic 3D that has to run everywhere**: print previews, simple viewers, light
animation. Windows, Linux, macOS, Android, iOS and the browser, from one codebase and one shader
source.

It is deliberately not a game engine. There is no ECS, no physics, no editor, no asset pipeline. If
you need those, Godot and Stride are excellent and free. This is for the case where you want a model
on screen inside an application you are already writing, and you do not want to adopt someone's
entire world to get it.

## What works today

| | |
|---|---|
| **`StannumFab.Core`** | `Mesh`, `Material`, `Scene`, `BoundingBox`, and the vertex welder — **zero dependencies** |
| **`StannumFab.Formats`** | STL, OBJ, 3MF and glTF 2.0 / GLB, all producing the same `Scene` — **zero dependencies** |

Both are pure managed C# with nothing outside the BCL, so they compile for every target including
WebAssembly, where a native parser would mean a per-platform build matrix for what is, in the end,
reading bytes.

```csharp
// Every format, one type, and the caller never learns which parser ran.
Scene fromPrinter = StlReader.ReadFile("part.stl");        // welded, sharp edges kept sharp
Scene fromSlicer  = ThreeMfReader.ReadFile("plate.3mf");   // units converted to millimetres
Scene fromBlender = GltfReader.ReadFile("scene.glb");      // PBR materials, node hierarchy
Scene whatever    = ModelReader.ReadFile("mystery.dat");   // sniffed from the bytes
```

### Three things it does that most loaders do not

**Welding that keeps corners sharp.** An STL has no shared vertices — a box arrives as 36 vertices
rather than 8, and a million-triangle print as three million. Welding them naively then averaging
the normals rounds off every corner, which is exactly wrong for mechanical parts. So the merge is
conditional on the angle between the faces: a box stays faceted, a tessellated cylinder goes smooth,
one pass and no per-model tuning.

**Units.** A 3MF says whether it means millimetres or inches; this converts to millimetres so a build
volume or a measurement is written once. An STL cannot say, and that is worth knowing about STL.

**It tells you what it could not do.** Every load returns a `LoadReport` listing features that were
in the file and are not in the result:

```csharp
foreach (var note in scene.Report.Notes) Console.WriteLine(note);
// Unsupported: animations — the file has 2 animation(s); the model is shown in its rest pose
// Info: welding — 2,904,000 to 486,102 vertices (83% smaller), 12,044 kept sharp
```

A viewer that silently drops an animation track and shows a T-posed character has told its user
their file is broken when it is not. Where a feature changes how the *bytes* are encoded — Draco,
meshopt, quantisation — loading **throws** instead, because a half-decoded mesh is not a degraded
result but a wrong one.

## Licensing

MIT, and it intends to stay that way. Dependencies are held to a policy: permissive only, nothing
that would force a licence change on anyone who uses this. See [docs/LICENSING.md](docs/LICENSING.md).

Today the count is zero — `Core` and `Formats` use nothing but the BCL.

## Using it with CupriFace

[CupriFace](https://github.com/Wixely/CupriFace) is an HTML/CSS UI engine with a `CupriFace.Gl`
package that binds a GL viewport to a page element on every host. StannumFab is designed to drop into
that seam once its renderer exists — roughly thirty lines of glue, in the app rather than in either
library. Neither depends on the other; see [the plan](docs/PLAN.md#cupriface).

## Building

```
dotnet build StannumFab.slnx
dotnet test tests/StannumFab.Tests
```

Requires the .NET 10 SDK. Warnings are errors.
