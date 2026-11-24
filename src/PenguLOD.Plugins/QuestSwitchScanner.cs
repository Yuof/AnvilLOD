using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace PenguLOD.Plugins;

/// <summary>
/// Scans enabled <c>QUST</c> aliases and Papyrus <c>VMAD</c> script/alias object properties to identify exterior placed
/// references that quests or scripts toggle at runtime without an <c>EnableParent</c> link, routing them to dynamic LOD.
/// </summary>
public static class QuestSwitchScanner
{
    private static readonly string[] ToggleTokens =
    [
        "disable", "enable", "toggle", "switch", "swap", "stage", "build", "rebuilt", "construct",
        "destroy", "ruin", "rubble", "repair", "upgrade", "camp", "tent", "barricade", "siege", "hide", "show", "state"
    ];

    public static HashSet<FormKey> Scan(GameContext game, CancellationToken ct = default)
    {
        var toggled = new HashSet<FormKey>();
        foreach (var quest in game.LoadOrder.PriorityOrder.Quest().WinningOverrides())
        {
            ct.ThrowIfCancellationRequested();
            if (quest.IsDeleted) continue;

            bool questHasToggleScript = false;
            var toggleAliases = new HashSet<uint>();

            if (quest.VirtualMachineAdapter is { } vmad)
            {
                foreach (var script in vmad.Scripts)
                {
                    bool scriptMatches = MatchesToggleToken(script.Name);
                    if (scriptMatches) questHasToggleScript = true;
                    CollectScriptProperties(script, scriptMatches, toggled);
                }
                foreach (var aliasFrag in vmad.Aliases)
                {
                    uint? aliasId = aliasIdFromProperty(aliasFrag);
                    foreach (var script in aliasFrag.Scripts)
                    {
                        bool scriptMatches = MatchesToggleToken(script.Name);
                        if (scriptMatches && aliasId is { } id) toggleAliases.Add(id);
                        CollectScriptProperties(script, scriptMatches, toggled);
                    }
                }
            }

            if (quest.Aliases is { Count: > 0 } aliases)
            {
                foreach (var alias in aliases)
                {
                    if (alias.ForcedReference.IsNull) continue;
                    bool allowDisabled = alias.Flags?.HasFlag(QuestAlias.Flag.AllowDisabled) == true;
                    if ((allowDisabled && (questHasToggleScript || toggleAliases.Contains(alias.ID) || MatchesToggleToken(alias.Name)))
                        || toggleAliases.Contains(alias.ID))
                    {
                        toggled.Add(alias.ForcedReference.FormKey);
                    }
                }
            }
        }
        return toggled;
    }

    public static bool HasToggleScript(IPlacedObjectGetter placed)
    {
        if (placed.VirtualMachineAdapter is not { Scripts.Count: > 0 } vmad) return false;
        foreach (var script in vmad.Scripts)
        {
            if (MatchesToggleToken(script.Name)) return true;
            foreach (var prop in script.Properties)
                if (MatchesToggleToken(prop.Name)) return true;
        }
        return false;
    }

    private static uint? aliasIdFromProperty(IQuestFragmentAliasGetter aliasFrag) =>
        aliasFrag.Property.Alias >= 0 ? (uint)aliasFrag.Property.Alias : null;

    private static void CollectScriptProperties(IScriptEntryGetter script, bool scriptMatches, HashSet<FormKey> toggled)
    {
        foreach (var prop in script.Properties)
        {
            bool propMatches = scriptMatches || MatchesToggleToken(prop.Name);
            if (!propMatches) continue;
            if (prop is IScriptObjectPropertyGetter objProp && objProp.Alias < 0 && !objProp.Object.IsNull)
                toggled.Add(objProp.Object.FormKey);
            else if (prop is IScriptObjectListPropertyGetter listProp)
            {
                foreach (var item in listProp.Objects)
                    if (item.Alias < 0 && !item.Object.IsNull)
                        toggled.Add(item.Object.FormKey);
            }
        }
    }

    private static bool MatchesToggleToken(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var token in ToggleTokens)
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
