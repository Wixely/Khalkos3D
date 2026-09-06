# Khalkos3D

A small, managed-first 3D engine for .NET. Load models, render them anywhere.

```csharp
var scene = ModelReader.ReadFile("bracket.stl");

Console.WriteLine($"{scene.TriangleCount:N0} triangles, {scene.Bounds}");
if (scene.Report.Summary is { } warning) Console.WriteLine(warning);
```

**Status: the asset layer and the renderer both work and are tested against a real driver.** See
[the plan](docs/PLAN.md) for what is next.

---

## What it is

An engine for **basic 3D that has to run everywhere**: model viewers, inspection tools, light
animation, anything that needs geometry on screen inside an application. Windows, Linux, macOS,
Android, iOS and the browser, from one codebase and one shader source.

It is deliberately not a game engine. There is no ECS, no physics, no editor, no asset pipeline. If
you need those, Godot and Stride are excellent and free. This is for the case where you want a model
on screen inside an application you are already writing, and you do not want to adopt someone's
entire world to get it.

## What works today

| | |
|---|---|
| **`Khalkos3D.Core`** | `Mesh`, `Material`, `Scene`, `BoundingBox`, `Camera`, and the vertex welder |
| **`Khalkos3D.Formats`** | STL, OBJ, 3MF and glTF 2.0 / GLB, all producing the same `Scene` |
| **`Khalkos3D.Gl`** | draws it — desktop GL 3.3, OpenGL ES 3.0 and WebGL2 from one shader source |

**All three have zero dependencies.** Nothing outside the BCL, so the asset layer compiles for every
target including WebAssembly — where a native parser would mean a per-platform build matrix for what
is, in the end, reading bytes — and the renderer creates no window and no context, so it embeds
wherever there is already one.

```csharp
// The renderer is handed a proc-address function and draws into whatever framebuffer is bound.
var renderer = GlRenderer.Create(getProcAddress, out var error);

var view = new OrbitController();
view.Frame(scene, aspect: (float)width / height);   // takes the up axis from the file
renderer?.Draw(scene, view.Camera, width, height);

// Drags, pans and wheel ticks are fractions of the viewport, so a gesture feels the same on a
// phone and on a 4K monitor.
view.Orbit(dx / width, dy / height);
view.Zoom(wheelTicks);
```

Plus the scenery a viewer needs — a ground grid, a bounded work area and an RGB axis marker — and the
debug views that make a bad mesh obvious: wireframe, normals, and **backface highlighting**, which
matters because the shader deliberately flips normals towards the viewer so an inverted facet does not
appear as a black hole in geometry that is otherwise fine. That kindness hides the defect; this is how
you see it.

### It tells you what a mesh actually is

```csharp
var report = MeshAnalysis.Analyse(scene);
Console.WriteLine(report);
// 4,032 triangles, 0.1 x 0.05 x 0.06, 128 boundary edges

if (MeshAnalysis.ExceedsLimits(scene.Bounds, new Vector3(220, 220, 250)) is { } tooBig)
    Console.WriteLine(tooBig);
```

Volume, surface area, and whether the surface is a **closed, consistently oriented manifold** — the
property that volume, inside/outside tests, boolean operations and offsetting all quietly assume.
When it is not, you get which of the three irregularities applies: boundary edges, non-manifold
edges, or inconsistent winding. Plus inversion, detected from the *sign* of the volume, because an
inverted mesh is a perfectly good closed manifold and looks correct from outside.

It is a diagnostic, not a judgement: an open surface is a defect in something meant to enclose a
volume and completely correct in a terrain or a cloth, so it reports what the geometry **is** and
leaves the meaning to you.

Topology is computed on positions rather than indices, which matters more than it sounds: a
render-ready mesh has extra vertices at every hard edge, so matching by index would report every edge
of a sound cube as a boundary.

**Cross-section** cuts through a model so you can see wall thickness and internal structure —
`bounds.SectionAt(direction, 0.5f)` gives a plane a slider can drive without knowing the model's
scale.

### Materials look like materials

Metallic-roughness PBR with textures, per-texture sampler state, normal maps (tangents derived when a
file does not supply them), and an **environment** — because a metal has no diffuse colour at all, so
with nothing to reflect it correctly renders near-black and every user reads that as a broken shader.

The environment is a sky/horizon/ground gradient rather than an image: no HDR decoder, no
precomputation, no asset to ship, no per-platform texture format, and identical on all six targets.

```csharp
// Every format, one type, and the caller never learns which parser ran.
Scene fromPrinter = StlReader.ReadFile("part.stl");        // welded, sharp edges kept sharp
Scene fromSlicer  = ThreeMfReader.ReadFile("plate.3mf");   // units converted to millimetres
Scene fromBlender = GltfReader.ReadFile("scene.glb");      // PBR materials, node hierarchy
Scene whatever    = ModelReader.ReadFile("mystery.dat");   // sniffed from the bytes
```

### Three things it does that most loaders do not

**Welding that keeps corners sharp.** An STL has no shared vertices — a box arrives as 36 vertices
rather than 8, and a million-triangle model as three million. Welding them naively then averaging
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

## The demo

A standalone viewer for Windows, Linux, macOS and Android.

```
dotnet run --project samples/Khalkos3D.DesktopDemo              # the built-in scene
dotnet run --project samples/Khalkos3D.DesktopDemo -- model.glb # or any file it reads
```

Drag to orbit, right-drag or shift-drag to pan, wheel to zoom. **W** wireframe, **N** normals,
**B** highlight back-faces, **R** reset the view. On Android the same gestures apply: one finger
orbits, two pinch and pan.

The default scene is two rows of spheres sweeping roughness — metal above, dielectric below — and a
box, on a grid. It is chosen because it **fails visibly**: a single cube looks right under almost any
broken shader, whereas if the lighting maths is wrong the row stops varying, if the environment is
missing the metals go black, and if normals are inverted everything lights from the wrong side. One
screen tells you whether the renderer works on your machine.

Both solids are generated in code and come out as closed manifolds — `MeshAnalysis` will confirm it.
A sample for an engine that ships a watertightness check should not hand it geometry that fails.

### Where the code lives, which is the point

| | |
|---|---|
| `samples/Khalkos3D.Demo` | The scene, the camera, the lighting, the debug views, the gesture arithmetic. No window, no windowing library, no image codec. |
| `samples/Khalkos3D.DesktopDemo` | A Silk.NET window, a mouse and a keyboard. |
| `samples/Khalkos3D.AndroidDemo` | An activity, a `GLSurfaceView`, and touch. |

The shared project is the large one and the hosts are thin, which is the claim being demonstrated
rather than a tidiness preference. Android contributes about fifteen lines of real platform code:
`NativeLibrary.TryLoad("libGLESv3.so")` and exported-symbol lookup, deliberately **not**
`eglGetProcAddress` — some drivers return a non-null stub for any name asked of it, which makes a
missing entry point look present and then crash on the call.

Silk.NET is a *sample* dependency. Nothing under `src/` references it, which the `dependencies` CI
job asserts on every run — that is what lets the engine be driven from a host that already owns its
own window.

An installable arm64 APK is built on every CI run and attached to it as an artifact. It is signed
with a debug key that changes per build, so uninstall any previous copy before sideloading a new one
or Android will refuse it as a signature mismatch.

## Licensing

MIT, and it intends to stay that way. Dependencies are held to a policy: permissive only, nothing
that would force a licence change on anyone who uses this. See [docs/LICENSING.md](docs/LICENSING.md).

Today the count is zero. `Core`, `Formats` and `Gl` all use nothing but the BCL — the renderer's GL
entry points are function pointers resolved from a delegate the host supplies, not a binding library.

## Using it with CupriFace

[CupriFace](https://github.com/Wixely/CupriFace) is an HTML/CSS UI engine with a `CupriFace.Gl`
package that binds a GL viewport to a page element on every host. Its `IGlContent` interface hands
over a proc-address function and a size, which is exactly what `GlRenderer` wants — so the glue is
about thirty lines.

**Neither library depends on the other, and the glue lives in CupriFace's repository rather than
here** — a general-purpose engine depending on a UI toolkit would invert the direction.

It shipped: CupriFace's Showcase now draws its 3D viewport through this engine on all three of its
hosts, referencing it as a package. Each of the three lanes is evidenced on a different driver —
zero-copy shared-texture on desktop GL, GLES 3.0 through the Android device gate, and WebGL2 in
Chromium — with the engine and the toolkit independently parsing `GL_VERSION` and agreeing on the
dialect every time. See [the plan](docs/PLAN.md#cupriface).

## Building

```
dotnet build Khalkos3D.slnx
dotnet test tests/Khalkos3D.Tests          # no GPU needed; runs anywhere
dotnet test tests/Khalkos3D.RenderTests    # needs a GL context (xvfb-run on a headless Linux box)
```

Requires the .NET 10 SDK. Warnings are errors.

The two test projects are separate on purpose: the first must run on any machine, including one with
no GL at all, and merging them would make the fast portable suite unrunnable wherever the
driver-bound one cannot start.
