# Polymorph

When the survival crafting grid can make more than one thing, a button appears beside the output slot. Click it to open the other results, then click the one you want. The same arrangement keeps the result you picked.

Server admins can run `/polymorph conflicts` to write a list of exact grids that can craft more than one item. Each result has a deny line. Copy that quoted text into `ModConfig/polymorph.json` under `Deny`. A denied result never crafts, even when it is the only match. `/polymorph syntax` checks the file without changing it. Moderators and admins can run it. `/polymorph reload` reads the file again, reports any line that matches no grid recipe, and sends the valid lines to connected clients.

This build targets Vintage Story 1.22.x.

## Prior work

Polymorph was made using these mods:

- [Recipe Selector](https://github.com/DArkHekRoMaNT/RecipeSelector) by [DArkHekRoMaNT](https://mods.vintagestory.at/show/user/614DA343A4AC0CE99F63)
- [Recipe Selector Fork](https://github.com/Just-Irish/Recipe-Selector-Fork) by Just_Irish, the 1.21 update of that mod

Both were released under the MIT license, and this mod stays under that license.
