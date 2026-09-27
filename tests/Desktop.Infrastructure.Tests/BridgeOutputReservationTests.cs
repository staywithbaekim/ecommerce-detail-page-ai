using Desktop.Application.Rendering;
using Desktop.Contracts.Validation;
using Desktop.Infrastructure.Bridge;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgeOutputReservationTests
{
    [Fact]
    public void ReservationChecksWritableDestinationAndNeverChangesExistingOutputs()
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        var paths = new LocalAppPathPolicy(fixture.Root);
        var validator = new RenderPlanValidator(new JsonSchemaContractValidator());
        var original = fixture.Hashes();
        using (var reservation = BridgeOutputReservation.Create(fixture.Plan.ToJsonString(), "outputs", paths, validator))
        {
            Assert.DoesNotContain(fixture.Root, reservation.ToString(), StringComparison.Ordinal);
            Assert.Equal("never", reservation.Binding.OverwritePolicy);
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), ".reservation", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "outputs", "artifact_page_01")));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "outputs"), ".reservation", SearchOption.AllDirectories));
        var existing = Path.Combine(fixture.Root, "outputs", "artifact_page_01");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "01-detail.jpg"), "existing formal result");
        Assert.Throws<InvalidDataException>(() => BridgeOutputReservation.Create(fixture.Plan.ToJsonString(), "outputs", paths, validator));
        Assert.Equal("existing formal result", File.ReadAllText(Path.Combine(existing, "01-detail.jpg")));
        Assert.Equal(original, fixture.Hashes());
    }

    [Theory]
    [InlineData("artifact:stream")]
    [InlineData("CON")]
    [InlineData("LPT1.txt")]
    [InlineData("trailing.")]
    public void LogicalIdsCannotBecomeDeviceOrAlternateStreamDestinations(string id)
    {
        using var fixture = new BridgeResourceLeaseTests.Fixture();
        fixture.Plan["output"]!["artifact_set_id"] = id;
        fixture.Rehash();
        Assert.Throws<InvalidDataException>(() => BridgeOutputReservation.Create(fixture.Plan.ToJsonString(), "outputs",
            new LocalAppPathPolicy(fixture.Root), new RenderPlanValidator(new JsonSchemaContractValidator())));
    }
}
