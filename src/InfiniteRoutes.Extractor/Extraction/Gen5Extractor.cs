using System.Text.Json;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Roms;
using InfiniteRoutes.Extractor.Text;

namespace InfiniteRoutes.Extractor.Extraction;

public sealed record ExtractionProgress(string Step, double Fraction);

/// <summary>
/// Extracts what the client needs from a Black/White ROM into the local cache:
/// text tables, species data and battle sprites (M1a), and the overworld maps (M1b). Writes into a temporary folder and
/// swaps it in at the end, so an interrupted run never leaves a half-built cache.
/// </summary>
public static class Gen5Extractor
{
    public static bool Supports(GameDefinition game) => game.Profile == "gen5-bw";

    public static string Extract(string romPath, GameDefinition game, string gameCode, string sha1,
        IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Supports(game))
            throw new NotSupportedException($"Extraction for {game.Name} isn't available yet.");

        var target = ExtractionCache.DirectoryFor(gameCode, sha1);
        var temp = target + ".partial";
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);

        using (var fs = NitroFs.Open(RomSource.OpenSeekable(romPath)))
        {
            progress?.Report(new("Reading text", 0.02));
            var text = Narc.Parse(fs.ReadFile(Gen5Layout.SystemText));
            List<string> Table(int index) => Gen5Text.Decode(text.Get(index).Span);

            var tables = new Dictionary<string, List<string>>
            {
                ["species"] = Table(Gen5Layout.Text.SpeciesNames),
                ["moves"] = Table(Gen5Layout.Text.MoveNames),
                ["items"] = Table(Gen5Layout.Text.ItemNames),
                ["abilities"] = Table(Gen5Layout.Text.AbilityNames),
                ["types"] = Table(Gen5Layout.Text.TypeNames),
                ["locations"] = Table(Gen5Layout.Text.LocationNames),
            };
            var textDir = Directory.CreateDirectory(Path.Combine(temp, "text")).FullName;
            foreach (var (name, lines) in tables)
                WriteJson(Path.Combine(textDir, name + ".json"), lines);

            progress?.Report(new("Reading species data", 0.06));
            var personal = Narc.Parse(fs.ReadFile(Gen5Layout.Personal));
            var species = new List<SpeciesInfo>();
            for (var id = 1; id <= Gen5Layout.NationalDexCount && id < personal.Count; id++)
                species.Add(Gen5Personal.Decode(id, personal.Get(id).Span, tables["species"], tables["types"], tables["abilities"]));
            Directory.CreateDirectory(Path.Combine(temp, "data"));
            WriteJson(Path.Combine(temp, "data", "species.json"), species);

            var sprites = Narc.Parse(fs.ReadFile(Gen5Layout.BattleSprites));
            var spriteDir = Path.Combine(temp, "sprites", "battle");
            foreach (var sub in new[] { "front", "back", "front-shiny", "back-shiny" })
                Directory.CreateDirectory(Path.Combine(spriteDir, sub));

            for (var id = 1; id <= Gen5Layout.NationalDexCount; id++)
            {
                ct.ThrowIfCancellationRequested();
                if (id % 10 == 0)
                    progress?.Report(new($"Battle sprites ({id}/{Gen5Layout.NationalDexCount})", 0.1 + 0.3 * id / Gen5Layout.NationalDexCount));

                var b = id * Gen5Layout.Sprite.FilesPerSpecies;
                var normal = Nclr.Parse(Lz.DecompressIfNeeded(sprites.Read(b + Gen5Layout.Sprite.Palette)));
                var shiny = Nclr.Parse(Lz.DecompressIfNeeded(sprites.Read(b + Gen5Layout.Sprite.ShinyPalette)));
                var front = Ncgr.Parse(Lz.DecompressIfNeeded(sprites.Read(b + Gen5Layout.Sprite.Front)));
                var back = Ncgr.Parse(Lz.DecompressIfNeeded(sprites.Read(b + Gen5Layout.Sprite.Back)));

                var file = $"{id:D3}.png";
                Png.Save(front.RenderGen5Battler(normal), Path.Combine(spriteDir, "front", file));
                Png.Save(back.RenderGen5Battler(normal), Path.Combine(spriteDir, "back", file));
                Png.Save(front.RenderGen5Battler(shiny), Path.Combine(spriteDir, "front-shiny", file));
                Png.Save(back.RenderGen5Battler(shiny), Path.Combine(spriteDir, "back-shiny", file));
            }

            Gen5WorldExtractor.Export(fs, tables["locations"], Directory.CreateDirectory(Path.Combine(temp, "maps")).FullName,
                progress, 0.4, 0.58, ct);
        }

        ExtractionCache.WriteStamp(temp, new ExtractionStamp(ExtractionCache.ExtractorVersion, game.Id, gameCode, sha1, DateTime.UtcNow));
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(temp, target);
        progress?.Report(new("Done", 1));
        return target;
    }

    static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json.Indented));
}
