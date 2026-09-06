namespace Khalkos3D;

/// <summary>What happens to a texture coordinate outside 0..1.</summary>
public enum TextureWrap
{
    /// <summary>Tile. The default, and the one that matters — see <see cref="TextureRef"/>.</summary>
    Repeat,
    /// <summary>Stretch the edge texel outwards.</summary>
    ClampToEdge,
    /// <summary>Tile, flipping alternate copies.</summary>
    MirroredRepeat,
}

/// <summary>How a texture is sampled between texels.</summary>
public enum TextureFilter
{
    /// <summary>Blend. What almost everything wants.</summary>
    Linear,
    /// <summary>Take the nearest texel. For pixel art, indexed data, and anything where a blended
    /// value between two texels would be meaningless rather than smoother.</summary>
    Nearest,
}

/// <summary>
/// An image plus how to sample it — glTF's <c>texture</c>, which is deliberately a pairing rather
/// than a property of either half.
///
/// <para><b>The same image can be sampled two ways in one file</b>, tiling on one material and
/// clamped on another, so the sampler cannot live on the image; and one sampler serves many images,
/// so the image cannot live on the sampler. Modelling it as a pair is what lets both be shared.</para>
///
/// <para><b>Wrap mode is the field that bites.</b> A texture authored to tile, sampled with clamping,
/// does not look like a wrapping bug — it looks like a broken UV unwrap: the model appears to have
/// one stretched edge texel smeared across most of its surface. Nothing errors, and the texture
/// parameters read back exactly as they were set. It is worth knowing that this exact failure, caused
/// by a bound sampler object silently overriding the texture's own settings, cost a full debugging
/// session on a related project and was invisible on one driver and glaring on another.</para>
/// </summary>
/// <param name="Image">Index into <see cref="Scene.Images"/>.</param>
public sealed record TextureRef(int Image)
{
    /// <summary>Horizontal wrap.</summary>
    public TextureWrap WrapS { get; init; } = TextureWrap.Repeat;

    /// <summary>Vertical wrap.</summary>
    public TextureWrap WrapT { get; init; } = TextureWrap.Repeat;

    /// <summary>Filter when the texture is drawn larger than its pixels.</summary>
    public TextureFilter Magnify { get; init; } = TextureFilter.Linear;

    /// <summary>Filter when it is drawn smaller.</summary>
    public TextureFilter Minify { get; init; } = TextureFilter.Linear;

    /// <summary>
    /// Build and sample a mip chain when minifying.
    ///
    /// <para>On by default, and it is not a quality nicety: a texture minified across a curved
    /// surface without mipmaps aliases into a shimmering mess the moment the model turns, which reads
    /// as a rendering fault rather than as a missing filter setting. Off only for data a blend would
    /// corrupt — an index map, a lookup table.</para>
    /// </summary>
    public bool Mipmaps { get; init; } = true;
}
