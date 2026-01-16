using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace RecipeSelector
{
	[HarmonyPatch(typeof(GuiDialogInventory))]
	[HarmonyPatch("ComposeSurvivalInvDialog")]
	public static class Patch_GuiDialogInventory_ComposeSurvivalInvDialog
	{
		private static RecipeSelector? _recipeSelector;

		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo composeMethod = AccessTools.Method(typeof(GuiComposer), "Compose", (Type[])null, (Type[])null);
			MethodInfo injectMethod = AccessTools.Method(typeof(Patch_GuiDialogInventory_ComposeSurvivalInvDialog), "Inject", (Type[])null, (Type[])null);
			CodeInstruction[] codes = instructions.ToArray();
			for (int i = 0; i < codes.Length - 2; i++)
			{
				if (CodeInstructionExtensions.Calls(codes[i + 1], composeMethod))
				{
					yield return CodeInstruction.LoadArgument(0, false);
					yield return CodeInstruction.LoadField(typeof(GuiDialogInventory), "craftingInv", false);
					yield return new CodeInstruction(OpCodes.Call, (object)injectMethod);
				}
				yield return codes[i];
			}
			yield return codes[^2];
			yield return codes[^1];
		}

		private static GuiComposer Inject(GuiComposer composer, IInventory inv)
		{
			//IL_0024: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Expected O, but got Unknown
			GuiComposerHelpers.AddButton(composer, Lang.Get("Next", Array.Empty<object>()), new ActionConsumable(OnClick), ElementBounds.Fixed(412.0, 280.0, 60.0, 30.0), CairoFont.WhiteSmallText(), (EnumButtonStyle)3, "buttonNext");
			GuiComposerHelpers.GetButton(composer, "buttonNext").SetOrientation((EnumTextOrientation)2);
			return composer;
			bool OnClick()
			{
				IInventory obj = inv;
				InventoryCraftingGrid val = (InventoryCraftingGrid)(object)((obj is InventoryCraftingGrid) ? obj : null);
				if (val != null)
				{
					if (_recipeSelector == null)
					{
						_recipeSelector = ((InventoryBase)val).Api.ModLoader.GetModSystem<RecipeSelector>(true);
					}
					ICoreAPI api = ((InventoryBase)val).Api;
					IClientPlayer player = ((ICoreClientAPI)((api is ICoreClientAPI) ? api : null)).World.Player;
					_recipeSelector.OnNextClicked(player);
				}
				return true;
			}
		}
	}
	[HarmonyPatch(typeof(InventoryCraftingGrid))]
	[HarmonyPatch("FindMatchingRecipe")]
	public static class Patch_InventoryCraftingGrid_FindMatchingRecipe
	{
		private static RecipeSelector? _recipeSelector;

		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
		{
			MethodInfo foundMatchMethod = AccessTools.Method(typeof(InventoryCraftingGrid), "FoundMatch", (Type[])null, (Type[])null);
			int lastMatchingRecipeIndex = generator.DeclareLocal(typeof(GridRecipe)).LocalIndex;
			yield return CodeInstruction.LoadArgument(0, false);
			yield return CodeInstruction.LoadField(typeof(InventoryCraftingGrid), "MatchingRecipe", false);
			yield return CodeInstruction.StoreLocal(lastMatchingRecipeIndex);
			int recipeListIndex = generator.DeclareLocal(typeof(List<GridRecipe>)).LocalIndex;
			yield return new CodeInstruction(OpCodes.Newobj, (object)typeof(List<GridRecipe>).GetConstructor(Type.EmptyTypes));
			yield return CodeInstruction.StoreLocal(recipeListIndex);
			CodeInstruction[] codes = instructions.ToArray();
			for (int i = 0; i < codes.Length - 3; i++)
			{
				if (CodeInstructionExtensions.Calls(codes[i + 2], foundMatchMethod))
				{
					yield return CodeInstruction.LoadLocal(recipeListIndex, false);
					yield return codes[i + 1];
					yield return CodeInstruction.Call(typeof(List<GridRecipe>), "Add", (Type[])null, (Type[])null);
					i += 3;
				}
				else
				{
					yield return codes[i];
				}
			}
			Label endLabel = generator.DefineLabel();
			yield return CodeInstruction.LoadLocal(recipeListIndex, false);
			yield return CodeInstruction.Call(typeof(List<GridRecipe>), "get_Count", (Type[])null, (Type[])null);
			yield return new CodeInstruction(OpCodes.Ldc_I4_0, (object)null);
			yield return new CodeInstruction(OpCodes.Cgt, (object)null);
			yield return new CodeInstruction(OpCodes.Brfalse_S, (object)endLabel);
			yield return CodeInstruction.LoadArgument(0, false);
			yield return CodeInstruction.LoadLocal(lastMatchingRecipeIndex, false);
			yield return CodeInstruction.LoadLocal(recipeListIndex, false);
			yield return CodeInstruction.LoadArgument(0, false);
			yield return CodeInstruction.LoadField(typeof(InventoryCraftingGrid), "Api", false);
			yield return CodeInstruction.LoadArgument(0, false);
			yield return CodeInstruction.Call(typeof(InventoryCraftingGrid), "get_Player", (Type[])null, (Type[])null);
			yield return CodeInstruction.Call(typeof(Patch_InventoryCraftingGrid_FindMatchingRecipe), "Inject", (Type[])null, (Type[])null);
			yield return new CodeInstruction(OpCodes.Call, (object)foundMatchMethod);
			codes[^3].labels.Add(endLabel);
			yield return codes[^3];
			yield return codes[^2];
			yield return codes[^1];
		}

		private static GridRecipe Inject(GridRecipe currentMatchingRecipe, List<GridRecipe> recipes, ICoreAPI api, IPlayer player)
		{
			if (recipes.Count == 1)
			{
				return recipes[0];
			}
			if (_recipeSelector == null)
			{
				_recipeSelector = api.ModLoader.GetModSystem<RecipeSelector>(true);
			}
			if (_recipeSelector.ConsumeNextPressed(player) && currentMatchingRecipe != null && HasRecipe(currentMatchingRecipe))
			{
				for (int i = 0; i < recipes.Count; i++)
				{
					if (recipes[i] == currentMatchingRecipe)
					{
						GridRecipe val = recipes[(i + 1 < recipes.Count) ? (i + 1) : 0];
						_recipeSelector.OnRecipeSelected(player, val);
						return val;
					}
				}
			}
			if (_recipeSelector.TryGetLastCraftedRecipe(player, out GridRecipe lastCraftedRecipe) && HasRecipe(lastCraftedRecipe))
			{
				return lastCraftedRecipe;
			}
			if (currentMatchingRecipe != null && HasRecipe(currentMatchingRecipe))
			{
				return currentMatchingRecipe;
			}
			return recipes[0];
			bool HasRecipe(GridRecipe recipe)
			{
				return recipes.Any((GridRecipe x) => x.Output.ResolvedItemstack.Equals(api.World, recipe.Output.ResolvedItemstack, Array.Empty<string>()));
			}
		}
	}
	public class RecipeSelector : ModSystem
	{
		[ProtoContract]
		private record NextRecipePacket;

		private IClientNetworkChannel? _clientChannel;

		private readonly HashSet<string> _nextPressedPlayers = new HashSet<string>();

		private readonly Dictionary<string, GridRecipe> _lastCraftedRecipeByPlayer = new Dictionary<string, GridRecipe>();

		public override void StartClientSide(ICoreClientAPI api)
		{
			_clientChannel = api.Network.RegisterChannel("recipeselector").RegisterMessageType<NextRecipePacket>();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			api.Network.RegisterChannel("recipeselector").RegisterMessageType<NextRecipePacket>().SetMessageHandler<NextRecipePacket>((NetworkClientMessageHandler<NextRecipePacket>)OnNextRecipePacketReceived);
		}

		public void OnNextClicked(IClientPlayer player)
		{
			_nextPressedPlayers.Add(((IPlayer)player).PlayerUID);
			IClientNetworkChannel? clientChannel = _clientChannel;
			if (clientChannel != null)
			{
				clientChannel.SendPacket<NextRecipePacket>(new NextRecipePacket());
			}
		}

		private void OnNextRecipePacketReceived(IServerPlayer fromPlayer, NextRecipePacket packet)
		{
			_nextPressedPlayers.Add(((IPlayer)fromPlayer).PlayerUID);
			UpdateRecipe((IPlayer)(object)fromPlayer);
		}

		public bool ConsumeNextPressed(IPlayer player)
		{
			if (_nextPressedPlayers.Contains(player.PlayerUID))
			{
				_nextPressedPlayers.Remove(player.PlayerUID);
				return true;
			}
			return false;
		}

		public void OnRecipeSelected(IPlayer player, GridRecipe recipe)
		{
			if (_lastCraftedRecipeByPlayer.ContainsKey(player.PlayerUID))
			{
				_lastCraftedRecipeByPlayer[player.PlayerUID] = recipe;
			}
			else
			{
				_lastCraftedRecipeByPlayer.Add(player.PlayerUID, recipe);
			}
		}

		public bool TryGetLastCraftedRecipe(IPlayer player, [NotNullWhen(true)] out GridRecipe? lastCraftedRecipe)
		{
			return _lastCraftedRecipeByPlayer.TryGetValue(player.PlayerUID, out lastCraftedRecipe);
		}

		private static void UpdateRecipe(IPlayer player)
		{
			string inventoryName = player.InventoryManager.GetInventoryName("craftinggrid");
			IInventory inventory = player.InventoryManager.GetInventory(inventoryName);
			InventoryCraftingGrid obj = (InventoryCraftingGrid)(object)((inventory is InventoryCraftingGrid) ? inventory : null);
			AccessTools.Method(typeof(InventoryCraftingGrid), "FindMatchingRecipe", (Type[])null, (Type[])null).Invoke(obj, Array.Empty<object>());
		}
	}
	public class VanillaPatches : ModSystem
	{
		private Harmony _harmonyInstance;

		private string PatchCode { get; } = typeof(VanillaPatches).AssemblyQualifiedName;

		public override void StartPre(ICoreAPI api)
		{
			ICoreClientAPI val = (ICoreClientAPI)(object)((api is ICoreClientAPI) ? api : null);
			if (val != null)
			{
				if (!val.IsSinglePlayer)
				{
					PatchAll();
				}
			}
			else
			{
				PatchAll();
			}
		}

		private void PatchAll()
		{
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_0011: Expected O, but got Unknown
			_harmonyInstance = new Harmony(PatchCode);
			_harmonyInstance.PatchAll();
			IEnumerable<MethodBase> patchedMethods = _harmonyInstance.GetPatchedMethods();
			((ModSystem)this).Mod.Logger.Notification("Harmony patched: " + string.Join(",", patchedMethods));
		}

		public override void Dispose()
		{
			Harmony harmonyInstance = _harmonyInstance;
			if (harmonyInstance != null)
			{
				harmonyInstance.UnpatchAll(PatchCode);
			}
		}
	}
}
