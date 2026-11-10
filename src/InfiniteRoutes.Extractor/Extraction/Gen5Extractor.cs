using System.Text.Json;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using InfiniteRoutes.Extractor.Roms;
using InfiniteRoutes.Extractor.Text;

namespace InfiniteRoutes.Extractor.Extraction;

public sealed record ExtractionProgress(string Step, double Fraction);

/// <summary>
/// Extracts what the client needs from a Black/White ROM into the local cache:
/// text tables, species data and battle sprites (M1a), and the overworld maps and player sprites (M1b). Writes into a temporary folder and
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
        using var guard = ExtractionCache.Lock(target, out var waited, ct);
        if (waited && ExtractionCache.IsComplete(target))
            return target;
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
                ["trainers"] = Table(Gen5Layout.Text.TrainerNames),
                ["trainer-classes"] = Table(Gen5Layout.Text.TrainerClassNames),
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

            progress?.Report(new("Reading moves and encounters", 0.08));
            var moveData = Narc.Parse(fs.ReadFile(Gen5Layout.Moves));
            var moves = new List<MoveInfo>();
            for (var id = 1; id < moveData.Count; id++)
                moves.Add(Gen5Battle.DecodeMove(id, moveData.Read(id), tables["moves"]));
            WriteJson(Path.Combine(temp, "data", "moves.json"), moves);

            // Learnsets by species id (index 0 is empty); forms beyond the national dex are left out for now.
            var learnData = Narc.Parse(fs.ReadFile(Gen5Layout.Learnsets));
            var learnsets = new List<List<LevelMove>>();
            for (var id = 0; id <= Gen5Layout.NationalDexCount && id < learnData.Count; id++)
                learnsets.Add(id == 0 ? [] : Gen5Battle.DecodeLearnset(learnData.Read(id)));
            WriteJson(Path.Combine(temp, "data", "learnsets.json"), learnsets);

            var encounterData = Narc.Parse(fs.ReadFile(Gen5Layout.Encounters));
            var encounters = new List<EncounterTable>();
            for (var id = 0; id < encounterData.Count; id++)
                encounters.Add(Gen5Battle.DecodeEncounters(id, encounterData.Read(id)));
            WriteJson(Path.Combine(temp, "data", "encounters.json"), encounters);

            var trainerData = Narc.Parse(fs.ReadFile(Gen5Layout.Trainers));
            var trainerParties = Narc.Parse(fs.ReadFile(Gen5Layout.TrainerParties));
            var trainers = new List<TrainerInfo>();
            var trainerLines = Gen5Battle.DecodeTrainerLines(Narc.Parse(fs.ReadFile(Gen5Layout.TrainerTextTable)).Read(0),
                Table(Gen5Layout.Text.TrainerLines));
            for (var id = 0; id < trainerData.Count && id < trainerParties.Count; id++)
                trainers.Add(Gen5Battle.DecodeTrainer(id, trainerData.Read(id), trainerParties.Read(id), tables["trainers"], tables["trainer-classes"])
                    is var t && trainerLines.TryGetValue(id, out var said) ? t with { Intro = said.Intro, Defeat = said.Defeat, After = said.After } : t);
            WriteJson(Path.Combine(temp, "data", "trainers.json"), trainers);

            // Item prices by item id, in ₽ (the ROM stores tenths).
            var itemData = Narc.Parse(fs.ReadFile(Gen5Layout.Items));
            var prices = new List<int>();
            for (var id = 0; id < itemData.Count; id++)
                prices.Add(itemData.Read(id) is { Length: >= 2 } item ? (item[0] | item[1] << 8) * 10 : 0);
            WriteJson(Path.Combine(temp, "data", "item-prices.json"), prices);

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

            // Trainer class sprites, one 80×80 PNG per class.
            var trainerSprites = Narc.Parse(fs.ReadFile(Gen5Layout.TrainerSprites));
            var trainerDir = Directory.CreateDirectory(Path.Combine(temp, "sprites", "trainers")).FullName;
            for (var c = 0; (c + 1) * Gen5Layout.TrainerSpriteFiles <= trainerSprites.Count; c++)
            {
                try
                {
                    var b = c * Gen5Layout.TrainerSpriteFiles;
                    var palette = Nclr.Parse(Lz.DecompressIfNeeded(trainerSprites.Read(b + Gen5Layout.TrainerSpritePalette)));
                    Png.Save(Ncgr.Parse(Lz.DecompressIfNeeded(trainerSprites.Read(b))).RenderGen5Trainer(palette), Path.Combine(trainerDir, $"{c:D3}.png"));
                }
                catch (InvalidDataException)
                {
                    // A class without a sprite of this shape: battles just don't show one.
                }
            }

            progress?.Report(new("Overworld characters", 0.4));
            var characters = Narc.Parse(fs.ReadFile(Gen5Layout.OverworldSprites));
            var characterDir = Directory.CreateDirectory(Path.Combine(temp, "sprites", "overworld")).FullName;
            foreach (var (name, index) in new[] { ("hero", Gen5Layout.Player.Hero), ("heroine", Gen5Layout.Player.Heroine) })
                if (Tex0.FromFile(Lz.DecompressIfNeeded(characters.Read(index))) is { } tex)
                    Png.Save(CharacterSheet.FromGen5Player(tex), Path.Combine(characterDir, name + ".png"));
            foreach (var (name, index) in new[] { ("hero-surf", Gen5Layout.Player.HeroSurf), ("heroine-surf", Gen5Layout.Player.HeroineSurf) })
                if (Tex0.FromFile(Lz.DecompressIfNeeded(characters.Read(index))) is { } tex)
                    Png.Save(CharacterSheet.FromGen5Surf(tex), Path.Combine(characterDir, name + ".png"));

            var world = Gen5WorldExtractor.Export(fs, tables["locations"], Directory.CreateDirectory(Path.Combine(temp, "maps")).FullName,
                progress, 0.4, 0.56, ct);
            progress?.Report(new("Battle scenery", 0.96));
            Gen5WorldExtractor.ExportBattleScenery(fs, Directory.CreateDirectory(Path.Combine(temp, "battle")).FullName);
            progress?.Report(new("NPC sprites", 0.97));
            Gen5WorldExtractor.ExportNpcSprites(fs, world.Zones, Path.Combine(characterDir, "npc"));
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
