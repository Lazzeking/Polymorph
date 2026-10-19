using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.ServerMods.NoObf;
using ModJsonPatch = Vintagestory.ServerMods.NoObf.JsonPatch;

namespace Polymorph;

// A mod that adds grindingProps to an item that already grinds into something would replace that result.
// Polymorph keeps the old result as one more quern choice instead. Only an add (or addeach) does this:
// a replace, addmerge or any other change to the result stays vanilla, and a removal drops the kept results.
public static class GrindingOverloads
{
    public const string PatchCategory = "polymorph-grinding-overload";

    private const string GrindingProps = "grindingProps";
    private const string GrindingPropsByType = "grindingPropsByType";

    private static readonly ConditionalWeakTable<ICoreAPI, List<GrindingOverload>> ByApi = new();

    public static List<GrindingOverload> For(ICoreAPI api)
    {
        return ByApi.GetValue(api, _ => new List<GrindingOverload>());
    }

    public static void Replace(ICoreAPI api, IEnumerable<GrindingOverload>? overloads)
    {
        ByApi.AddOrUpdate(api, overloads?.ToList() ?? new List<GrindingOverload>());
    }

    // The kept results that apply to this item, oldest first.
    public static IEnumerable<GrindingProperties> Resolve(IWorldAccessor world, CollectibleObject collectible)
    {
        if (world.Api == null || collectible.Code == null)
        {
            yield break;
        }

        string path = collectible.Code.Path;
        foreach (GrindingOverload overload in For(world.Api))
        {
            if (!overload.Domain.Equals(collectible.Code.Domain, StringComparison.OrdinalIgnoreCase)
                || (!path.Equals(overload.Code, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(overload.Code + "-", StringComparison.OrdinalIgnoreCase))
                || (overload.Pattern.Length > 0 && !WildcardUtil.Match(overload.Pattern, path)))
            {
                continue;
            }

            // Fill in variant placeholders such as flour-{type}, the way the game does when it loads the item.
            string json = overload.Stack;
            foreach (KeyValuePair<string, string> variant in collectible.Variant)
            {
                json = json.Replace("{" + variant.Key + "}", variant.Value);
            }

            JsonItemStack? stack;
            try
            {
                stack = new JsonObject(JToken.Parse(json)).AsObject<JsonItemStack>(null!, overload.Domain);
            }
            catch (Exception)
            {
                continue;
            }

            if (stack != null && stack.Resolve(world, "Polymorph kept grinding result of " + collectible.Code))
            {
                yield return new GrindingProperties { GroundStack = stack };
            }
        }
    }

    public static bool IsTypeFile(AssetLocation? file)
    {
        return file != null && (file.Path.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase));
    }

    public static JToken? Parse(IAsset? asset)
    {
        try
        {
            return asset == null ? null : JToken.Parse(asset.ToText());
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The grinding results of an item or block file just before a patch changes it.
    public static Capture? Before(ICoreAPI api, EnumJsonPatchOp op, AssetLocation file, AssetLocation? source, JToken? document)
    {
        return IsTypeFile(file) && document is JObject root
            ? new Capture(api, op, file.Domain, source?.ToString() ?? "", Snapshot(root))
            : null;
    }

    public static void After(Capture pending, JToken? document)
    {
        if (document is not JObject root || root.GetValue("code", StringComparison.OrdinalIgnoreCase)?.Value<string>() is not string code)
        {
            return;
        }

        Dictionary<string, string> before = pending.Before;
        Dictionary<string, string> after = Snapshot(root);
        List<GrindingOverload> overloads = For(pending.Api);
        foreach ((string pattern, string old) in before)
        {
            after.TryGetValue(pattern, out string? now);
            if (now == old)
            {
                continue;
            }

            if (now == null)
            {
                overloads.RemoveAll(overload => overload.Matches(pending.Domain, code, pattern));
                continue;
            }

            if (pending.Op is not (EnumJsonPatchOp.Add or EnumJsonPatchOp.AddEach)
                || overloads.Any(overload => overload.Matches(pending.Domain, code, pattern) && overload.Stack == old))
            {
                continue;
            }

            overloads.Add(new GrindingOverload { Domain = pending.Domain, Code = code, Pattern = pattern, Stack = old, Source = pending.Source });
            // The client applies the same patches when it joins, so only the server reports them.
            if (pending.Api.Side == EnumAppSide.Server)
            {
                // The kept result is JSON, so it goes in as an argument: its braces would break the log's format string.
                pending.Api.Logger.Notification(
                    "Polymorph kept the quern result {0} of {1}:{2}{3} as a choice, after {4} added another one.",
                    old, pending.Domain, code, pattern.Length > 0 ? " (" + pattern + ")" : "", pending.Source);
            }
        }
    }

    // The ground stack of grindingProps under "", and of each grindingPropsByType entry under its key.
    private static Dictionary<string, string> Snapshot(JObject root)
    {
        var stacks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (GroundStack(root.GetValue(GrindingProps, StringComparison.OrdinalIgnoreCase)) is string rootStack)
        {
            stacks[""] = rootStack;
        }

        if (root.GetValue(GrindingPropsByType, StringComparison.OrdinalIgnoreCase) is JObject byType)
        {
            foreach (JProperty property in byType.Properties())
            {
                if (GroundStack(property.Value) is string stack)
                {
                    stacks[property.Name] = stack;
                }
            }
        }

        return stacks;
    }

    private static string? GroundStack(JToken? props)
    {
        if (props is not JObject value)
        {
            return null;
        }

        JToken? stack = value.GetValue("groundStack", StringComparison.OrdinalIgnoreCase)
            ?? value.GetValue("grindedStack", StringComparison.OrdinalIgnoreCase);
        return stack == null || stack.Type == JTokenType.Null ? null : stack.ToString(Formatting.None);
    }

    public sealed record Capture(ICoreAPI Api, EnumJsonPatchOp Op, string Domain, string Source, Dictionary<string, string> Before);
}

[ProtoContract]
public class GrindingOverload
{
    [ProtoMember(1)]
    public string Domain { get; set; } = "";

    [ProtoMember(2)]
    public string Code { get; set; } = "";

    // The grindingPropsByType key, or empty for grindingProps itself.
    [ProtoMember(3)]
    public string Pattern { get; set; } = "";

    [ProtoMember(4)]
    public string Stack { get; set; } = "";

    [ProtoMember(5)]
    public string Source { get; set; } = "";

    public bool Matches(string domain, string code, string pattern)
    {
        return Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)
            && Code.Equals(code, StringComparison.OrdinalIgnoreCase)
            && Pattern == pattern;
    }
}

[ProtoContract]
public class GrindingOverloadsPacket
{
    [ProtoMember(1)]
    public GrindingOverload[] Entries { get; set; } = Array.Empty<GrindingOverload>();
}

// The loader's main pass. The file is read from the loader's own cache, or from its asset before the loader first loads it.
[HarmonyPatchCategory(GrindingOverloads.PatchCategory)]
[HarmonyPatch(typeof(ModJsonPatchLoader), "ProcessSinglePatch")]
public static class ProcessSinglePatchPatch
{
    public static void Prefix(ICoreAPI ___api, Dictionary<AssetLocation, (JToken, IAsset)> ___jsonCache, AssetLocation fileLoc, ModJsonPatch patch, AssetLocation source, out GrindingOverloads.Capture? __state)
    {
        __state = null;
        if (!GrindingOverloads.IsTypeFile(fileLoc))
        {
            return;
        }

        AssetLocation key = fileLoc.Clone().WithPathAppendixOnce(".json");
        JToken? document = ___jsonCache.TryGetValue(key, out (JToken Token, IAsset Asset) cached) ? cached.Token : GrindingOverloads.Parse(___api.Assets.TryGet(key));
        __state = GrindingOverloads.Before(___api, patch.Op, key, source, document);
    }

    public static void Postfix(Dictionary<AssetLocation, (JToken, IAsset)> ___jsonCache, AssetLocation fileLoc, GrindingOverloads.Capture? __state)
    {
        if (__state != null && ___jsonCache.TryGetValue(fileLoc.Clone().WithPathAppendixOnce(".json"), out (JToken Token, IAsset Asset) cached))
        {
            GrindingOverloads.After(__state, cached.Token);
        }
    }
}

// The single-patch path, which writes the patched file back into its asset.
[HarmonyPatchCategory(GrindingOverloads.PatchCategory)]
[HarmonyPatch(typeof(ModJsonPatchLoader), nameof(ModJsonPatchLoader.ApplyPatch))]
public static class ApplyPatchPatch
{
    public static void Prefix(ICoreAPI ___api, AssetLocation patchSourcefile, ModJsonPatch jsonPatch, out GrindingOverloads.Capture? __state)
    {
        __state = null;
        AssetLocation? file = jsonPatch.File;
        if (file == null || file.Path.EndsWith('*') || !GrindingOverloads.IsTypeFile(file))
        {
            return;
        }

        AssetLocation key = file.Clone().WithPathAppendixOnce(".json");
        __state = GrindingOverloads.Before(___api, jsonPatch.Op, key, patchSourcefile, GrindingOverloads.Parse(___api.Assets.TryGet(key)));
    }

    public static void Postfix(ICoreAPI ___api, ModJsonPatch jsonPatch, GrindingOverloads.Capture? __state)
    {
        if (__state != null && jsonPatch.File != null)
        {
            GrindingOverloads.After(__state, GrindingOverloads.Parse(___api.Assets.TryGet(jsonPatch.File.Clone().WithPathAppendixOnce(".json"))));
        }
    }
}
