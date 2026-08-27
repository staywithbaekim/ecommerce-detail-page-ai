namespace Desktop.Domain.Tasks;

public sealed class TaskRuleViolationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
