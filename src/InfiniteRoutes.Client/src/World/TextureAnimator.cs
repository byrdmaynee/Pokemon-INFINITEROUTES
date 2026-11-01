using Godot;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Plays the ROM's map texture animations on a loaded chunk's materials: SRT scrolling (water, rivers, clouds) and
/// frame swaps (flowers, sea foam, river banks). Every chunk samples the same clock, so textures stay continuous
/// across chunk seams.
/// </summary>
public sealed class TextureAnimator
{
    /// <summary>Map animations advance at 30 frames per second, like the DS field.</summary>
    const double FramesPerSecond = 30;

    readonly List<(BaseMaterial3D Material, SrtTrack Track, int Frames)> _targets = new();
    readonly List<(BaseMaterial3D Material, TexturePattern Pattern, Texture2D?[] Frames)> _swaps = new();

    public int Count => _targets.Count + _swaps.Count;

    /// <summary>Materials whose texture is swapped frame by frame; for smoke tests.</summary>
    public int SwapCount => _swaps.Count;

    /// <summary>
    /// Hooks every surface material under <paramref name="root"/> whose name an SRT track targets, or whose texture
    /// (<paramref name="textureOf"/>) a frame-swap pattern targets; <paramref name="frame"/> loads a pattern's frame.
    /// </summary>
    public static TextureAnimator? Attach(Node root, IReadOnlyList<SrtAnimation> animations, IReadOnlyList<TexturePattern> patterns,
        Func<Material, string?> textureOf, Func<string, Texture2D?> frame)
    {
        var tracks = new Dictionary<string, (SrtTrack, int)>();
        foreach (var anim in animations)
        foreach (var track in anim.Tracks)
            tracks.TryAdd(track.Material, (track, Math.Max(1, anim.Frames)));
        var swaps = new Dictionary<string, TexturePattern>();
        foreach (var pattern in patterns)
            swaps.TryAdd(pattern.Texture, pattern);
        if (tracks.Count == 0 && swaps.Count == 0)
            return null;

        var animator = new TextureAnimator();
        var seen = new HashSet<BaseMaterial3D>();
        foreach (var mesh in root.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
        {
            if (mesh.Mesh.SurfaceGetMaterial(s) is not BaseMaterial3D m || !seen.Add(m))
                continue;
            if (tracks.TryGetValue(m.ResourceName, out var t))
                animator._targets.Add((m, t.Item1, t.Item2));
            if (textureOf(m) is { } texture && swaps.TryGetValue(Gen5TexturePatterns.BaseName(texture), out var pattern))
                animator._swaps.Add((m, pattern, pattern.Frames.Select(f => frame(f.Image)).ToArray()));
        }
        return animator.Count > 0 ? animator : null;
    }

    public void Update(double seconds)
    {
        var frame = (long)(seconds * FramesPerSecond);
        foreach (var (material, pattern, frames) in _swaps)
        {
            var tick = (int)(frame % pattern.Length);
            var shown = 0;
            for (var i = 1; i < frames.Length; i++)
                if (pattern.Frames[i].Start <= tick)
                    shown = i;
            if (frames[shown] is { } texture && material.AlbedoTexture != texture)
                material.AlbedoTexture = texture;
        }
        foreach (var (material, track, frames) in _targets)
        {
            var f = (int)(frame % frames);
            // The DS moves the texture by +translate, i.e. samples at uv − translate.
            material.Uv1Offset = new Vector3(-Sample(track.TranslateS, f, 0), -Sample(track.TranslateT, f, 0), 0);
            material.Uv1Scale = new Vector3(Sample(track.ScaleS, f, 1), Sample(track.ScaleT, f, 1), 1);
        }
    }

    static float Sample(float[] values, int frame, float fallback) => values.Length == 0 ? fallback : values[Math.Min(frame, values.Length - 1)];
}
