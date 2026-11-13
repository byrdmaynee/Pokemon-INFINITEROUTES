using System.Diagnostics.CodeAnalysis;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The zones by id: the extracted ones, plus Infinite Routes (<see cref="UnchartedRoutes"/>), which are built from the
/// same seed as on the server the first time one is asked for.
/// </summary>
sealed class ZoneTable
{
    readonly Dictionary<int, ZoneInfo> _zones = new();
    GameAssets? _assets;

    public void Load(GameAssets assets)
    {
        _assets = assets;
        foreach (var z in assets.LoadZones())
            _zones[z.Id] = z;
    }

    /// <summary>The extracted zones (Infinite Routes aren't listed; look them up by id).</summary>
    public IEnumerable<ZoneInfo> Values => _zones.Values;

    public bool TryGetValue(int id, [MaybeNullWhen(false)] out ZoneInfo zone)
    {
        if (_zones.TryGetValue(id, out zone))
            return true;
        zone = _assets?.Route(id)?.Zone;
        return zone is not null;
    }

    public bool ContainsKey(int id) => TryGetValue(id, out _);

    public ZoneInfo? GetValueOrDefault(int id) => TryGetValue(id, out var zone) ? zone : null;
}
