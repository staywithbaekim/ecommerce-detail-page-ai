using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Tests;

public sealed class RenderPlanRegenerationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryAndDynamicAttemptsPreserveAllFrozenFactsAndValidateIndependently(bool dynamicSize)
    {
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json"));
        if (dynamicSize)
        {
            var fixture = SizeTableInsertionTests.Fixture();
            source = new RenderPlanPlacementMigration(validator).MigrateSizeTableInsertion(fixture.Plan, fixture.Target,
                fixture.Baseline, "plan_original", "attempt_original", "artifact_original", DateTimeOffset.UnixEpoch);
        }
        var regenerated = RenderPlanRegeneration.Create(source, DateTimeOffset.UtcNow);
        var another = RenderPlanRegeneration.Create(source, DateTimeOffset.UtcNow);
        Assert.True(validator.Validate(regenerated).IsValid);
        Assert.True(RenderPlanRegeneration.PreservesFacts(source, regenerated));
        Assert.False(RenderPlanRegeneration.PreservesFacts(source, source));
        using var before = JsonDocument.Parse(source);
        using var after = JsonDocument.Parse(regenerated);
        foreach (var field in new[] { "resources", "document", "targets", "size_table", "template_revision_id", "required_capabilities" })
            Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty(field), after.RootElement.GetProperty(field)));
        Assert.NotEqual(JsonNode.Parse(regenerated)!["output"]!["artifact_set_id"]!.GetValue<string>(),
            JsonNode.Parse(another)!["output"]!["artifact_set_id"]!.GetValue<string>());
        var changed = JsonNode.Parse(regenerated)!;
        changed["resources"]![0]!["sha256"] = new string('a', 64);
        changed["plan_digest"] = RenderPlanDigest.Compute(changed.ToJsonString());
        Assert.True(validator.Validate(changed.ToJsonString()).IsValid);
        Assert.False(RenderPlanRegeneration.PreservesFacts(source, changed.ToJsonString()));
    }
}
