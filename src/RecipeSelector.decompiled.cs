using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
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
			bool injected = false;
			for (int i = 0; i < codes.Length - 2; i++)
			{
				if (CodeInstructionExtensions.Calls(codes[i + 1], composeMethod))
				{
					yield return CodeInstruction.LoadArgument(0, false);
					yield return CodeInstruction.LoadField(typeof(GuiDialogInventory), "craftingInv", false);
					yield return new CodeInstruction(OpCodes.Call, (object)injectMethod);
					injected = true;
				}
				yield return codes[i];
			}
			yield return codes[^2];
			yield return codes[^1];
			if (!injected)
			{
				throw new InvalidOperationException("Recipe Selector could not find GuiComposer.Compose in GuiDialogInventory.ComposeSurvivalInvDialog.");
			}
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
			// 1.22 FindMatchingRecipe returns on the first shaped or shapeless hit.
			// Keep scanning, remember every match, then call FoundMatch once with the chosen recipe.
			MethodInfo foundMatchMethod = AccessTools.Method(typeof(InventoryCraftingGrid), "FoundMatch");
			MethodInfo listAdd = AccessTools.Method(typeof(List<GridRecipe>), "Add");
			MethodInfo listCount = AccessTools.PropertyGetter(typeof(List<GridRecipe>), "Count");
			MethodInfo injectMethod = AccessTools.Method(typeof(Patch_InventoryCraftingGrid_FindMatchingRecipe), "Inject");
			FieldInfo apiField = AccessTools.Field(typeof(InventoryCraftingGrid), "Api");
			MethodInfo getPlayer = AccessTools.Method(typeof(InventoryCraftingGrid), "get_Player");
			int lastMatchingRecipeIndex = generator.DeclareLocal(typeof(GridRecipe)).LocalIndex;
			int recipeListIndex = generator.DeclareLocal(typeof(List<GridRecipe>)).LocalIndex;
			yield return CodeInstruction.LoadArgument(0);
			yield return CodeInstruction.LoadField(typeof(InventoryCraftingGrid), "MatchingRecipe");
			yield return CodeInstruction.StoreLocal(lastMatchingRecipeIndex);
			yield return new CodeInstruction(OpCodes.Newobj, typeof(List<GridRecipe>).GetConstructor(Type.EmptyTypes));
			yield return CodeInstruction.StoreLocal(recipeListIndex);
			CodeInstruction[] codes = instructions.ToArray();
			int replaced = 0;
			for (int i = 0; i < codes.Length; i++)
			{
				if (IsFoundMatchSite(codes, i, foundMatchMethod))
				{
					CodeInstruction addList = CodeInstruction.LoadLocal(recipeListIndex);
					MoveLabels(codes[i], addList);
					MoveLabels(codes[i + 1], addList);
					MoveLabels(codes[i + 2], addList);
					MoveLabels(codes[i + 3], addList);
					yield return addList;
					yield return codes[i + 1];
					yield return new CodeInstruction(OpCodes.Callvirt, listAdd);
					replaced++;
					i += 3;
					continue;
				}
				if (codes[i].opcode == OpCodes.Ret)
				{
					Label skip = generator.DefineLabel();
					yield return CodeInstruction.LoadLocal(recipeListIndex);
					yield return new CodeInstruction(OpCodes.Callvirt, listCount);
					yield return new CodeInstruction(OpCodes.Brfalse, skip);
					yield return CodeInstruction.LoadArgument(0);
					yield return CodeInstruction.LoadLocal(lastMatchingRecipeIndex);
					yield return CodeInstruction.LoadLocal(recipeListIndex);
					yield return CodeInstruction.LoadArgument(0);
					yield return new CodeInstruction(OpCodes.Ldfld, apiField);
					yield return CodeInstruction.LoadArgument(0);
					yield return new CodeInstruction(OpCodes.Call, getPlayer);
					yield return new CodeInstruction(OpCodes.Call, injectMethod);
					yield return new CodeInstruction(OpCodes.Call, foundMatchMethod);
					codes[i].labels.Add(skip);
				}
				yield return codes[i];
			}
			if (replaced != 2)
			{
				throw new InvalidOperationException("Recipe Selector expected to rewrite 2 FoundMatch calls in InventoryCraftingGrid.FindMatchingRecipe, but rewrote " + replaced + ".");
			}
		}

		private static bool IsFoundMatchSite(CodeInstruction[] codes, int index, MethodInfo foundMatchMethod)
		{
			if (index + 3 >= codes.Length || !CodeInstructionExtensions.Calls(codes[index + 2], foundMatchMethod))
			{
				return false;
			}
			OpCode leave = codes[index + 3].opcode;
			return leave == OpCodes.Leave || leave == OpCodes.Leave_S;
		}

		private static void MoveLabels(CodeInstruction from, CodeInstruction to)
		{
			if (from.labels.Count > 0)
			{
				to.labels.AddRange(from.labels);
				from.labels.Clear();
			}
			if (from.blocks.Count > 0)
			{
				to.blocks.AddRange(from.blocks);
				from.blocks.Clear();
			}
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
				return recipes.Any((GridRecipe x) => x.Output.ResolvedItemStack.Equals(api.World, recipe.Output.ResolvedItemStack, Array.Empty<string>()));
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
			_clientChannel = api.Network.RegisterChannel("recipeselectorforkt2").RegisterMessageType<NextRecipePacket>();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			api.Network.RegisterChannel("recipeselectorforkt2").RegisterMessageType<NextRecipePacket>().SetMessageHandler<NextRecipePacket>((NetworkClientMessageHandler<NextRecipePacket>)OnNextRecipePacketReceived);
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
