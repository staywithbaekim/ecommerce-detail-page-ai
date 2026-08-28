using Desktop.Application.Files;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Templates;

public sealed class TemplateOwnedStorage(IPathPolicy paths) : ITemplateOwnedStorage
{
    public Task DeleteGroupFilesAsync(string groupId, CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID");
        cancellationToken.ThrowIfCancellationRequested();
        var directory = paths.ResolveOwnedPath($"templates/groups/{groupId}");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        return Task.CompletedTask;
    }
}
