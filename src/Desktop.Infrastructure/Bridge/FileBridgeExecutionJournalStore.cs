using System.Text.Json;
using System.Text.Json.Serialization;
using Desktop.Application.Files;
using Desktop.Application.Rendering;
using Desktop.Infrastructure.Files;

namespace Desktop.Infrastructure.Bridge;

public sealed class FileBridgeExecutionJournalStore(IPathPolicy paths,
    string journalRelativePath = "render/bridge-executions-v1.json") : IBridgeExecutionJournalStore
{
    private const int ByteLimit = 16 * 1024 * 1024;
    private readonly AtomicFileStore _files = new(paths);
    // Preserve the existing PascalCase v1 file format, but reject ambiguous or incomplete recovery data.
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 64
    };

    public async Task<BridgeExecutionJournal?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(paths.ResolveOwnedPath(journalRelativePath), FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (stream)
        {
            if (stream.Length > ByteLimit) throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
            using var content = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, ByteLimit - content.Length + 1)), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (content.Length + count > ByteLimit) throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
                content.Write(buffer, 0, count);
            }
            try
            {
                return JsonSerializer.Deserialize<BridgeExecutionJournal>(content.GetBuffer().AsSpan(0, (int)content.Length), Options)
                    ?? throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
            }
            catch (JsonException) { throw new InvalidDataException("BRIDGE_JOURNAL_INVALID"); }
        }
    }

    public Task SaveAsync(BridgeExecutionJournal journal, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Options);
        if (bytes.Length > ByteLimit) throw new InvalidDataException("BRIDGE_JOURNAL_INVALID");
        return _files.WriteAtomicallyAsync(journalRelativePath, bytes, cancellationToken);
    }
}
