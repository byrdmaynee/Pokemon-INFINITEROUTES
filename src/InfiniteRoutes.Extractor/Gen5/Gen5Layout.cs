namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>
/// Where Black/White keep the data the extractor reads. Verified against a USA/EUR White dump;
/// Black uses the same layout (same "gen5-bw" profile).
/// </summary>
public static class Gen5Layout
{
    public const string SystemText = "a/0/0/2";
    public const string StoryText = "a/0/0/3";
    public const string BattleSprites = "a/0/0/4";
    public const string Personal = "a/0/1/6";
    /// <summary>Level-up learnsets, one per personal record (see <see cref="Gen5Battle.DecodeLearnset"/>).</summary>
    public const string Learnsets = "a/0/1/8";
    /// <summary>Move data, 36 bytes per move (see <see cref="Gen5Battle.DecodeMove"/>).</summary>
    public const string Moves = "a/0/2/1";
    /// <summary>Wild encounter tables, indexed by the zone header (see <see cref="Gen5Battle.DecodeEncounters"/>).</summary>
    public const string Encounters = "a/1/2/6";
    /// <summary>Trainer records and their parties, same index (see <see cref="Gen5Battle.DecodeTrainer"/>).</summary>
    public const string Trainers = "a/0/9/2";
    public const string TrainerParties = "a/0/9/3";
    /// <summary>One file: (u16 trainer, u16 message type) per line of <see cref="Text.TrainerLines"/>, in line order.</summary>
    public const string TrainerTextTable = "a/0/9/0";
    /// <summary>Trainer class sprites: 8 files per class (0: 80×80 NCGR, 7: NCLR; the rest animate it).</summary>
    public const string TrainerSprites = "a/0/7/2";
    public const int TrainerSpriteFiles = 8, TrainerSpritePalette = 7;
    /// <summary>Item data, one 36-byte member per item id; u16 @0 is the price in units of ₽10.</summary>
    public const string Items = "a/0/2/4";
    /// <summary>Trainer NPCs run script <c>TrainerScriptBase + trainer id</c>.</summary>
    public const int TrainerScriptBase = 3000;

    public const string MapChunks = "a/0/0/8";
    public const string MapMatrices = "a/0/0/9";
    /// <summary>NARC whose first member holds every zone header, <see cref="ZoneHeaderSize"/> bytes each.</summary>
    public const string ZoneHeaders = "a/0/1/2";
    /// <summary>Per-zone events: furniture, NPCs, warps, triggers (see <see cref="Gen5Events"/>).</summary>
    public const string ZoneEvents = "a/1/2/5";
    /// <summary>Plain table (not a NARC): 10 bytes per area — u16 building pack, u16 texture set, u16 ?, u8 outdoor flag, …</summary>
    public const string AreaTable = "a/0/1/3";
    public const int AreaSize = 10;
    public const int AreaOutdoorFlag = 6;
    /// <summary>
    /// Area byte 7: lighting/season group. Seasonal outdoor areas come as four consecutive entries
    /// (spring, summer, autumn, winter) whose values run n, n+1, n+2, n+3 with n a multiple of 4.
    /// </summary>
    public const int AreaSeasonGroup = 7;
    /// <summary>
    /// Area u16 at 4: low byte = SRT texture animation in <see cref="MapSrtAnimations"/>, high byte = frame-swap
    /// animation in <see cref="MapPatternAnimations"/> (0xFF = none).
    /// </summary>
    public const int AreaAnimations = 4;
    /// <summary>BTA0 texture-scroll animations for map terrain (water, rivers, clouds), indexed by the area table.</summary>
    public const string MapSrtAnimations = "a/0/6/9";
    /// <summary>Frame-swap texture animations for map terrain (flowers, sea foam, river banks); see <see cref="Gen5TexturePatterns"/>.</summary>
    public const string MapPatternAnimations = "a/0/7/0";
    public const int Seasons = 4;
    public const string MapTextures = "a/0/1/4";
    /// <summary>Outdoor building packs ("AB"): N metadata entries followed by N BMD0 models.</summary>
    public const string BuildingPacks = "a/2/2/9";
    /// <summary>BTX0 per building pack (same index): the textures the pack's models use.</summary>
    public const string BuildingTextures = "a/1/7/6";
    /// <summary>Indoor areas' furniture packs and their textures, laid out like the outdoor ones.</summary>
    public const string IndoorBuildingPacks = "a/2/3/0";
    public const string IndoorBuildingTextures = "a/1/7/7";
    /// <summary>
    /// Battle scenery: BMD0 models named batt_stage* (the platforms, files 0–33) and batt_bg* (the backdrops, with BCA0
    /// animations, 34–83; "a"–"d" variants are the four seasons), then 2D battle UI graphics.
    /// </summary>
    public const string BattleStages = "a/0/1/1";
    /// <summary>Overworld character models (BMD0) and textures (BTX0).</summary>
    public const string OverworldSprites = "a/0/4/9";
    /// <summary>Sprite id → <see cref="OverworldSprites"/> member (see <see cref="Gen5OverworldTable"/>).</summary>
    public const string OverworldTable = "a/0/4/8";

    /// <summary>The overworld matrix (all of Unova's outdoor map).</summary>
    public const int OverworldMatrix = 0;
    public const int ZoneHeaderSize = 48;

    /// <summary>Field offsets inside a zone header.</summary>
    public static class Zone
    {
        public const int Area = 0x02;
        public const int Matrix = 0x04;
        /// <summary>u16 index into <see cref="Encounters"/>; 0xFFFF for no wild Pokémon.</summary>
        public const int Encounters = 0x14;
        public const int Events = 0x16;
        public const int Parent = 0x18;
        public const int LocationName = 0x1A;
        /// <summary>u8: the battle scenery set (backdrop and platforms) wild battles here use.</summary>
        public const int BattleScenery = 0x01;
    }

    /// <summary>Message file indices inside <see cref="SystemText"/>.</summary>
    public static class Text
    {
        public const int ItemNames = 54;
        public const int SpeciesNames = 70;
        public const int LocationNames = 89;
        public const int AbilityNames = 182;
        public const int TypeNames = 199;
        public const int MoveDescriptions = 202;
        public const int MoveNames = 203;
        public const int TrainerNames = 190;
        public const int TrainerClassNames = 191;
        /// <summary>What trainers say: before (type 0), on losing (1) and after (2) a battle; see <see cref="TrainerTextTable"/>.</summary>
        public const int TrainerLines = 189;
    }

    /// <summary>Battle sprite NARC: 20 files per species, at species × 20.</summary>
    public static class Sprite
    {
        public const int FilesPerSpecies = 20;
        public const int Front = 0;
        public const int Back = 9;
        public const int Palette = 18;
        public const int ShinyPalette = 19;
    }

    /// <summary>Player characters in <see cref="OverworldSprites"/>: BTX0s of 32 32×32 frames.</summary>
    public static class Player
    {
        public const int Hero = 6;
        public const int Heroine = 9;
        /// <summary>Frames 1–12 walk, 13–24 run; three per direction (stand, left foot, right foot) in the order up, down, left, right.</summary>
        public const int WalkFirst = 1;
        public const int RunFirst = 13;
    }

    public const int NationalDexCount = 649;
}
