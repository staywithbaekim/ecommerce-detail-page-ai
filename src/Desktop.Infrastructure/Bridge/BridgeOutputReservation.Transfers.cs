using System.Security.Cryptography;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

public sealed partial class BridgeOutputReservation
{
    // Access is serialized by the owning broker. Files are unpublished and never opened by a caller path.
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private bool _transferFaulted;
    internal string TransferStage { get; private set; } = "none";

    internal async Task<AdapterOutputReceipt> WriteAsync(AdapterOutputWrite chunk, CancellationToken cancellationToken)
    {
        TransferStage = "binding";
        CheckBinding(chunk.PlanDigest, chunk.OutputRootHandle, chunk.ArtifactSetId, chunk.OutputKind, chunk.ByteLength);
        var bytes = DecodeChunk(chunk);

        if (!_uploads.TryGetValue(chunk.OutputKind, out var upload))
        {
            TransferStage = "create";
            if (chunk.Offset != 0) throw Invalid();
            var path = Path.Combine(StagingPath, chunk.OutputKind + ".part");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            OwnedRenderResourceFile? owned = null;
            try
            {
                BridgeResourceLease.VerifyOpenedPath(stream.SafeFileHandle, path);
                ValidatePath(path);
                owned = OwnedRenderResourceFile.Capture(path, stream.SafeFileHandle);
                if (TemporaryFiles is not null)
                    await TemporaryFiles.TrackAsync(Path.Combine(_stagingRelativePath, chunk.OutputKind + ".part"), owned,
                        cancellationToken).ConfigureAwait(false);
                upload = new(stream, chunk.ByteLength, owned);
                _uploads.Add(chunk.OutputKind, upload);
            }
            catch { stream.Dispose(); _transferFaulted = true; owned?.Dispose(); throw; }
        }
        TransferStage = "offset";
        if (upload.Sealed || upload.Length != chunk.ByteLength || upload.Stream.Position != chunk.Offset
            || upload.Stream.Length != chunk.Offset) throw Invalid();
        TransferStage = "write";
        try { await upload.Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
        catch { _transferFaulted = true; throw; }
        upload.AcceptedLength = upload.Stream.Position;
        return new(chunk.ExecutionId, Binding.ArtifactSetId, chunk.OutputKind, upload.Stream.Position, false);
    }

    // Cancellation keeps only immutable binding and accepted-length facts, never reopens the disposed file.
    internal void ValidateCancelledWrite(AdapterOutputWrite chunk)
    {
        CheckCancelledBinding(chunk.PlanDigest, chunk.OutputRootHandle, chunk.ArtifactSetId, chunk.OutputKind, chunk.ByteLength);
        _ = DecodeChunk(chunk);
        TransferStage = "offset";
        if (_uploads.TryGetValue(chunk.OutputKind, out var upload)
            ? upload.Sealed || upload.Length != chunk.ByteLength || upload.AcceptedLength != chunk.Offset
            : chunk.Offset != 0) throw Invalid();
    }

    internal void ValidateCancelledSeal(AdapterOutputSeal request)
    {
        CheckCancelledBinding(request.PlanDigest, request.OutputRootHandle, request.ArtifactSetId, request.OutputKind, request.ByteLength);
        TransferStage = "length";
        if (!_uploads.TryGetValue(request.OutputKind, out var upload) || upload.Sealed
            || upload.Length != request.ByteLength || upload.AcceptedLength != request.ByteLength
            || request.Sha256.Length != 64 || !request.Sha256.All(char.IsAsciiHexDigit)) throw Invalid();
    }

    private void CheckCancelledBinding(string digest, string handle, string artifact, string kind, long length)
    {
        TransferStage = "binding";
        if (_transferFaulted || !string.Equals(digest, PlanDigest, StringComparison.OrdinalIgnoreCase)
            || handle != Binding.OutputRootHandle || artifact != Binding.ArtifactSetId
            || kind != "jpg" && !(IncludePsd && kind == "editable_psd") || length is < 1 or > 2147483648) throw Invalid();
    }

    private byte[] DecodeChunk(AdapterOutputWrite chunk)
    {
        TransferStage = "range";
        if (chunk.Count is < 1 or > BridgeAdapterProtocol.ResourceChunkByteLimit || chunk.Offset < 0
            || chunk.Offset > chunk.ByteLength - chunk.Count || chunk.Data.Length != (chunk.Count * 4 + 2) / 3) throw Invalid();
        byte[] bytes;
        TransferStage = "chunk_hash";
        try
        {
            bytes = Convert.FromBase64String(chunk.Data.Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - chunk.Data.Length % 4) % 4));
        }
        catch (FormatException) { throw Invalid(); }
        if (bytes.Length != chunk.Count || BridgeAdapterCryptography.Encode(bytes) != chunk.Data
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != chunk.Sha256) throw Invalid();
        return bytes;
    }

    internal async Task<AdapterOutputReceipt> SealAsync(AdapterOutputSeal request, CancellationToken cancellationToken)
    {
        TransferStage = "binding";
        CheckBinding(request.PlanDigest, request.OutputRootHandle, request.ArtifactSetId, request.OutputKind, request.ByteLength);
        TransferStage = "length";
        if (!_uploads.TryGetValue(request.OutputKind, out var upload) || upload.Sealed
            || upload.Length != request.ByteLength || upload.Stream.Length != request.ByteLength) throw Invalid();
        try
        {
            TransferStage = "seal_hash";
            await upload.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            upload.Stream.Flush(flushToDisk: true);
            upload.Stream.Position = 0;
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(upload.Stream, cancellationToken).ConfigureAwait(false));
            if (digest != request.Sha256) throw Invalid();
            // Retain a read-only lease after sealing. Independent worker/decoder reads
            // can coexist, while any write/delete (including another uploader) is denied.
            var path = upload.Stream.Name;
            upload.Stream.Dispose();
            upload.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            BridgeResourceLease.VerifyOpenedPath(upload.Stream.SafeFileHandle, path);
            ValidatePath(path);
            if (upload.Stream.Length != request.ByteLength
                || Convert.ToHexStringLower(await SHA256.HashDataAsync(upload.Stream, cancellationToken).ConfigureAwait(false)) != digest)
                throw Invalid();
            upload.Sealed = true;
            upload.Sha256 = digest;
            // This receipt confirms flushed bytes only, never media validity or formal publication.
            return new(request.ExecutionId, Binding.ArtifactSetId, request.OutputKind, upload.Length, true);
        }
        catch { _transferFaulted = true; throw; }
    }

    private void CheckBinding(string digest, string handle, string artifact, string kind, long length)
    {
        EnsureMatches(digest, Binding);
        if (_transferFaulted || handle != Binding.OutputRootHandle || artifact != Binding.ArtifactSetId
            || kind != "jpg" && !(IncludePsd && kind == "editable_psd") || length is < 1 or > 2147483648)
            throw Invalid();
    }

    private sealed class Upload(FileStream stream, long length, OwnedRenderResourceFile ownedFile)
    {
        public FileStream Stream { get; set; } = stream;
        public OwnedRenderResourceFile OwnedFile { get; } = ownedFile;
        public long Length { get; } = length;
        public long AcceptedLength { get; set; }
        public bool Sealed { get; set; }
        public string? Sha256 { get; set; }
    }
}
