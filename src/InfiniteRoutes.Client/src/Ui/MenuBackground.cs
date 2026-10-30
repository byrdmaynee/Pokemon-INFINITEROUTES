using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Placeholder animated backdrop (no ROM assets yet): a dusk gradient with slow, looping "routes".
/// </summary>
public partial class MenuBackground : Control
{
    static readonly Color Top = new(0.07f, 0.10f, 0.16f);
    static readonly Color Bottom = new(0.13f, 0.20f, 0.30f);
    const int Bands = 48;
    const int RouteCount = 5;

    float _time;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta)
    {
        _time += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        var bandHeight = size.Y / Bands;
        for (var i = 0; i < Bands; i++)
            DrawRect(new Rect2(0, i * bandHeight, size.X, bandHeight + 1), Top.Lerp(Bottom, i / (float)(Bands - 1)));

        for (var r = 0; r < RouteCount; r++)
        {
            var baseY = size.Y * (0.35f + r * 0.13f);
            var amplitude = 28f + r * 10f;
            var speed = 0.12f + r * 0.04f;
            var color = new Color(0.95f, 0.76f, 0.31f, 0.10f + r * 0.035f);
            var points = new Vector2[65];
            for (var i = 0; i < points.Length; i++)
            {
                var x = size.X * i / (points.Length - 1);
                var phase = x / size.X * Mathf.Tau * (1.2f + r * 0.3f) + _time * speed * Mathf.Tau;
                points[i] = new Vector2(x, baseY + Mathf.Sin(phase) * amplitude + Mathf.Sin(phase * 0.5f + r) * amplitude * 0.4f);
            }
            DrawPolyline(points, color, 3f + r, antialiased: true);
        }

        // Travelling "players" along the front route.
        for (var d = 0; d < 6; d++)
        {
            var t = Mathf.PosMod(_time * 0.04f + d / 6f, 1f);
            var x = size.X * t;
            var phase = t * Mathf.Tau * 2.4f + _time * 0.28f * Mathf.Tau;
            var y = size.Y * 0.87f + Mathf.Sin(phase) * 68f + Mathf.Sin(phase * 0.5f + 4) * 27f;
            DrawCircle(new Vector2(x, y), 5f, new Color(1, 1, 1, 0.55f));
        }
    }
}
