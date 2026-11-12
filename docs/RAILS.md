# Black/White rails

Some Unova areas aren't walked on the tile grid but along **rails**: paths the player follows (Skyarrow Bridge,
Castelia City's streets, Village Bridge, Gear Station, Victory Road, Icirrus, Opelucid, the Pokémon League, Celestial
and Dragonspiral Towers, the Abyssal Ruins). Some maps are rail-only (their chunks are type `NG`: model and buildings,
**no collision**); others are part grid, part rail (Castelia's plaza, Village Bridge, Icirrus, Opelucid, the League).
Their warps and NPCs use coordinate type 1 (rail positions).

We don't move along rails. The extractor turns them into grid collision (`Gen5Rails`, `Gen5WorldExtractor.RailCollision`),
so the server and client walk them like any other map. Movement doesn't follow the curve, and the camera doesn't turn
with it. That is close enough for an MMO, and real rail movement can come later.

## Finding a zone's rail file

1. ARM9 (White USA: RAM `0x0209E03E`; code `0x02013CB8` reads it): u16 entries, zone id in the low 11 bits and an index
   into `a/1/0/9` in the high 5, ending in a 0. Found by `Gen5Rails.ZoneRails`: the longest run of distinct zones, read
   back from a 0, whose indices cover the whole of `a/1/0/9`.
2. `a/1/0/9` (arc 0x6D), member *index*: u16 rail file, u16 attribute file (`0xFFFF` = none).
3. `a/0/7/9`: the rail files (members 0–16, magic `RL`; 17–33 are another format, probably attributes).

| Zone | Place | Rail file |
|---|---|---|
| 28 | Castelia City (ring road by the harbour) | 5 |
| 30, 36 | Castelia City (plaza; street) | 6, 0 |
| 249 | Skyarrow Bridge | 11 |
| 255 | Village Bridge | 12 |
| 214 | Victory Road | 9 |
| 66 | Gear Station | 1 |
| 114 | Icirrus City | 2 |
| 121 | Opelucid City | 3 |
| 137 | Pokémon League | 4 |
| 209, 211 | Dragonspiral Tower | 7, 8 |
| 241–244 | Abyssal Ruins | 10 |
| 338–341 | Celestial Tower | 13–16 |

## Rail file (`RL`)

Header: `"RL"`, u16 5, then u32 offsets to six sections (the last = file length). Section 0 is one u32 (0x1000).
Positions are fx32 (÷ 65536 = tiles) in the map's tile space: the same space as the matrix grid, but they can run past
the matrix because the models overhang their cells.

- **Points** (112 bytes each): u32 line[4] (0xFFFF = none), u32 key[4] (which way each line leaves), u32 width[4]
  (the **half**-width of that line at this point, in 1/16 tile), fx32 x, y (height), z, u32 index, char name[48]
  ("RE_POINT_0", "POINT_START" …).
- **Lines** (72 bytes each): u32 point A, u32 point B, u32, u32 line index, u32 camera (index into section 3), u32
  attribute, char name[48].
- **Section 3** (36 bytes each): camera settings, indexed by lines and points. Not needed.
- **Section 4** (36 bytes each, one per line): the line's shape. u32 kind: 0 = straight A → B; otherwise (1, 2 seen) an
  arc around the fx32 centre (x, y, z) that follows, from A to B the short way round. The radius goes from |A − centre|
  to |B − centre|. Castelia's ring road is five arcs around one centre; Skyarrow Bridge's north end has curved ramps.

## Rail positions (warps, NPCs)

- **Warps**: X = line, Height = distance along it from A (tiles), Z = signed sideways offset (tiles). Positive offsets
  are left of the direction of travel (x right, z down: heading east, + is north). Width runs along the line and Depth
  across it.
  - A *door* sits beside the rail (|offset| ≥ 2, depth 1). It becomes a solid `Door` tile you walk into, and arriving
    faces back towards the rail.
  - A *crossing* spans the rail (Skyarrow's ends, Castelia's exits). It becomes an open `Edge` you stand on, and arriving
    faces into the line.
- **NPCs**: X = line, Y = distance, low 16 bits of Height = signed sideways offset. Their height comes from the line's
  end points.
- **Furniture** (signs) on rails isn't converted yet.

## Collision from rails

For every map with rail zones, `RailCollision`:

1. Draws each line with its half-width (interpolated between its points; a tile counts when its centre is within half a
   tile of the band).
   - A side lined with doors is narrowed to stop one tile short of the **farthest** door on it (nearer doors are kiosks
     within the street).
   - Crossings are opened and doors made solid.
2. Rail-only chunks (no collision) take that drawing as their grid.
3. On grid chunks, the rails open what they cover, solid or water (Village Bridge's deck is a rail over the river). The
   rails start at the grid's junction tiles (low byte `0xA3` in the third u16 of the tile record).
4. Where rails run past the matrix, the map gains model-less cells (chunk −1, own `tiles`) that only carry collision.

The result is written per cell as `chunks/rail-{map}-{x}-{y}.tiles` and named in `WorldCell.Tiles`.

## Map cell sizes

Map type (zone header byte 0) sets the matrix cell size. An overlay (21 in White) has one 72-byte record per type,
starting with the size as fx32: 0x200000 = 512 units = 32 tiles. Type 1 (Skyarrow Bridge) is 128 tiles and type 5
(zone 136, Pokémon League) is 64, with a 64×64 collision grid. `Gen5Rails.BlockTiles` finds the table, and
`WorldIndex.ChunkTiles` carries the size.

The models' node translations are in unscaled units: a model's position scale applies to its vertices before the node
transforms (`Nsbmd`). Rail maps' models rely on that, because their pieces are offset by whole cells.

## Known gaps

- Castelia's plaza (zone 30, chunk 108) draws without its floor. This was there before the rail work.
- Rail streets of zone 30 that run off its chunk show no ground. In the ROM they're drawn by the neighbouring street
  zones' maps, which we load as separate maps.
- Rail furniture (signs) isn't placed.
