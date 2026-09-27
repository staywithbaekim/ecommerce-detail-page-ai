using System.Text.Json;
using System.Text.Json.Nodes;
using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Tests;

public sealed class RenderPlanPlacementMigrationTests
{
    private readonly RenderPlanValidator _validator = new(new JsonSchemaContractValidator());
    private static string Original() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "render-plan-v1.valid.example.json"));
    private string Migrate(string? source = null, RenderTargetPlacement? placement = null) =>
        new RenderPlanPlacementMigration(_validator).Migrate(source ?? Original(),
            new Dictionary<string, RenderTargetPlacement> { ["frame_01"] = placement ?? new(4000, 6000, -5, 10, 800, 1200) },
            "plan_placement", "placement_attempt_01", "artifact_placement", DateTimeOffset.UnixEpoch);

    [Fact]
    public void ExplicitMigrationPreservesLegacyBytesAndTargetEvidenceAndChangesAttemptAndDigest()
    {
        var source = Original();
        using var before = JsonDocument.Parse(source);
        Assert.True(_validator.Validate(source).IsValid);
        Assert.False(RenderPlanPlacementMigration.IsExplicit(before.RootElement));
        var migrated = Migrate(source);
        using var after = JsonDocument.Parse(migrated);
        Assert.True(_validator.Validate(migrated).IsValid);
        Assert.True(RenderPlanPlacementMigration.IsExplicit(after.RootElement));
        Assert.Equal(source, Original());
        Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty("targets")[0].GetProperty("expected_layer"),
            after.RootElement.GetProperty("targets")[0].GetProperty("expected_layer")));
        Assert.NotEqual(before.RootElement.GetProperty("plan_digest").GetString(), after.RootElement.GetProperty("plan_digest").GetString());
        Assert.Throws<InvalidDataException>(() => Migrate(migrated));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("capability")]
    [InlineData("distorted")]
    [InlineData("outside")]
    [InlineData("unknown")]
    [InlineData("zero")]
    public void AmbiguousOrUnsupportedGeometryCannotBecomeExecutable(string mutation)
    {
        var plan = JsonNode.Parse(Migrate())!;
        var layout = plan["targets"]![0]!["layout"]!;
        switch (mutation)
        {
            case "missing": layout.AsObject().Remove("placement"); break;
            case "capability": plan["required_capabilities"]!.AsArray().RemoveAt(3); break;
            case "distorted": layout["placement"]!["height"] = 1000; break;
            case "outside": layout["placement"]!["left"] = 790; break;
            case "unknown": layout["placement"]!["photoshop_command"] = "transform"; break;
            case "zero": layout["placement"]!["source_width"] = 0; break;
        }
        plan["plan_digest"] = RenderPlanDigest.Compute(plan.ToJsonString());
        Assert.False(_validator.Validate(plan.ToJsonString()).IsValid);
    }

    [Fact]
    public void MigrationRequiresNewArtifactAndIdempotencyIdentityAndCompleteExplicitInput()
    {
        var migration = new RenderPlanPlacementMigration(_validator);
        Assert.Throws<InvalidDataException>(() => migration.Migrate(Original(), new Dictionary<string, RenderTargetPlacement>(),
            "plan_new", "attempt_new", "artifact_new", DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidDataException>(() => migration.Migrate(Original(),
            new Dictionary<string, RenderTargetPlacement> { ["frame_01"] = new(4000, 6000, 0, 0, 800, 1200) },
            "plan_new", "batch_page_attempt_01", "artifact_new", DateTimeOffset.UnixEpoch));
    }
}
