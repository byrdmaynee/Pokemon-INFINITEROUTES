using Godot;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The player's Active Buddy (a party Pokémon chosen in the menu once 3 have been caught) walking one tile behind them.
/// Offline it's the save's buddy; online it's the one in the game the server sent. It hides while surfing, while the
/// player is hidden (spectating) and when it has fainted.
/// </summary>
public partial class OverworldScreen
{
    BuddyFollower? _buddy;

    /// <summary>The species following the player now (0 none); for smoke tests.</summary>
    public int BuddySpecies => _buddy is { Visible: true } b ? b.Species : 0;

    /// <summary>Each frame: shows, changes or hides the follower to match the game.</summary>
    void UpdateBuddy()
    {
        int species = 0;
        bool shiny = false, female = false;
        if (_online)
        {
            if (_onlineState?.ActiveBuddy is { } save)
                (species, shiny, female) = (save.Species, save.Shiny, save.Gender == Core.Battle.Gender.Female);
        }
        else if (_battles && OfflineSession.State.ActiveBuddy is { } mon)
            (species, shiny, female) = (mon.Species.Id, mon.Shiny, mon.Gender == Core.Battle.Gender.Female);
        if (_surfing || !_player.Visible)
            species = 0;
        if (_buddy is null || !IsInstanceValid(_buddy))
        {
            _buddy = new BuddyFollower(_assets);
            _world.AddChild(_buddy);
        }
        if (species == _buddy.Species && shiny == _buddy.Shiny && female == _buddy.Female)
            return;
        _buddy.SetPokemon(species, shiny, female);
        PlaceBuddy();
    }

    /// <summary>Stands the buddy just behind the player (after arriving somewhere, or being put back).</summary>
    void PlaceBuddy()
    {
        if (_buddy is null || !IsInstanceValid(_buddy))
            return;
        var behind = _tile - _dir;
        _buddy.Place(IsBlocked(behind) ? _tile : behind, DirToFacing(_dir), _height);
    }
}
