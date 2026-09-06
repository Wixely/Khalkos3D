# Test assets

## `teapot.glb`

A real glTF, copied from [CupriFace](https://github.com/Wixely/CupriFace)'s `samples/Demo3d`, where
its provenance is recorded: **the repo owner's own work** — mesh from the 3ds Max teapot primitive,
base-colour texture authored alongside it. Same author, same MIT licence, no third party to track.

**It is here because synthetic fixtures cannot prove a parser.** Everything else in these suites is
built in code, which is right for pinning specific behaviours and useless for the question "does this
open a file somebody actually exported". This one file carries four things the hand-written fixtures
do not:

| | why it matters |
|---|---|
| **Interleaved accessors at stride 32** | The single most likely glTF parsing bug. A reader that assumes tightly packed data reads garbage — and produces geometry, not an error |
| **`UNSIGNED_INT` indices** | The 32-bit index path, which small fixtures never reach |
| **A two-node scene graph** | Node transforms actually composing, rather than one root at the origin |
| **An 838 KB embedded JPEG** | The image seam end to end: a `bufferView`-backed image, decoded by a caller-supplied codec |

Only the test projects reference it. Nothing in `src/` does, and it is not packed.
