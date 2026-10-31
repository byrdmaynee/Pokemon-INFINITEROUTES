# Offline Test World — manual test checklist

What to try in **Main menu → Offline Test World** (milestone M1b). Each item says what should happen;
anything else is a bug worth reporting with the HUD's location, season and tile numbers.

## Before you start

- [ ] Launch the client after pulling: the loading screen re-extracts your Black/White ROM once
      (extractor 0.5.3, ~20 s, ~245 MB in the cache). Later launches skip it.
- [ ] Opening two clients at once: the second waits for the first's extraction, then starts without
      extracting again.
- [ ] With no ROM extracted, the screen says to re-extract in Settings → ROM Library instead of crashing.

## Controls

| Key | Action |
|---|---|
| Arrows / WASD | Walk one tile per press; hold to keep walking |
| Shift (held) | Run |
| Z / Enter / Space | Talk to the NPC you face; press again (or Esc) to close |
| Mouse wheel | Zoom (0.5×–4×) |
| F3 | Collision overlay: red blocked, blue warp, green tall grass |
| F4 | Next season |
| Esc | Back to the main menu (closes a dialogue first) |

## Walking and the map

- [ ] You start in Nuvema Town; the HUD shows "Nuvema Town · <season> · tile x, y".
- [ ] Walking animates both feet; running uses the run frames and is about twice as fast.
- [ ] Facing changes when you press a direction into a wall (no step taken).
- [ ] You can't walk through houses, fences, trees, signs or water (compare with F3: red tiles).
- [ ] You can walk through tall grass (green on F3); nothing happens yet — encounters are M1c.
- [ ] Ground height follows slopes and steps; you never sink into or float above the ground.
- [ ] Walking north to Route 1 and on: chunks stream in ahead of you with no gaps at chunk seams,
      and the HUD location changes at the boundary.
- [ ] Walk far (e.g. along Route 1 to Accumula Town): no stutter build-up or missing chunks.
- [ ] Zoom out fully and in fully: the camera keeps the player centred.

## Warps

- [ ] Door: walk up into the player's house door → fade → inside 1F, stepping off the door mat.
- [ ] Stairs: 1F stairs → 2F bedroom; 2F stairs → back to 1F next to the stairs.
- [ ] Exit mat: stand on the 1F exit mat and press Down → outside, in front of the house door.
- [ ] Other houses and the lab in Nuvema: each door leads to its own interior and back out.
- [ ] Map edges: warps at route/town edges that lead to another map (caves, gates) take you there.
- [ ] Known gap: 77 warps placed on rails (some bridges, special routes) do nothing yet.

## NPCs

- [ ] Nuvema has townspeople standing in fixed spots; Mom is in the house kitchen (1F).
- [ ] Some NPCs turn to look around; some wander a few tiles from home and come back.
- [ ] NPCs block their tile: you can't walk into one, and a wanderer won't walk into you.
- [ ] Face an NPC and press Z: it turns toward you and a box opens with its NPC/script/sprite ids.
      Press Z/Enter/Esc to close it. You can't walk while it is open.
- [ ] NPC sprites face the right way in all four directions (right-facing ones are mirrored left frames
      for 7-frame sprites — check they look natural).
- [ ] Walking away and back reloads the NPCs of that area (wanderers return to their home spot).
- [ ] Known gaps: story NPCs (Cheren, Bianca, the gift boxes upstairs) are hidden until story flags
      exist; there is no real dialogue until the script engine; 23 sprite ids that are 3D models are
      invisible but still block their tile; 40 rail-placed NPCs are missing.

## Seasons

- [ ] The starting season follows the real month (Jan spring, Feb summer, Mar autumn, Apr winter, May
      spring, …), shown in the HUD.
- [ ] F4 cycles Spring → Summer → Autumn → Winter: trees and grass change colour, the change is
      instant and NPCs stay where they are.
- [ ] Houses, the lab, mailboxes and hedges are there in every season (seasonal building packs only
      hold the buildings that change; the rest come from spring's).
- [ ] Collision is identical in every season (use F3 while cycling).
- [ ] Interiors look the same in every season.
- [ ] Walk through a few routes in winter: no missing or untextured chunks.

## Animated textures

- [ ] The sea south of Nuvema scrolls and the surf line moves on the beach.
- [ ] Rivers and ponds along the routes flow; fountains in cities animate.
- [ ] Water stays continuous across chunk seams (no visible jump at chunk edges).
- [ ] Animation keeps running after a season change (F4) and after going indoors and back out.

## Things that are expected to be missing (not bugs yet)

- Frame-swap animations (swaying flowers, blinking lights) and animated objects are still static.
- No battles, wild encounters, menus, items, saving or other players.
- No sound or music.
