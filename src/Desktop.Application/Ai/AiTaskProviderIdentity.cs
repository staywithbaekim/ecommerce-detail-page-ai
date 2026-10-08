namespace Desktop.Application.Ai;

/// <summary>Non-secret identity of one configured task implementation, not a routing/failover policy.</summary>
public sealed record AiTaskProviderIdentity(
    string Provider, string Model, string ConfigurationVersion, string ConsentScope)
{
    // Existing persisted model versions keep their meaning. Explicit provider/model
    // identity is additionally included in new request fingerprints and audit records.
    public static AiTaskProviderIdentity LegacyTemplateRules { get; } = new(
        "volc-ark", "doubao-seed-2-1-pro-260628",
        "doubao-seed-2.1-pro-260628", AiConsentScopes.AnalysisCopy);

    public void Validate()
    {
        foreach (var value in new[] { Provider, Model, ConfigurationVersion, ConsentScope })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128
                || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/')))
                throw new InvalidOperationException("AI_PROVIDER_IDENTITY_INVALID");
    }
}
