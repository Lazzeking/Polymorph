## Version 2.2.0 (2026-10-15)

- The mod is named Polymorph
- `/polymorph syntax` checks the denylist and reports lines that match no grid recipe, without changing the list
- Loading and `/polymorph reload` skip those lines and say which ones they were
- Each result in the conflicts file has the exact deny text to copy

## Version 2.1.3 (2026-10-14)

- `/rsft2 reload` reads the denylist again and sends it to connected players
- The conflicts file lists only an arrangement that crafts more than one different item

## Version 2.1.2 (2026-10-13)

- `/rsft2 conflicts` writes that file under ModConfig
- A denylist in `ModConfig/recipeselectorforkt2.json` is sent to clients
- A denied recipe never crafts, even when it is the only match

## Version 2.1.1 (2026-10-10)

- Clicking the button opens a slot for each result
- Clicking a slot selects that recipe and closes the panel

## Version 2.1.0 (2026-10-08)

- When several recipes match, a button appears beside the output slot
- The same arrangement keeps the result you picked

## Version 2.0.1 (2026-10-08)

- Updated the Recipe Selector fork so it runs on the current game, Vintage Story 1.22.x
