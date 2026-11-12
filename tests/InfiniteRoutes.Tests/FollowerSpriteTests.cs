using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Tests;

/// <summary>Where HeartGold/SoulSilver keep each species' walking sprite (a/0/8/1, entries 297–862).</summary>
public class FollowerSpriteTests
{
    [Fact]
    public void Each_species_finds_its_own_sprite_after_the_extras_before_it()
    {
        Assert.Equal(297, Gen4Followers.SpriteOf(1));        // Bulbasaur
        Assert.Equal(301, Gen4Followers.SpriteOf(4));        // Charmander, after Venusaur (299) and its female look (300)
        Assert.Equal(501, Gen4Followers.SpriteOf(201));      // Unown A; its other 27 letters follow
        Assert.Equal(529, Gen4Followers.SpriteOf(202));      // Wobbuffet
        Assert.Equal(824, Gen4Followers.SpriteOf(479));      // Rotom; its five appliance forms follow
        Assert.Equal(845, Gen4Followers.SpriteOf(493));      // Arceus
        // The 18 Arceus types end the archive: 566 sprites for 493 species.
        Assert.Equal(862, Gen4Followers.SpriteOf(493) + Gen4Followers.Extras[493]);
        Assert.Equal(566 - 493, Gen4Followers.Extras.Values.Sum());
        Assert.All(Gen4Followers.FemaleLooks, s => Assert.Equal(1, Gen4Followers.Extras[s]));
    }
}
