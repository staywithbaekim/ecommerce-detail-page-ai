using Desktop.Application.Ai;

namespace Desktop.Application.Tests;

public sealed class AiCredentialRulesTests
{
    [Fact]
    public void MasksOnlyTheSuffix()
    {
        Assert.Equal("••••9876", AiCredentialRules.MaskSuffix("ark-test-secret-9876"));
        Assert.Equal("••••1234", AiCredentialRules.MaskSuffix("1234"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ark\nsecret")]
    [InlineData("ark\tsecret")]
    public void RejectsEmptyWhitespaceAndControlCharacters(string value)
    {
        Assert.Throws<ArgumentException>(() => AiCredentialRules.NormalizeAndValidate(value));
    }

    [Fact]
    public void RejectsCredentialManagerBlobOverflow()
    {
        var value = new string('x', AiCredentialRules.MaxUtf8Bytes + 1);

        Assert.Throws<ArgumentException>(() => AiCredentialRules.NormalizeAndValidate(value));
    }
}
