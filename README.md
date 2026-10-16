# Polymorph

Just like that, remember the other block game mod ? Yup, this block game too now has one! The survival crafting grid can make more than one item from the same ingredients if there are multiple recipes present, a button shows up next to the output slot so that you can click it and pick the result you want.

There are some more things that what was the original project i forked (Recipe Selector), other than adding a little icon and making it more than just cycling, i added some server side denylist for conflicts that could emerge, for these there are 3 commands added that are only available to server admins (or yourself if you play in sp).

`/polymorph conflicts` creates a .txt file in the ModConfig folder containings all found conflicts of recipes, with the relative string to paste in the mod own Polymorph.json denylist, a denied result will not craft not on the server side (authoritative) and not even should be shown on the client side.(beware roofs and their variants are present aswell, so just ignore them while i find a better way to "find" "real" conflicts)

Other than that, there is `/polymorph syntax` checks the file and tells in chat which lines don't match with a known recipe, after having checked with it you can do a simple `/polymorph reload` to send the good ones to the players who are connected at that time.

For Vintage Story 1.22.x.

## Prior work

This started from two other mods.

- [Recipe Selector](https://github.com/DArkHekRoMaNT/RecipeSelector) by [DArkHekRoMaNT](https://mods.vintagestory.at/show/user/614DA343A4AC0CE99F63)
- [Recipe Selector Fork](https://github.com/Just-Irish/Recipe-Selector-Fork) by Just_Irish which updated that mod for 1.21

They are MIT and so is this one.
