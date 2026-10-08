using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>Strict local rule payloads; not the legacy manifest/package serializer.</summary>
public static class TemplateRuleCodec
{
    public const int MaximumPayloadBytes = 1_048_576;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        CheckSize(json);
        return json;
    }

    public static T Deserialize<T>(string json)
    {
        CheckSize(json);
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException("Null rule payload.");
        }
        catch (JsonException)
        {
            throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
        }
    }

    public static void ValidateWorkspace(TemplateRuleWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Version < 0 || workspace.Aliases.IsDefault || workspace.Aliases.Length > 10_000)
            throw new TemplateRuleViolationException("TPL_RULE_WORKSPACE_INVALID");
        var targets = new HashSet<(string, string?)>();
        var numbers = new HashSet<(string?, int)>();
        foreach (var alias in workspace.Aliases)
        {
            if (alias is null || alias.Number <= 0 || alias.Number > 10_000)
                throw new TemplateRuleViolationException("TPL_RULE_ALIAS_INVALID");
            TemplateValueRules.Identifier(alias.PageId, "TPL_RULE_ALIAS_INVALID");
            if (alias.FrameId is not null)
                TemplateValueRules.Identifier(alias.FrameId, "TPL_RULE_ALIAS_INVALID");
            if (!targets.Add((alias.PageId, alias.FrameId))
                || !numbers.Add((alias.FrameId is null ? null : alias.PageId, alias.Number)))
                throw new TemplateRuleViolationException("TPL_RULE_ALIAS_INVALID");
        }
        if (workspace.Aliases.Any(alias => alias.FrameId is not null
            && !targets.Contains((alias.PageId, null))))
            throw new TemplateRuleViolationException("TPL_RULE_ALIAS_INVALID");
        if (workspace.Rules is { } rules)
            ValidateRuleShape(rules);
        _ = Serialize(workspace);
    }

    public static void ValidateRuleShape(TemplateRuleSet rules)
    {
        if (rules.SchemaVersion != TemplateRuleSet.CurrentSchemaVersion)
            throw new TemplateRuleViolationException("TPL_RULE_SCHEMA_UNSUPPORTED");
        if (rules.Branches.IsDefault || rules.Branches.Length > 128)
            throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
        foreach (var branch in rules.Branches)
        {
            if (branch is null || branch.Condition is null || !Enum.IsDefined(branch.Condition.Parity)
                || branch.Recipes.IsDefault || branch.Recipes.Length > 128)
                throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
            TemplateValueRules.Identifier(branch.BranchId, "TPL_RULE_PAYLOAD_INVALID");
            foreach (var recipe in branch.Recipes)
            {
                if (recipe is null || !Enum.IsDefined(recipe.Repeat) || !Enum.IsDefined(recipe.ColorRange)
                    || recipe.ColorSlots.IsDefault || recipe.ColorSlots.Length > 128
                    || recipe.FrameBindings.IsDefault || recipe.FrameBindings.Length > 1024)
                    throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
                TemplateValueRules.Identifier(recipe.RecipeId, "TPL_RULE_PAYLOAD_INVALID");
                TemplateValueRules.Identifier(recipe.PageId, "TPL_RULE_PAYLOAD_INVALID");
                foreach (var slot in recipe.ColorSlots)
                {
                    if (slot is null || !Enum.IsDefined(slot.Source))
                        throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
                    TemplateValueRules.Identifier(slot.SlotId, "TPL_RULE_PAYLOAD_INVALID");
                }
                foreach (var frame in recipe.FrameBindings)
                {
                    if (frame is null) throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
                    TemplateValueRules.Identifier(frame.FrameId, "TPL_RULE_PAYLOAD_INVALID");
                    TemplateValueRules.Identifier(frame.SlotId, "TPL_RULE_PAYLOAD_INVALID");
                }
            }
        }
    }

    public static string BusinessDigest(TemplateRevision revision) => Hash(Serialize(new
    {
        schema = "template-rules/v1",
        pages = revision.Pages.OrderBy(page => page.Order).Select(page => new
        {
            page.PageId, page.Order, page.Name, page.ContentType, page.GenerationMode,
            page.VariantCapacity, page.Enabled, page.TemplateResourceId, page.Notes, page.SizeTableCalibration
        }),
        frames = revision.FrameBindings.OrderBy(frame => frame.FrameId, StringComparer.Ordinal),
        rules = revision.RuleWorkspace?.Rules
    }));

    public static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static TemplateRuleWorkspace Remap(TemplateRuleWorkspace workspace,
        IReadOnlyDictionary<string, string> pages, IReadOnlyDictionary<string, string> frames)
    {
        ValidateWorkspace(workspace);
        // Tombstones also get fresh identities; never reuse a deleted alias on import.
        var pageMap = pages.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var frameMap = frames.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var alias in workspace.Aliases)
        {
            pageMap.TryAdd(alias.PageId, "page_" + Guid.NewGuid().ToString("N"));
            if (alias.FrameId is not null) frameMap.TryAdd(alias.FrameId, "frame_" + Guid.NewGuid().ToString("N"));
        }
        return workspace with
        {
            Version = 0,
            Aliases = [.. workspace.Aliases.Select(alias => alias with
            { PageId = pageMap[alias.PageId], FrameId = alias.FrameId is null ? null : frameMap[alias.FrameId] })],
            Rules = workspace.Rules is not { } rules ? null : rules with
            {
                Branches = [.. rules.Branches.Select(branch => branch with
                {
                    Recipes = [.. branch.Recipes.Select(recipe => recipe with
                    {
                        PageId = pages[recipe.PageId],
                        FrameBindings = [.. recipe.FrameBindings.Select(binding => binding with { FrameId = frames[binding.FrameId] })]
                    })]
                })]
            }
        };
    }

    public static void ValidatePackageRules(TemplatePackageManifest manifest)
    {
        if (manifest.RuleWorkspace is not { } workspace) return;
        ValidateWorkspace(workspace);
        var revision = TemplateRevision.CreateDraft(manifest.RevisionId, "package_validation", manifest.RevisionNo, DateTimeOffset.UnixEpoch);
        foreach (var page in manifest.Pages ?? [])
        {
            revision = revision.AddPage(TemplatePage.Create(page.PageId, manifest.RevisionId, page.Order, page.Name,
                Enum.Parse<TemplatePageContentType>(page.ContentType.ToString()), Enum.Parse<TemplateGenerationMode>(page.GenerationMode.ToString()),
                page.VariantCapacity, page.Enabled, page.ResourceLogicalId, notes: page.Notes, sizeTableCalibration: page.SizeTableCalibration));
            foreach (var frame in page.Bindings ?? [])
                revision = revision.AddFrameBinding(TemplatePackageService.ToDomain(frame, page.PageId, frame.FrameId));
        }
        revision = revision.WithRuleWorkspace(workspace);
        if (workspace.Reconcile(revision).Aliases.Length != workspace.Aliases.Length
            || workspace.Rules is { } rules && !TemplateRuleEvaluator.Validate(rules, revision).IsEmpty
            || !string.Equals(BusinessDigest(revision), manifest.RuleDigest, StringComparison.Ordinal))
            throw new TemplateRuleViolationException("TPL_RULE_PACKAGE_INVALID");
    }

    private static void CheckSize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes)
            throw new TemplateRuleViolationException("TPL_RULE_PAYLOAD_INVALID");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate rule property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }
}
