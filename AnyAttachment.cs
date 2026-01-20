using System.Reflection;
using AnyAttachment.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace AnyAttachment;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "ifp.anyattachment";
    public override string Name { get; init; } = "AnyAttachment";
    public override string Author { get; init; } = "ifp";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.11");

    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class AnyAttachmentModPostDb(AnyAttachmentPatcher patcher) : IOnLoad
{
    public Task OnLoad() => patcher.Apply("PostDB", logSummary: false);
}

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 9999)]
public class AnyAttachmentModPostSpt(AnyAttachmentPatcher patcher) : IOnLoad
{
    public Task OnLoad() => patcher.Apply("PostSpt", logSummary: true);
}

[Injectable]
public class AnyAttachmentPatcher(
    DatabaseServer databaseServer,
    ModHelper modHelper,
    ISptLogger<AnyAttachmentPatcher> logger)
{
    private const string MagSlotName = "mod_magazine";

    private sealed record PatchStats
    {
        public int RemovedForegripsFromHandguardPool { get; set; }

        public int AnyAmmo_MagazineSlotsUpdated { get; set; }
        public int AnyAmmo_WeaponChambersUpdated { get; set; }

        public int AnyMag_SlotsUpdated { get; set; }

        public int Cursed_SlotsUpdated { get; set; }
        public int Cursed_TemplatesConflictsCleared { get; set; }
    }

    public Task Apply(string phase, bool logSummary)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var settings = modHelper.GetJsonDataFromFile<Settings>(pathToMod, "config/settings.json");

        if (!settings.Active)
        {
            if (logSummary)
            {
                logger.Warning($"AnyAttachment: disabled via config/settings.json (Active=false) [{phase}]");
            }
            return Task.CompletedTask;
        }

        var stats = new PatchStats();

        var items = databaseServer.GetTables().Templates.Items;

        var ammoTpls = BuildAmmoTplList(items, settings);
        var modsBySlotName = BuildModsBySlotCategory(items);
        stats.RemovedForegripsFromHandguardPool = NormalizeSlotPools(modsBySlotName);

        if (settings.AllowAnyAmmo)
        {
            stats.AnyAmmo_MagazineSlotsUpdated = ApplyAnyAmmoToMagazines(items, settings, ammoTpls);
            stats.AnyAmmo_WeaponChambersUpdated = ApplyAnyAmmoToWeapons(items, settings, ammoTpls);
        }

        if (settings.AllowAnyMag)
        {
            stats.AnyMag_SlotsUpdated = ApplyAnyMag(items, modsBySlotName);
        }

        if (settings.Cursed.Enable)
        {
            var (slotsChanged, conflictsCleared) = ApplyCursed(items, modsBySlotName);
            stats.Cursed_SlotsUpdated = slotsChanged;
            stats.Cursed_TemplatesConflictsCleared = conflictsCleared;
        }

        if (logSummary)
        {
            logger.Success("[Any Attachment] Successfully loaded!");

            
        }

        return Task.CompletedTask;
    }

    private static int NormalizeSlotPools(Dictionary<string, HashSet<MongoId>> modsBySlotName)
    {
        if (!modsBySlotName.TryGetValue("mod_handguard", out var handguardPool))
        {
            return 0;
        }

        if (!modsBySlotName.TryGetValue("mod_foregrip", out var foregripPool))
        {
            return 0;
        }

        var before = handguardPool.Count;
        handguardPool.ExceptWith(foregripPool);
        var removed = before - handguardPool.Count;

        return removed;
    }

    private static HashSet<MongoId> BuildAmmoTplList(Dictionary<MongoId, TemplateItem> items, Settings settings)
    {
        var blacklist = new HashSet<string>(settings.IDs.Blacklist);
        var ammo = new HashSet<MongoId>();

        foreach (var kvp in items)
        {
            var tpl = kvp.Key;
            var item = kvp.Value;

            if (!string.Equals(item.Parent, settings.IDs.Ammo, StringComparison.Ordinal))
            {
                continue;
            }

            if (blacklist.Contains(tpl))
            {
                continue;
            }

            ammo.Add(tpl);
        }

        return ammo;
    }

    private static Dictionary<string, HashSet<MongoId>> BuildModsBySlotCategory(Dictionary<MongoId, TemplateItem> items)
    {
        var result = new Dictionary<string, HashSet<MongoId>>(StringComparer.Ordinal);

        foreach (var item in items.Values)
        {
            var props = item.Properties;
            if (props is null)
            {
                continue;
            }

            var slots = props.Slots;
            if (slots is null)
            {
                continue;
            }

            foreach (var slot in slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Name))
                {
                    continue;
                }

                if (!result.TryGetValue(slot.Name, out var set))
                {
                    set = new HashSet<MongoId>();
                    result[slot.Name] = set;
                }

                var slotProps = slot.Properties;
                if (slotProps is null)
                {
                    continue;
                }

                var filters = slotProps.Filters;
                if (filters is null)
                {
                    continue;
                }

                foreach (var filter in filters)
                {
                    if (filter.Filter is null)
                    {
                        continue;
                    }

                    foreach (var modTpl in filter.Filter)
                    {
                        set.Add(modTpl);
                    }
                }
            }
        }

        return result;
    }

    private static int ApplyAnyAmmoToMagazines(
        Dictionary<MongoId, TemplateItem> items,
        Settings settings,
        HashSet<MongoId> ammoTpls)
    {
        var changed = 0;

        foreach (var item in items.Values)
        {
            if (!string.Equals(item.Parent, settings.IDs.Magazine, StringComparison.Ordinal))
            {
                continue;
            }

            var props = item.Properties;
            var cartridges = props?.Cartridges;
            if (cartridges is null)
            {
                continue;
            }

            foreach (var cartridgeSlot in cartridges)
            {
                changed += ReplaceSlotFilterList(cartridgeSlot, ammoTpls);
            }
        }

        return changed;
    }

    private static int ApplyAnyAmmoToWeapons(
        Dictionary<MongoId, TemplateItem> items,
        Settings settings,
        HashSet<MongoId> ammoTpls)
    {

        var childrenByParent = new Dictionary<MongoId, List<MongoId>>();
        foreach (var kvp in items)
        {
            var tpl = kvp.Key;
            var parent = kvp.Value.Parent;
            if (!childrenByParent.TryGetValue(parent, out var list))
            {
                list = new List<MongoId>();
                childrenByParent[parent] = list;
            }

            list.Add(tpl);
        }

        var changed = 0;
        var visited = new HashSet<MongoId>();
        var queue = new Queue<MongoId>();
        queue.Enqueue(settings.IDs.Weapon);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!visited.Add(parentId))
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var childTpl in children)
            {
                if (!items.TryGetValue(childTpl, out var childItem))
                {
                    continue;
                }

                var chambers = childItem.Properties?.Chambers;
                if (chambers is not null && chambers.Any())
                {
                    foreach (var chamber in chambers)
                    {
                        changed += ReplaceSlotFilterList(chamber, ammoTpls);
                    }
                }
                else
                {
                    queue.Enqueue(childTpl);
                }
            }
        }

        return changed;
    }

    private static int ApplyAnyMag(
        Dictionary<MongoId, TemplateItem> items,
        Dictionary<string, HashSet<MongoId>> modsBySlotName)
    {
        if (!modsBySlotName.TryGetValue(MagSlotName, out var magPool))
        {
            return 0;
        }

        var changed = 0;

        foreach (var item in items.Values)
        {
            var slots = item.Properties?.Slots;
            if (slots is null)
            {
                continue;
            }

            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, MagSlotName, StringComparison.Ordinal))
                {
                    continue;
                }

                changed += ReplaceSlotFilterList(slot, magPool);
            }
        }

        return changed;
    }

    private static (int slotsChanged, int templatesWithConflictsCleared) ApplyCursed(
        Dictionary<MongoId, TemplateItem> items,
        Dictionary<string, HashSet<MongoId>> modsBySlotName)
    {
        var slotsChanged = 0;
        var templatesWithConflictsCleared = 0;

        foreach (var item in items.Values)
        {
            var props = item.Properties;
            var slots = props?.Slots;
            if (slots is null)
            {
                continue;
            }

            if (props!.ConflictingItems is { Count: > 0 })
            {
                props.ConflictingItems.Clear();
                templatesWithConflictsCleared++;
            }

            foreach (var slot in slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Name))
                {
                    continue;
                }

                if (string.Equals(slot.Name, MagSlotName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!modsBySlotName.TryGetValue(slot.Name, out var pool))
                {
                    continue;
                }

                slotsChanged += ReplaceSlotFilterList(slot, pool);
            }
        }

        return (slotsChanged, templatesWithConflictsCleared);
    }

    private static int ReplaceSlotFilterList(Slot slot, HashSet<MongoId> newAllowedTpls)
    {
        var filters = slot.Properties?.Filters;
        if (filters is null)
        {
            return 0;
        }

        var stable = new HashSet<MongoId>(newAllowedTpls);
        var changed = 0;

        foreach (var slotFilter in filters)
        {
            slotFilter.Filter = stable;
            changed++;
        }

        return changed;
    }
}
