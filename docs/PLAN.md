# StannumFab: what it is, and how it gets built

Written at the start, so the decisions that are expensive to reverse are visible before they are
made. Revised as they are tested — the sizing at the bottom is an estimate and estimates on this kind
of work have a track record.

---

## The claim

**Basic 3D, on six platforms, from one codebase — and the shared half is as large as it can be.**

Not the fastest renderer. Not a game engine. The thing that is genuinely hard and genuinely reusable
is getting *the same model, looking the same, on a desktop, a phone and a web page*, and that is what
this optimises for. Where unified behaviour and peak performance disagree, unified wins; where the
gap becomes large enough to matter, the backend seam is where it gets fixed, not the API.

**Target uses, in the order they are worth doing:** 3D-print preview (STL, 3MF, units, build plate),
simple model viewers, then very basic animation and games. A game framework is explicitly later, and
explicitly a separate decision.

---

## Decision 1: one graphics backend, and it is OpenGL

**OpenGL 3.3 core on desktop, OpenGL ES 3.0 on mobile, WebGL2 in the browser.**

These are one API with two shader headers. WebGL2 *is* GLES 3.0, so a phone and a browser want the
identical shader and only the desktop differs — which reduces the portability tax to a single line
at the top of each shader source.

| target | context | dialect |
|---|---|---|
| Windows | WGL | `#version 330 core` |
| Linux | GLX / EGL | `#version 330 core` |
| macOS | NSOpenGL (4.1 max) | `#version 330 core` |
| Android | EGL, `libGLESv3.so` | `#version 300 es` |
| iOS | EAGL | `#version 300 es` |
| Browser · NativeAOT-LLVM | `emscripten_webgl_*` | `#version 300 es` |
| Browser · Mono | (see below) | `#version 300 es` |

**Why not Vulkan.** Three backends' worth of work (Vulkan + Metal via MoltenVK + WebGPU) to serve a
performance ceiling this engine's use cases never approach. A print preview is a few million static
triangles drawn once per interaction. Vulkan would also put an Apache-2.0 component (MoltenVK) in the
Apple path, which the licence policy would rather avoid.

**Why not WebGPU.** It is the right long-term answer and is not ready across these six targets. The
backend seam exists so this can be revisited without touching the API.

### The two risks, named up front

**iOS deprecated OpenGL ES in iOS 12 and it still works.** Apple could remove it. If they do, the
answer is a Metal backend behind the same seam, and iOS is the least important target by the project's
own statement. This is a known, bounded exposure rather than a surprise.

**Mono WebAssembly cannot currently reach the emscripten WebGL entry points** the way NativeAOT-LLVM
can. So **NativeAOT-LLVM is the primary browser target** — it is also the one CupriFace's own web host
already proves — and the Mono path is *supported by construction*: GL entry points are resolved
through a `Func<string, nint>` handed in from outside, never through a hardcoded P/Invoke in the
renderer. If Mono closes the gap, the Mono host supplies its own resolver and nothing in the engine
changes.

## Decision 2: the engine does not own a window

`StannumFab.Gl` is handed a proc-address function and a size, and draws. It never creates a context,
a window or a swap chain.

That is what lets one renderer serve a desktop window, an Android `SurfaceView`, a browser canvas and
an offscreen framebuffer inside somebody else's UI toolkit — which is the actual requirement here,
since embedding in CupriFace was a founding use case. An engine that owned its window would need a
separate integration for each, which is precisely the duplication this exists to avoid.

Standalone applications need a window from somewhere, so an optional `StannumFab.Windowing` will wrap
Silk.NET (MIT) for samples and simple apps. Optional, so nothing that embeds ever pays for it.

## Decision 3: managed everywhere it is not absurd

`Core` and `Formats` have **zero dependencies** and nothing outside the BCL. `System.IO.Compression`
opens a 3MF, `System.Text.Json` reads a glTF, `System.Numerics` does the maths with SIMD behind it.

The consequence worth stating: the entire asset layer runs anywhere .NET runs, including wasm, with no
per-platform build matrix and no native artefacts to sign, ship or debug. Native code enters this
project only for GL entry points, which are function pointers rather than a library.

**The one deliberate omission is image decoding.** PNG and JPEG belong to whoever already has a codec
— Skia in a CupriFace app, ImageSharp elsewhere — so textures arrive through a caller-supplied
delegate. Bundling a JPEG decoder to open a model would be the wrong trade for the many callers who
already have one, and skipping textures is a fully supported outcome that the load report names.

---

## Milestones

### M0 · The asset layer — **done**

`Mesh`, `Material`, `Scene`, `BoundingBox`, `LoadReport`, angle-aware `MeshWelder`; readers for STL
(binary + ASCII), OBJ, 3MF and glTF 2.0 / GLB; format sniffing; 49 tests. No GPU needed to test any
of it, which is why it went first.

### M1 · The renderer — **mostly done**

`StannumFab.Gl` draws a `Scene`, verified against a real driver (NVIDIA GL 3.3 core locally, Mesa
llvmpipe on CI).

Done: instanced entry-point table from a supplied `Func<string, nint>` · Cook-Torrance
metallic-roughness with one directional light and a flat ambient term · depth, winding,
`doubleSided`, `alphaMode` MASK and sorted BLEND · interleaved vertex upload cached per `Mesh`, so a
plate of twenty instances uploads once · textures cached per `ImageData` · `Camera` with
fit-to-bounds, orbit and zoom · wireframe and normals debug views · `GlOffscreenTarget` for headless
rendering · the enforced state reset from the [state discipline](#state-discipline) note.

Two decisions worth recording, both cases of unified beating fastest:

- **Wireframe is an edge index buffer, not `glPolygonMode`** — which does not exist in OpenGL ES or
  WebGL2, so the obvious implementation would work on a desktop and silently do nothing on four of
  the six targets.
- **`Camera.Frame` fits whichever field of view is narrower.** A perspective projection is specified
  vertically, so a PORTRAIT viewport has a narrower horizontal field and a vertical-only fit clips the
  model at the sides. Found by a test, not by a phone.

Still open: an `IRenderBackend` interface (there is one backend, and inventing the seam before the
second one exists would be guessing at its shape), and image comparison.

**Verification turned out to split in two, and the estimate was wrong about which half was hard.**
Property assertions — the box occludes what is behind it, the model stays inside the frame, an
inverted facet still shades — were quick and are what actually catch regressions. Reference-image
comparison is the part still outstanding, and it is deliberately not rushed: an exact PNG fails on any
driver that rounds differently, which across six targets means failing for reasons that are not
defects. It needs a tolerance calibrated against more than one driver, so it waits until there is more
than one to calibrate against.

### M2 · Viewer essentials — **1 week**

Orbit / pan / zoom camera with a fit-to-bounds default, a build plate and grid, and the debug views
that make a bad model obvious: wireframe, normals, backface highlight. Camera and input state live in
`Core`; nothing here touches a window.

### M3 · Printing — **1–2 weeks**

The use case that motivated the project. Measurement and a scale bar (units are already carried),
cross-section against an arbitrary plane, per-triangle colours from 3MF colour groups, and toolpath
display — `PrimitiveKind.Lines` is already in the model for exactly this, so a G-code preview is a
parser plus a line renderer rather than an architecture change.

### M4 · Textures and environment — **1 week**

Wire the image-decoder seam, sampler state, mipmaps, and a small prefiltered environment so metals
have something to reflect. Without it every metallic material renders near-black, which is correct
and looks broken.

### M5 · Animation — **reassess before starting**

Node animation and skinning. This is the point where the commitment changes shape: months rather than
weeks, and the first thing that makes this feel like an engine rather than a viewer. It should be a
fresh decision made against real demand, not momentum from M4.

### Later, and not scheduled

Vulkan / Metal / WebGPU backends · frustum and occlusion culling · shadows · post-processing · LOD ·
a scene-authoring format · physics · anything resembling a game framework.

---

## <a id="state-discipline"></a>State discipline, learned elsewhere

CupriFace's GL work lost a full debugging session to one thing, and it is worth importing the lesson
rather than rediscovering it: **a bound sampler object overrides every texture parameter on its unit,
including wrap mode.** Skia binds them. A renderer sharing a context therefore sets `GL_REPEAT`, draws
with a tiling UV, gets clamping, and half the model samples one edge texel — with no error, and with
the texture parameters reading back exactly as they were set. It was invisible on one desktop driver
and glaring on a phone.

So: `StannumFab.Gl` will reset a documented list of state before every frame, enforced in one place
rather than written down. The list is sampler objects first, then the enables (blend, scissor,
stencil, cull, dither), depth state, colour mask, active texture unit, pixel store alignment and the
bound program.

**And the wider lesson from that project:** every rendering defect in it passed CI and 800 unit tests
and looked correct on one desktop driver. Two were found only by a person running it on a phone. The
emulator does not substitute — it answers `SwiftShader`, which proves the code path and not the
driver, and the driver is the axis that breaks. Plan for hardware testing; do not plan to avoid it.

---

## <a id="cupriface"></a>CupriFace

CupriFace's `CupriFace.Gl` package already solves the embedding half: it acquires a GL context bound
to a page element on desktop, Android and the browser, sizes the target to the element's device box,
and hands the frame back with no copy. Its `IGlContent` interface is three methods.

Once M1 exists the glue is about thirty lines:

```csharp
sealed class StannumContent(Scene scene) : IGlContent
{
    private Renderer? _renderer;
    public bool Initialise(GlContext gl) { _renderer = Renderer.Create(gl.GetProcAddress, gl.ShaderHeader); return true; }
    public void Render(GlContext gl, in GlFrame frame) => _renderer!.Draw(scene, camera, frame.Width, frame.Height);
    public void Shutdown(GlContext gl) => _renderer?.Dispose();
}
```

**Where that code lives is decided: the CupriFace repository, not this one.** A general-purpose
engine taking a dependency on a UI toolkit inverts the direction, and practically it would tie this
repo's CI to CupriFace's package feed. CupriFace already has the packaging machinery and already
ships optional packages, so the glue belongs there.

**And it waits.** Not until M1 merely renders something, but until StannumFab is on par with what
CupriFace's own `samples/Demo3d` already does — a textured glTF model under a metallic-roughness
shader, composited behind live UI on all three hosts. Wiring it earlier would replace a working demo
with a worse one and call it progress. On the milestones below that is **M1 plus M4**; M2 and M3 are
viewer and printing work that the CupriFace demo does not need.

---

## Licence policy

MIT, and no dependency may put that at risk. The rule and the permitted list are in
[LICENSING.md](LICENSING.md); the short version is permissive-only, with a CI check rather than
good intentions.

One name worth recording because it is the trap: **ImageSharp is not usable here.** It moved to a
split licence that is not free for all commercial use, which is exactly the kind of condition this
policy exists to keep out — and it is the obvious first choice for the image decoding this project
needs. SkiaSharp (MIT over BSD-3 Skia) and StbImageSharp (public domain) are the alternatives, and
neither is a dependency of this repository: they are what a *caller* plugs into the decoder seam.

---

## Sizing, and how much to trust it

| | |
|---|---|
| M0 asset layer | **done** |
| M1 renderer | 1–2 weeks, of which the CI image-comparison gate is half |
| M2 viewer essentials | ~1 week |
| M3 printing features | 1–2 weeks |
| M4 textures and environment | ~1 week |
| M5 animation | months — reassess first |

**The estimate least worth trusting is M1, and not for the reason it looks.** The renderer itself is
a known quantity. What is not is the verification: a headless GL context on CI, driver differences
between Mesa and everything real, and the tolerance on image comparison that is loose enough to pass
and tight enough to catch a regression.

Related work has now been wrong three times in the same direction: the risk named up front was never
the one that bit, and each time the surprise was about **which layer** a fix had to live in rather
than how hard the fix was. Expect that here.
