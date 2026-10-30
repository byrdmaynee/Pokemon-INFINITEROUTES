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

    public const int NationalDexCount = 649;
}
