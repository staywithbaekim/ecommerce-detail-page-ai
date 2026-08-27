namespace Desktop.Domain.Templates;

public sealed class TemplateRuleViolationException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
