using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Tavis;
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

    [ThreadStatic] private static PendingPatch? _pending;

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

    internal static void Begin(ICoreAPI api, EnumJsonPatchOp op, AssetLocation? file, AssetLocation? source)
    {
        _pending = file != null && (file.Path.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase))
            ? new PendingPatch(api, op, file.Domain, source?.ToString() ?? "")
            : null;
    }

    internal static void End()
    {
        _pending = null;
    }

    internal static Dictionary<string, string>? Before(object? document)
    {
        return _pending != null && document is JObject root ? Snapshot(root) : null;
    }

    internal static void After(object? document, Dictionary<string, string> before)
    {
        PendingPatch? pending = _pending;
        if (pending == null || document is not JObject root || root.GetValue("code", StringComparison.OrdinalIgnoreCase)?.Value<string>() is not string code)
        {
            return;
        }

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
            pending.Api.Logger.Notification(
                "Polymorph kept the quern result " + old + " of " + pending.Domain + ":" + code
                + (pattern.Length > 0 ? " (" + pattern + ")" : "") + " as a choice, after " + pending.Source + " added another one.");
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

    private sealed record PendingPatch(ICoreAPI Api, EnumJsonPatchOp Op, string Domain, string Source);
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

[HarmonyPatchCategory(GrindingOverloads.PatchCategory)]
[HarmonyPatch(typeof(ModJsonPatchLoader), "ProcessSinglePatch")]
public static class ProcessSinglePatchPatch
{
    public static void Prefix(ICoreAPI ___api, AssetLocation fileLoc, ModJsonPatch patch, AssetLocation source)
    {
        GrindingOverloads.Begin(___api, patch.Op, fileLoc, source);
    }

    public static void Finalizer()
    {
        GrindingOverloads.End();
    }
}

[HarmonyPatchCategory(GrindingOverloads.PatchCategory)]
[HarmonyPatch(typeof(ModJsonPatchLoader), nameof(ModJsonPatchLoader.ApplyPatch))]
public static class ApplyPatchPatch
{
    public static void Prefix(ICoreAPI ___api, AssetLocation patchSourcefile, ModJsonPatch jsonPatch)
    {
        GrindingOverloads.Begin(___api, jsonPatch.Op, jsonPatch.File, patchSourcefile);
    }

    public static void Finalizer()
    {
        GrindingOverloads.End();
    }
}

// Both patch paths of the loader end in PatchDocument.ApplyTo, where the item JSON is compared before and after.
[HarmonyPatchCategory(GrindingOverloads.PatchCategory)]
[HarmonyPatch(typeof(PatchDocument), nameof(PatchDocument.ApplyTo), new[] { typeof(JToken) })]
public static class PatchDocumentApplyPatch
{
    public static void Prefix(object[] __args, out Dictionary<string, string>? __state)
    {
        __state = GrindingOverloads.Before(__args[0]);
    }

    public static void Postfix(object[] __args, Dictionary<string, string>? __state)
    {
        if (__state != null)
        {
            GrindingOverloads.After(__args[0], __state);
        }
    }
}
