using Godot;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Plays the ROM's texture SRT animations (scrolling water, rivers, clouds) on a loaded chunk's materials.
/// Every chunk samples the same clock, so textures stay continuous across chunk seams.
/// </summary>
public sealed class TextureAnimator
{
    /// <summary>Map animations advance at 30 frames per second, like the DS field.</summary>
    const double FramesPerSecond = 30;

    readonly List<(BaseMaterial3D Material, SrtTrack Track, int Frames)> _targets = new();

    public int Count => _targets.Count;

    /// <summary>Hooks every surface material under <paramref name="root"/> whose name an animation track targets.</summary>
    public static TextureAnimator? Attach(Node root, IReadOnlyList<SrtAnimation> animations)
    {
        var tracks = new Dictionary<string, (SrtTrack, int)>();
        foreach (var anim in animations)
        foreach (var track in anim.Tracks)
            tracks.TryAdd(track.Material, (track, Math.Max(1, anim.Frames)));
        if (tracks.Count == 0)
            return null;

        var animator = new TextureAnimator();
        var seen = new HashSet<BaseMaterial3D>();
        foreach (var mesh in root.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            if (mesh.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D m && seen.Add(m) && tracks.TryGetValue(m.ResourceName, out var t))
                animator._targets.Add((m, t.Item1, t.Item2));
        return animator._targets.Count > 0 ? animator : null;
    }

    public void Update(double seconds)
    {
        var frame = (long)(seconds * FramesPerSecond);
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
