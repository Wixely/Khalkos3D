using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace StannumFab.Formats;

/// <summary>
/// Reads glTF 2.0 and its binary container GLB — the interchange format everything modern speaks.
///
/// <para>Where STL and 3MF describe a thing to print, glTF describes a thing to look at: a node
/// hierarchy, PBR materials, textures, cameras, animation and skinning. This reader takes the
/// static half of that, which is the half a viewer needs, and is explicit about the rest.</para>
///
/// <para><b>The distinction this reader is built around is between INCOMPLETE and WRONG.</b> An
/// animation track it cannot play is noted in <see cref="Scene.Report"/> and the model still
/// appears — incomplete, and said so. A required extension that changes how the BYTES are laid out
/// (Draco compression, quantisation, meshopt) is a different thing entirely: decoding the buffers as
/// if they were plain would produce geometry that is not merely plain but garbage. Those throw. A
/// viewer that showed the garbage would have told its user their file is broken when it is not.</para>
/// </summary>
public static class GltfReader
{
    private const uint GlbMagic = 0x46546C67;      // "glTF"
    private const uint ChunkJson = 0x4E4F534A;     // "JSON"
    private const uint ChunkBin = 0x004E4942;      // "BIN"

    /// <summary>Read a <c>.gltf</c> or <c>.glb</c> from a file. External buffers and images are
    /// resolved relative to it unless <paramref name="options"/> says otherwise.</summary>
    public static Scene ReadFile(string path, GltfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        options ??= new GltfOptions();
        options = options with
        {
            ResolveUri = options.ResolveUri ?? (uri =>
            {
                // Contained to the model's own directory on purpose: a URI is untrusted input, and
                // "../../.." in a downloaded file must not read arbitrary paths.
                var full = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(uri)));
                return full.StartsWith(directory, StringComparison.Ordinal) && File.Exists(full)
                    ? File.ReadAllBytes(full) : null;
            }),
        };
        return Read(File.ReadAllBytes(path), options, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Read a <c>.gltf</c> or <c>.glb</c> from bytes. The container is detected.</summary>
    public static Scene Read(ReadOnlySpan<byte> bytes, GltfOptions? options = null, string? name = null)
    {
        options ??= new GltfOptions();
        var report = new LoadReport.Builder();

        byte[]? binaryChunk = null;
        ReadOnlySpan<byte> json;

        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic)
            json = SplitGlb(bytes, out binaryChunk);
        else
            json = bytes;

        JsonDocument document;
        try { document = JsonDocument.Parse(json.ToArray()); }
        catch (JsonException ex) { throw new ModelFormatException("the glTF JSON could not be parsed", ex); }

        using (document)
            return new Context(document.RootElement, binaryChunk, options, report, name).Build();
    }

    /// <summary>Pull the JSON and BIN chunks out of a GLB container.</summary>
    private static ReadOnlySpan<byte> SplitGlb(ReadOnlySpan<byte> bytes, out byte[]? binary)
    {
        binary = null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (version != 2) throw new ModelFormatException($"GLB version {version} is not supported; only 2 is");

        ReadOnlySpan<byte> json = default;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            at += 8;
            if (length < 0 || at + length > bytes.Length)
                throw new ModelFormatException("a GLB chunk runs past the end of the file");

            if (kind == ChunkJson) json = bytes.Slice(at, length);
            else if (kind == ChunkBin) binary = bytes.Slice(at, length).ToArray();
            // Unknown chunk types are skipped, which the specification requires — it is how the
            // format is meant to grow.
            at += length;
        }

        if (json.IsEmpty) throw new ModelFormatException("the GLB has no JSON chunk");
        return json;
    }

    /// <summary>One parse. Holds the resolved buffers so an accessor can be read without threading
    /// six things through every call.</summary>
    private sealed class Context(JsonElement root, byte[]? glbBinary, GltfOptions options,
                                 LoadReport.Builder report, string? name)
    {
        private readonly List<byte[]?> _buffers = [];
        private readonly List<Mesh> _meshes = [];
        private readonly List<Material> _materials = [];
        private readonly List<ImageData> _images = [];
        // glTF meshes hold several primitives; this engine's Mesh holds one. The map records which
        // of our meshes each glTF mesh became, with the material each carries.
        private readonly Dictionary<int, List<(int Mesh, int? Material)>> _meshParts = [];

        internal Scene Build()
        {
            RefuseUnsupportedExtensions();
            LoadBuffers();
            LoadImages();
            LoadMaterials();
            LoadMeshes();
            NoteWhatWasSkipped();

            var roots = LoadNodes();
            if (_meshes.Count == 0) throw new ModelFormatException("the glTF contains no readable geometry");

            return new Scene
            {
                Meshes = _meshes,
                Materials = _materials,
                Images = _images,
                Roots = roots,
                Report = report.Build(),
                // The specification is explicit: glTF is Y-up.
                Up = UpAxis.Y,
            };
        }

        /// <summary>
        /// Throw on anything in <c>extensionsRequired</c> this reader cannot honour.
        ///
        /// <para>The rule stated once, in code: an extension listed as REQUIRED is the file saying
        /// its bytes cannot be understood without it. Draco and meshopt compress the buffers;
        /// quantisation changes their types. Reading those as plain arrays yields geometry that is
        /// not approximate but meaningless, and displaying it would be worse than refusing.</para>
        /// </summary>
        private void RefuseUnsupportedExtensions()
        {
            if (!root.TryGetProperty("extensionsRequired", out var required)) return;
            foreach (var extension in required.EnumerateArray())
            {
                var id = extension.GetString();
                if (id is null) continue;
                throw new ModelFormatException(
                    $"this file requires the '{id}' extension, which this reader does not implement. " +
                    "Required extensions change how the buffers are encoded, so the geometry cannot " +
                    "be read without it.");
            }
        }

        private void LoadBuffers()
        {
            if (!root.TryGetProperty("buffers", out var buffers)) return;
            var index = 0;
            foreach (var buffer in buffers.EnumerateArray())
            {
                byte[]? bytes = null;
                if (!buffer.TryGetProperty("uri", out var uri))
                {
                    // No URI means the GLB's own binary chunk, and only the first buffer may do it.
                    bytes = index == 0 ? glbBinary : null;
                }
                else if (uri.GetString() is { } text)
                {
                    bytes = text.StartsWith("data:", StringComparison.Ordinal)
                        ? DecodeDataUri(text)
                        : options.ResolveUri?.Invoke(text);
                    if (bytes is null)
                        report.Unsupported("external buffer",
                            $"'{Shorten(text)}' could not be resolved; geometry using it is missing");
                }
                _buffers.Add(bytes);
                index++;
            }
        }

        private void LoadImages()
        {
            if (!root.TryGetProperty("images", out var images)) return;
            var undecodable = 0;

            foreach (var image in images.EnumerateArray())
            {
                byte[]? encoded = null;
                if (image.TryGetProperty("uri", out var uri) && uri.GetString() is { } text)
                {
                    encoded = text.StartsWith("data:", StringComparison.Ordinal)
                        ? DecodeDataUri(text) : options.ResolveUri?.Invoke(text);
                }
                else if (image.TryGetProperty("bufferView", out var view))
                {
                    encoded = ReadBufferView(view.GetInt32());
                }

                var decoded = encoded is not null && options.DecodeImage is not null
                    ? options.DecodeImage(encoded)
                    : null;

                if (decoded is null) { undecodable++; _images.Add(new ImageData([], 0, 0)); }
                else _images.Add(decoded);
            }

            if (undecodable > 0)
                report.Unsupported("textures", options.DecodeImage is null
                    ? $"{undecodable} image(s) were not decoded: no image decoder was supplied. " +
                      "Pass GltfOptions.DecodeImage — see the package notes."
                    : $"{undecodable} image(s) could not be decoded or resolved");
        }

        private void LoadMaterials()
        {
            if (!root.TryGetProperty("materials", out var materials)) return;
            foreach (var material in materials.EnumerateArray())
            {
                var baseColor = Vector4.One;
                float metallic = 1f, roughness = 1f;
                int? baseTexture = null;

                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var factor))
                        baseColor = ReadVector4(factor, Vector4.One);
                    if (pbr.TryGetProperty("metallicFactor", out var m)) metallic = m.GetSingle();
                    if (pbr.TryGetProperty("roughnessFactor", out var r)) roughness = r.GetSingle();
                    if (pbr.TryGetProperty("baseColorTexture", out var texture))
                        baseTexture = ResolveTextureImage(texture);
                }

                if (material.TryGetProperty("extensions", out _))
                    report.Unsupported("material extensions",
                        "KHR_materials_* extensions are ignored; the base metallic-roughness values are used");

                var alpha = material.TryGetProperty("alphaMode", out var mode)
                    ? mode.GetString() switch
                    {
                        "MASK" => AlphaMode.Mask,
                        "BLEND" => AlphaMode.Blend,
                        _ => AlphaMode.Opaque,
                    }
                    : AlphaMode.Opaque;

                _materials.Add(new Material
                {
                    Name = material.TryGetProperty("name", out var n) ? n.GetString() : null,
                    BaseColor = baseColor,
                    Metallic = metallic,
                    Roughness = roughness,
                    Emissive = material.TryGetProperty("emissiveFactor", out var e)
                        ? new Vector3(ReadVector4(e, Vector4.Zero).X, ReadVector4(e, Vector4.Zero).Y, ReadVector4(e, Vector4.Zero).Z)
                        : Vector3.Zero,
                    BaseColorTexture = baseTexture,
                    NormalTexture = material.TryGetProperty("normalTexture", out var nt)
                        ? ResolveTextureImage(nt) : null,
                    Alpha = alpha,
                    AlphaCutoff = material.TryGetProperty("alphaCutoff", out var cutoff)
                        ? cutoff.GetSingle() : 0.5f,
                    // glTF's own default, unlike the engine default: this file HAS an opinion, so it
                    // wins. Only formats with nothing to say get the forgiving double-sided default.
                    DoubleSided = material.TryGetProperty("doubleSided", out var ds) && ds.GetBoolean(),
                });
            }
        }

        private int? ResolveTextureImage(JsonElement textureRef)
        {
            if (!textureRef.TryGetProperty("index", out var indexElement)) return null;
            if (!root.TryGetProperty("textures", out var textures)) return null;

            var index = indexElement.GetInt32();
            var list = textures.EnumerateArray().ToArray();
            if ((uint)index >= (uint)list.Length) return null;
            if (!list[index].TryGetProperty("source", out var source)) return null;

            var image = source.GetInt32();
            return (uint)image < (uint)_images.Count ? image : null;
        }

        private void LoadMeshes()
        {
            if (!root.TryGetProperty("meshes", out var meshes)) return;
            var meshIndex = 0;
            var skipped = 0;

            foreach (var mesh in meshes.EnumerateArray())
            {
                var meshName = mesh.TryGetProperty("name", out var n) ? n.GetString() : null;
                var parts = new List<(int, int?)>();

                if (mesh.TryGetProperty("primitives", out var primitives))
                {
                    foreach (var primitive in primitives.EnumerateArray())
                    {
                        // mode 4 is TRIANGLES. Strips, fans and lines are skipped rather than
                        // reinterpreted: drawing a strip's indices as a list produces a shredded
                        // model that looks like corrupt geometry, not like an unsupported mode.
                        var mode = primitive.TryGetProperty("mode", out var m) ? m.GetInt32() : 4;
                        if (mode != 4) { skipped++; continue; }

                        var built = ReadPrimitive(primitive, meshName);
                        if (built is null) { skipped++; continue; }

                        _meshes.Add(built);
                        var material = primitive.TryGetProperty("material", out var mat) ? mat.GetInt32() : (int?)null;
                        parts.Add((_meshes.Count - 1, material));
                    }
                }

                _meshParts[meshIndex++] = parts;
            }

            if (skipped > 0)
                report.Unsupported("primitive mode",
                    $"{skipped} primitive(s) were not triangle lists and are not shown");
        }

        private Mesh? ReadPrimitive(JsonElement primitive, string? meshName)
        {
            if (!primitive.TryGetProperty("attributes", out var attributes)) return null;
            if (!attributes.TryGetProperty("POSITION", out var positionRef)) return null;

            var positions = ReadVector3Accessor(positionRef.GetInt32());
            if (positions is null || positions.Length == 0) return null;

            var normals = attributes.TryGetProperty("NORMAL", out var normalRef)
                ? ReadVector3Accessor(normalRef.GetInt32()) : null;
            var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uvRef)
                ? ReadVector2Accessor(uvRef.GetInt32()) : null;

            if (attributes.TryGetProperty("TEXCOORD_1", out _))
                report.Unsupported("TEXCOORD_1", "only the first texture coordinate set is read");
            if (attributes.TryGetProperty("JOINTS_0", out _))
                report.Unsupported("skinning", "the model has skin weights; it is shown in its rest pose");

            int[] indices;
            if (primitive.TryGetProperty("indices", out var indexRef))
            {
                indices = ReadIndexAccessor(indexRef.GetInt32()) ?? [];
            }
            else
            {
                indices = new int[positions.Length];
                for (var i = 0; i < indices.Length; i++) indices[i] = i;
            }
            if (indices.Length < 3) return null;

            var built = new Mesh
            {
                Positions = positions,
                Indices = indices,
                Normals = normals?.Length == positions.Length ? normals : null,
                Uvs = uvs?.Length == positions.Length ? uvs : null,
                Name = meshName,
            };

            if (built.Validate() is { } bad)
            {
                report.Repaired("primitive", $"a primitive was dropped: {bad}");
                return null;
            }

            if (built.Normals is not null) return built;

            // The specification says to compute flat normals when none are supplied. Smooth is used
            // instead because it is right far more often — a file without normals is nearly always
            // an exporter that skipped them, not an author asking for facets.
            report.Info("normals", "a primitive supplied none; smooth normals were computed");
            return new Mesh
            {
                Positions = built.Positions,
                Indices = built.Indices,
                Normals = built.ComputeSmoothNormals(),
                Uvs = built.Uvs,
                Name = meshName,
            };
        }

        private List<Node> LoadNodes()
        {
            if (!root.TryGetProperty("nodes", out var nodesElement)) return FallbackRoots();
            var nodes = nodesElement.EnumerateArray().ToArray();

            var sceneIndex = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
            int[] rootIndices;
            if (root.TryGetProperty("scenes", out var scenes))
            {
                var list = scenes.EnumerateArray().ToArray();
                var chosen = (uint)sceneIndex < (uint)list.Length ? list[sceneIndex] : default;
                rootIndices = chosen.ValueKind == JsonValueKind.Object && chosen.TryGetProperty("nodes", out var ids)
                    ? ids.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                    : Enumerable.Range(0, nodes.Length).ToArray();
                if (list.Length > 1)
                    report.Info("scenes", $"the file has {list.Length} scenes; scene {sceneIndex} is shown");
            }
            else rootIndices = Enumerable.Range(0, nodes.Length).ToArray();

            var built = new List<Node>(rootIndices.Length);
            foreach (var index in rootIndices)
                if (BuildNode(index, nodes, 0) is { } node) built.Add(node);
            return built.Count > 0 ? built : FallbackRoots();
        }

        private Node? BuildNode(int index, JsonElement[] nodes, int depth)
        {
            // A cyclic node graph is malformed but not impossible in a hand-edited file, and it
            // would otherwise recurse until the stack goes.
            if (depth > 128 || (uint)index >= (uint)nodes.Length) return null;
            var element = nodes[index];

            var transform = element.TryGetProperty("matrix", out var matrix)
                ? ReadMatrix(matrix)
                : ComposeTrs(element);

            var children = new List<Node>();
            if (element.TryGetProperty("children", out var childIds))
                foreach (var child in childIds.EnumerateArray())
                    if (BuildNode(child.GetInt32(), nodes, depth + 1) is { } node) children.Add(node);

            var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;

            if (!element.TryGetProperty("mesh", out var meshRef))
                return new Node { Name = name, Transform = transform, Children = children };

            // A glTF mesh with several primitives becomes several of ours, so it becomes a group
            // node holding one child per part. That keeps one material per drawable, which is what
            // a renderer wants, without inventing a multi-material mesh type.
            var parts = _meshParts.TryGetValue(meshRef.GetInt32(), out var found) ? found : [];
            if (parts.Count == 1 && children.Count == 0)
                return new Node { Name = name, Transform = transform, Mesh = parts[0].Mesh, Material = parts[0].Material };

            foreach (var (mesh, material) in parts)
                children.Add(new Node { Name = name, Mesh = mesh, Material = material });
            return new Node { Name = name, Transform = transform, Children = children };
        }

        /// <summary>Every mesh at the origin, for a file with no node graph at all.</summary>
        private List<Node> FallbackRoots()
        {
            var roots = new List<Node>(_meshes.Count);
            for (var i = 0; i < _meshes.Count; i++) roots.Add(new Node { Name = name, Mesh = i });
            return roots;
        }

        private void NoteWhatWasSkipped()
        {
            if (root.TryGetProperty("animations", out var animations))
            {
                var count = animations.GetArrayLength();
                if (count > 0)
                    report.Unsupported("animations",
                        $"the file has {count} animation(s); the model is shown in its rest pose");
            }
            if (root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
                report.Unsupported("skins", "skinned meshes are shown undeformed");
            if (root.TryGetProperty("cameras", out var cameras) && cameras.GetArrayLength() > 0)
                report.Unsupported("cameras", "authored cameras are ignored; the viewer frames the model itself");
            if (root.TryGetProperty("extensionsUsed", out var used))
                foreach (var extension in used.EnumerateArray())
                    if (extension.GetString() is { } id)
                        report.Unsupported("extensionsUsed", $"'{id}' and any other optional extensions are ignored");
        }

        // ---- accessors ----------------------------------------------------------------------

        private byte[]? ReadBufferView(int index)
        {
            if (!root.TryGetProperty("bufferViews", out var views)) return null;
            var list = views.EnumerateArray().ToArray();
            if ((uint)index >= (uint)list.Length) return null;

            var view = list[index];
            var buffer = view.TryGetProperty("buffer", out var b) ? b.GetInt32() : 0;
            if ((uint)buffer >= (uint)_buffers.Count || _buffers[buffer] is not { } bytes) return null;

            var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            var length = view.TryGetProperty("byteLength", out var l) ? l.GetInt32() : bytes.Length - offset;
            if (offset < 0 || length < 0 || offset + length > bytes.Length) return null;
            return bytes.AsSpan(offset, length).ToArray();
        }

        /// <summary>Locate an accessor's bytes, honouring the interleaving stride. Returns false when
        /// anything about it does not add up, which is treated as "this attribute is missing" rather
        /// than as a reason to abandon the file.</summary>
        private bool TryAccessor(int index, out ReadOnlySpan<byte> data, out int count,
                                 out int componentType, out int components, out int stride)
        {
            data = default; count = 0; componentType = 0; components = 0; stride = 0;

            if (!root.TryGetProperty("accessors", out var accessors)) return false;
            var list = accessors.EnumerateArray().ToArray();
            if ((uint)index >= (uint)list.Length) return false;
            var accessor = list[index];

            if (accessor.TryGetProperty("sparse", out _))
            {
                report.Unsupported("sparse accessors", "a sparse accessor was ignored; some geometry may be missing");
                return false;
            }

            count = accessor.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
            componentType = accessor.TryGetProperty("componentType", out var ct) ? ct.GetInt32() : 0;
            components = (accessor.TryGetProperty("type", out var t) ? t.GetString() : null) switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 0,
            };
            if (count <= 0 || components == 0) return false;

            var elementSize = ComponentSize(componentType) * components;
            if (elementSize == 0) return false;

            if (!accessor.TryGetProperty("bufferView", out var viewRef)) return false;
            var view = viewRef.GetInt32();
            if (ReadBufferView(view) is not { } bytes) return false;

            // byteStride lives on the bufferView, not the accessor, and defaults to tightly packed.
            // Missing it is how an interleaved buffer gets read as garbage.
            stride = elementSize;
            if (root.TryGetProperty("bufferViews", out var views))
            {
                var viewList = views.EnumerateArray().ToArray();
                if ((uint)view < (uint)viewList.Length &&
                    viewList[view].TryGetProperty("byteStride", out var bs))
                {
                    var declared = bs.GetInt32();
                    if (declared >= elementSize) stride = declared;
                }
            }

            var offset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
            if (offset < 0 || offset > bytes.Length) return false;
            if (offset + (long)(count - 1) * stride + elementSize > bytes.Length) return false;

            data = bytes.AsSpan(offset);
            return true;
        }

        private static int ComponentSize(int componentType) => componentType switch
        {
            5120 or 5121 => 1,     // BYTE, UNSIGNED_BYTE
            5122 or 5123 => 2,     // SHORT, UNSIGNED_SHORT
            5125 or 5126 => 4,     // UNSIGNED_INT, FLOAT
            _ => 0,
        };

        private Vector3[]? ReadVector3Accessor(int index)
        {
            if (!TryAccessor(index, out var data, out var count, out var componentType, out var components, out var stride)
                || components < 3 || componentType != 5126)
                return null;

            var result = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var at = i * stride;
                result[i] = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(data[at..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[(at + 4)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[(at + 8)..]));
            }
            return result;
        }

        private Vector2[]? ReadVector2Accessor(int index)
        {
            if (!TryAccessor(index, out var data, out var count, out var componentType, out var components, out var stride)
                || components < 2)
                return null;

            var result = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var at = i * stride;
                result[i] = componentType switch
                {
                    5126 => new Vector2(BinaryPrimitives.ReadSingleLittleEndian(data[at..]),
                                        BinaryPrimitives.ReadSingleLittleEndian(data[(at + 4)..])),
                    // Normalised integer UVs are common in size-optimised files and are simply a
                    // different encoding of the same numbers.
                    5121 => new Vector2(data[at] / 255f, data[at + 1] / 255f),
                    5123 => new Vector2(BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) / 65535f,
                                        BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]) / 65535f),
                    _ => Vector2.Zero,
                };
            }
            return result;
        }

        private int[]? ReadIndexAccessor(int index)
        {
            if (!TryAccessor(index, out var data, out var count, out var componentType, out var components, out var stride)
                || components != 1)
                return null;

            var result = new int[count];
            for (var i = 0; i < count; i++)
            {
                var at = i * stride;
                result[i] = componentType switch
                {
                    5121 => data[at],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]),
                    5125 => (int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..]),
                    _ => -1,
                };
                if (result[i] < 0) return null;
            }
            return result;
        }

        private static Vector4 ReadVector4(JsonElement array, Vector4 fallback)
        {
            if (array.ValueKind != JsonValueKind.Array) return fallback;
            Span<float> v = [fallback.X, fallback.Y, fallback.Z, fallback.W];
            var i = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (i == 4) break;
                v[i++] = element.GetSingle();
            }
            return new Vector4(v[0], v[1], v[2], v[3]);
        }

        private static Matrix4x4 ReadMatrix(JsonElement array)
        {
            Span<float> v = stackalloc float[16];
            var i = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (i == 16) break;
                v[i++] = element.GetSingle();
            }
            if (i != 16) return Matrix4x4.Identity;
            // glTF stores column-major; Matrix4x4's constructor takes rows. The layouts happen to
            // coincide because System.Numerics is row-vector, which is why this reads straight
            // across rather than transposing.
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
                                 v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }

        private static Matrix4x4 ComposeTrs(JsonElement node)
        {
            var translation = node.TryGetProperty("translation", out var t)
                ? ReadVector4(t, Vector4.Zero) : Vector4.Zero;
            var rotation = node.TryGetProperty("rotation", out var r)
                ? ReadVector4(r, new Vector4(0, 0, 0, 1)) : new Vector4(0, 0, 0, 1);
            var scale = node.TryGetProperty("scale", out var s)
                ? ReadVector4(s, Vector4.One) : Vector4.One;

            // Order is fixed by the specification: scale, then rotate, then translate. Any other
            // order produces a model that is subtly in the wrong place whenever a node does more
            // than one of the three.
            return Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z)
                 * Matrix4x4.CreateFromQuaternion(new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W))
                 * Matrix4x4.CreateTranslation(translation.X, translation.Y, translation.Z);
        }

        private static byte[]? DecodeDataUri(string uri)
        {
            var comma = uri.IndexOf(',');
            if (comma < 0) return null;
            var header = uri.AsSpan(0, comma);
            if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            try { return Convert.FromBase64String(uri[(comma + 1)..]); }
            catch (FormatException) { return null; }
        }

        private static string Shorten(string uri) => uri.Length <= 60 ? uri : uri[..57] + "...";
    }
}

/// <summary>How to read a glTF.</summary>
public sealed record GltfOptions
{
    /// <summary>
    /// Turns encoded image bytes (PNG, JPEG) into pixels, or null to skip textures.
    ///
    /// <para><b>A seam rather than a dependency, and deliberately.</b> Bundling a JPEG decoder into
    /// a model loader would add hundreds of kilobytes for the many callers who already have one: a
    /// CupriFace app has Skia, a desktop app may have ImageSharp, a server may have neither and not
    /// care. Skipping textures is a fully supported outcome — <see cref="Scene.Report"/> says so and
    /// the geometry still loads.</para>
    ///
    /// <para>With SkiaSharp, this is four lines; the repository README has them.</para>
    /// </summary>
    public Func<byte[], ImageData?>? DecodeImage { get; init; }

    /// <summary>Resolves a relative URI for an external buffer or image. Supplied automatically by
    /// <see cref="GltfReader.ReadFile"/>, restricted to the model's own directory. Null means
    /// external resources are skipped and reported, which is the right default for untrusted
    /// input.</summary>
    public Func<string, byte[]?>? ResolveUri { get; init; }
}
