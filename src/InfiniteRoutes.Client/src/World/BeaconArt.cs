using Godot;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The role beacons (<see cref="Core.Player.Beacons"/>) as 12×12 pixel art, drawn to match the game's sprites, and the
/// floating emblem made from them that hovers over a player's name.
/// </summary>
public static partial class BeaconArt
{
    static readonly Dictionary<char, Color> Palette = new()
    {
        ['K'] = new Color("1a1a22"), ['Y'] = new Color("f5c542"), ['y'] = new Color("c8901e"), ['W'] = new Color("ffffff"),
        ['R'] = new Color("e23c3c"), ['r'] = new Color("a02828"), ['B'] = new Color("3c78e6"), ['b'] = new Color("2850a0"),
        ['G'] = new Color("4cc04c"), ['g'] = new Color("2e8030"), ['P'] = new Color("9a5ce0"), ['O'] = new Color("f08c28"),
        ['C'] = new Color("78dcf0"), ['S'] = new Color("c8ccd6"), ['s'] = new Color("8a8f9c"), ['N'] = new Color("8a5a2c"),
        ['n'] = new Color("5a3a1c"),
    };

    static readonly Dictionary<string, string[]> Art = new()
    {
        ["crown"] = ["............", ".K...KK...K.", "KYK.KYYK.KYK", "KYYKYYYYKYYK", "KYYYYYYYYYYK", "KYYRYYYYRYYK", "KYYYYYYYYYYK",
            "KyyyyyyyyyyK", ".KKKKKKKKKK.", "............", "............", "............"],
        ["hammer"] = ["............", ".KKKKKKK....", "KSSSSSSSK...", "KSWSSSSSK...", "KssssssSK...", ".KKKNKKK....", "....KNK.....",
            "....KNK.....", "....KNK.....", "....KNK.....", "....KnK.....", ".....K......"],
        ["shield"] = ["..KKKKKKKK..", ".KBBBBBBBBK.", ".KBBWWWWBBK.", ".KBBWBBWBBK.", ".KBBWWWWBBK.", ".KBBBBBBBBK.", ".KBBBBBBBBK.",
            "..KBBBBBBK..", "..KbBBBBbK..", "...KbBBbK...", "....KbbK....", ".....KK....."],
        ["star"] = ["......K.....", ".....KYK....", ".....KYK....", "KKKKKYYYKKKK", ".KYYYYYYYYK.", "..KYYYYYYK..", "...KYYYYK...",
            "..KYYKKYYK..", "..KYK..KYK..", ".KK......KK.", "............", "............"],
        ["heart"] = ["............", ".KKK...KKK..", "KRRRK.KRRRK.", "KRWRRKRRRRK.", "KRRRRRRRRRK.", "KRRRRRRRRRK.", ".KRRRRRRRK..",
            "..KRRRRRK...", "...KRRRK....", "....KRK.....", ".....K......", "............"],
        ["diamond"] = [".....KK.....", "....KCCK....", "...KCWCCK...", "..KCWCCCCK..", ".KCCCCCCCCK.", "KCCCCCCCCCCK", ".KBCCCCCCBK.",
            "..KBCCCCBK..", "...KBCCBK...", "....KBBK....", ".....KK.....", "............"],
        ["flame"] = [".....K......", "....KOK.....", "....KOOK....", "...KOOOK.K..", "..KOOYOOKOK.", "..KOYYYOOOK.", ".KOOYYYYOOK.",
            ".KOYYWYYYOK.", ".KOYWWWYYOK.", "..KOYWWYOK..", "...KKOOKK...", ".....KK....."],
        ["bolt"] = ["......KKKK..", ".....KYYYK..", "....KYYYK...", "...KYYYK....", "..KYYYYKKK..", ".KYYYYYYYK..", "..KKKYYYK...",
            "....KYYK....", "...KYYK.....", "..KYK.......", "..KK........", "............"],
        ["leaf"] = ["........KKK.", "......KKGGK.", "....KKGGGGK.", "...KGGGgGGK.", "..KGGGgGGK..", "..KGGgGGGK..", ".KGGgGGGK...",
            ".KGgGGGK....", ".KgGGKK.....", "KgKKK.......", "Kg..........", "K..........."],
        ["drop"] = [".....KK.....", ".....KK.....", "....KBBK....", "....KBBK....", "...KBBBBK...", "..KBWBBBBK..", "..KBWBBBBK..",
            ".KBWBBBBBBK.", ".KBBBBBBBBK.", ".KbBBBBBBbK.", "..KbbBBbbK..", "...KKKKKK..."],
        ["moon"] = ["...KKKK.....", "..KYYYK.....", ".KYYYK......", "KYYYK.......", "KYYYK.......", "KYYYK.......", "KYYYYK......",
            "KYYYYYKKKK..", ".KYYYYYYYYK.", "..KyYYYYyK..", "...KKKKKK...", "............"],
        ["sun"] = ["K....KK....K", ".K...YY...K.", "....KKKK....", "...KYYYYK...", ".YKYYYYYYKY.", "KYKYYWYYYKYK", "KYKYYYYYYKYK",
            ".YKYYYYYYKY.", "...KYYYYK...", "....KKKK....", ".K...YY...K.", "K....KK....K"],
        ["snow"] = [".....KK.....", "..K..CC..K..", "...KKCCKK...", "...KCCCCK...", ".KKCC..CCKK.", "KCCC.CC.CCCK", "KCCC.CC.CCCK",
            ".KKCC..CCKK.", "...KCCCCK...", "...KKCCKK...", "..K..CC..K..", ".....KK....."],
        ["note"] = ["......KKK...", "......KPPK..", "......KPKPK.", "......KPK.K.", "......KPK...", "......KPK...", "......KPK...",
            "...KKKKPK...", "..KPPPPPK...", "..KPWPPPK...", "..KPPPPK....", "...KKKK....."],
        ["sword"] = ["..........KK", ".........KSK", "........KSK.", ".......KSK..", "......KSK...", ".....KSK....", "..K.KSK.....",
            "..KKSK......", "...KYK......", "..KNKKK.....", ".KNK........", ".KK........."],
        ["wrench"] = ["........KK..", ".......KSSK.", "......KSK.K.", "......KSSK..", ".....KSSK...", "....KSSK....", "...KSSK.....",
            "..KSSK......", ".KSSK.......", "KSSK........", "KSK.........", ".K.........."],
        ["book"] = ["............", ".KKKKKKKKKK.", ".KBBBBBBBBKK", ".KBWWWWWWBKK", ".KBBBBBBBBKK", ".KBBWWWWBBKK", ".KBBBBBBBBKK",
            ".KBBBBBBBBKK", ".KbbbbbbbbKK", ".KKKKKKKKKWK", "..KWWWWWWWWK", "...KKKKKKKK."],
        ["paw"] = ["..KK....KK..", ".KNNK..KNNK.", ".KNNK..KNNK.", "..KK....KK..", "KK..KKKK..KK", "KNKKNNNNKKNK", "KNKNNNNNNKNK",
            ".KKNNNNNNKK.", "..KNNNNNNK..", "..KNNNNNNK..", "...KKKKKK...", "............"],
        ["ball"] = ["...KKKKKK...", "..KRRRRRRK..", ".KRWRRRRRRK.", "KRRRRRRRRRRK", "KRRRKKKKRRRK", "KKKKKWWKKKKK", "KWWWKWWKWWWK",
            "KWWWWKKWWWWK", ".KWWWWWWWWK.", "..KWWWWWWK..", "...KKKKKK...", "............"],
        ["flag"] = [".K..........", ".KKKKKKKK...", ".KRRRRRRRK..", ".KRRRRRRRRK.", ".KRRRRRRRK..", ".KRRRRRRK...", ".KKKKKKKK...",
            ".KN.........", ".KN.........", ".KN.........", ".KN.........", "KKKK........"],
        ["wing"] = ["KK..........", "KWK.........", "KWWKK.......", "KWWWWKK.....", ".KWWWWWKK...", ".KWWWWWWWKK.", "..KWWSWWWWWK",
            "..KWWSSWWWK.", "...KWWSSWK..", "....KWWWK...", ".....KKK....", "............"],
        ["clover"] = ["...KK..KK...", "..KGGKKGGK..", "..KGGGGGGK..", "KKKGGGGGGKKK", "KGGGGgGGGGGK", "KGGGGgGGGGGK", "KKKGGGGGGKKK",
            "..KGGGGGGK..", "..KGGKKGGK..", "...KKKgK....", "......gK....", ".......K...."],
    };

    static readonly Dictionary<string, ImageTexture?> Textures = new();

    /// <summary>The beacon's picture (null for none or an unknown one).</summary>
    public static ImageTexture? Texture(string id)
    {
        if (Textures.TryGetValue(id, out var cached))
            return cached;
        ImageTexture? texture = null;
        if (Art.TryGetValue(id, out var rows))
        {
            var image = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8);
            for (var y = 0; y < rows.Length; y++)
                for (var x = 0; x < Math.Min(12, rows[y].Length); x++)
                    if (Palette.TryGetValue(rows[y][x], out var color))
                        image.SetPixel(x, y, color);
            texture = ImageTexture.CreateFromImage(image);
        }
        return Textures[id] = texture;
    }

    /// <summary>
    /// A beacon hovering over a character (above their name), bobbing gently; updates in place with
    /// <see cref="Set"/>.
    /// </summary>
    public sealed partial class Emblem : Sprite3D
    {
        double _time;
        readonly float _baseY;

        public string Id { get; private set; } = "";

        public Emblem(float height = 2.85f)
        {
            _baseY = height;
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            PixelSize = 0.04f;
            // Depth-tested, so it's hidden with its player under a bridge or behind a building.
            RenderPriority = 10;
            Position = new Vector3(0, height, 0);
            Visible = false;
        }

        public void Set(string id)
        {
            Id = id;
            Texture = BeaconArt.Texture(id);
            Visible = Texture is not null;
        }

        public override void _Process(double delta)
        {
            _time += delta;
            Position = new Vector3(0, _baseY + (float)Math.Sin(_time * 2.2) * 0.06f, 0);
        }
    }
}
