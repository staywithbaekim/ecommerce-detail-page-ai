using Desktop.Application.Rendering;

namespace Desktop.Application.Tests;

public sealed class RenderPlanDigestInteropTests
{
    // Shared vectors asserted by uxp-render-adapter/tests/work.test.cjs. They include numbers
    // that cannot pass through JavaScript Number without losing the canonical decimal text.
    [Theory]
    [InlineData("""{"plan_digest":"unused","z":"中文<&+`\"🚀","a":[1.23,0.00001,0,1e-28]}""",
        "sha256:6eadd0dcaa44acf671a6099b0a404e4c2661aa515620bad0083940fdd962e724")]
    [InlineData("""{"plan_digest":"ignored","n":12345678901234567890123456789,"a":0.0000000000000000000000000001}""",
        "sha256:f61ea7db7ee38b934f3b96e7639c07fdbf54a0ff83e4954e46614d9d58731726")]
    public void CanonicalDigestMatchesUxpLexicalVectors(string json, string digest) =>
        Assert.Equal(digest, RenderPlanDigest.Compute(json));
}
