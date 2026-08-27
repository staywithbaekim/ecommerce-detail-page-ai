using Desktop.Application.Psd;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed class TemplatePageImportService(
    ITemplateSourceFilePort sourceFiles,
    IPsdInspectionPort inspectionPort,
    ITemplateResourceRepository resources,
    TimeProvider timeProvider)
{
    public async Task<TemplatePageImportResult> ImportAsync(
        TemplatePageImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        ImportedTemplateResource? completed;
        try
        {
            completed = await resources.GetByCommandIdAsync(request.CommandId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Failure(Error(
                "TPL_REVISION_COMMIT_FAILED",
                retryable: true,
                "The previous template draft remains available. Retry the import."));
        }
        if (completed is not null)
        {
            var expectedResourceId = request.DuplicateDecision == TemplateDuplicateDecision.ReuseExisting
                ? request.SelectedDuplicateResourceId
                : request.ResourceId;
            return string.Equals(completed.ResourceId, expectedResourceId, StringComparison.Ordinal)
                ? Success(
                    request.DuplicateDecision == TemplateDuplicateDecision.ReuseExisting
                        ? TemplatePageImportStatus.ReusedExisting
                        : TemplatePageImportStatus.Imported,
                    completed,
                    idempotent: true)
                : Failure(Error(
                    "TPL_IMPORT_COMMAND_CONFLICT",
                    retryable: false,
                    "Restart the import with a new command."));
        }

        var fingerprintResult = await sourceFiles.FingerprintAsync(request.SourcePsdPath, cancellationToken)
            .ConfigureAwait(false);
        if (!fingerprintResult.IsSuccess)
        {
            return Failure(fingerprintResult.Error!);
        }

        var fingerprint = fingerprintResult.Fingerprint!;
        var relativePath = $"templates/groups/{request.GroupId}/drafts/{request.DraftId}/staging/"
            + $"{request.ResourceId}/template.psd";
        var copyResult = await sourceFiles.CopyVerifiedAsync(
            request.SourcePsdPath,
            fingerprint,
            relativePath,
            cancellationToken).ConfigureAwait(false);
        if (!copyResult.IsSuccess)
        {
            return Failure(copyResult.Error!);
        }

        var ownedCopy = copyResult.Copy!;
        IReadOnlyList<ImportedTemplateResource> duplicates;
        try
        {
            duplicates = await resources.FindBySha256Async(fingerprint.Sha256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await FailAndCleanAsync(
                ownedCopy.InternalRelativePath,
                Error(
                    "TPL_REVISION_COMMIT_FAILED",
                    retryable: true,
                    "The previous template draft remains available. Retry the import."),
                cancellationToken).ConfigureAwait(false);
        }
        if (duplicates.Count > 0)
        {
            if (request.DuplicateDecision == TemplateDuplicateDecision.NotSpecified)
            {
                return new TemplatePageImportResult(
                    TemplatePageImportStatus.DuplicateReviewRequired,
                    null,
                    duplicates,
                    Error(
                        "TPL_DUPLICATE_REVIEW_REQUIRED",
                        retryable: false,
                        "Select the existing copy, continue as a separate page, or cancel."),
                    Idempotent: false);
            }

            if (request.DuplicateDecision == TemplateDuplicateDecision.Cancel)
            {
                return await CancelAndCleanAsync(
                    ownedCopy.InternalRelativePath,
                    duplicates,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.DuplicateDecision == TemplateDuplicateDecision.ReuseExisting)
            {
                var selected = duplicates.SingleOrDefault(candidate => string.Equals(
                    candidate.ResourceId,
                    request.SelectedDuplicateResourceId,
                    StringComparison.Ordinal));
                if (selected is null
                    || !string.Equals(selected.GroupId, request.GroupId, StringComparison.Ordinal)
                    || !string.Equals(selected.DraftId, request.DraftId, StringComparison.Ordinal))
                {
                    return await FailAndCleanAsync(
                        ownedCopy.InternalRelativePath,
                        Error(
                            "TPL_DUPLICATE_SELECTION_INVALID",
                            retryable: false,
                            "Select one of the duplicate copies in this template draft."),
                        cancellationToken,
                        duplicates).ConfigureAwait(false);
                }

                if (!await sourceFiles.DeleteOwnedCopyAsync(
                        ownedCopy.InternalRelativePath,
                        cancellationToken).ConfigureAwait(false))
                {
                    return Failure(Error(
                        "TPL_IMPORT_CLEANUP_REQUIRED",
                        retryable: true,
                        "Restart the application to recover the duplicate staging copy."),
                        duplicates);
                }

                try
                {
                    var recorded = await resources.RecordExistingSelectionAsync(
                        request.CommandId,
                        selected,
                        cancellationToken).ConfigureAwait(false);
                    return Success(
                        TemplatePageImportStatus.ReusedExisting,
                        recorded,
                        idempotent: false,
                        duplicates);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    return Failure(Error(
                        "TPL_REVISION_COMMIT_FAILED",
                        retryable: true,
                        "The previous template draft remains available. Retry the duplicate choice."),
                        duplicates);
                }
            }
        }
        else if (request.DuplicateDecision == TemplateDuplicateDecision.ReuseExisting)
        {
            return await FailAndCleanAsync(
                ownedCopy.InternalRelativePath,
                Error(
                    "TPL_DUPLICATE_SELECTION_INVALID",
                    retryable: false,
                    "No matching internal copy is available. Continue with a new import."),
                cancellationToken).ConfigureAwait(false);
        }

        if (request.DuplicateDecision == TemplateDuplicateDecision.Cancel)
        {
            return await CancelAndCleanAsync(
                ownedCopy.InternalRelativePath,
                duplicates,
                cancellationToken).ConfigureAwait(false);
        }

        var inspection = await inspectionPort.InspectAsync(
            new PsdInspectionRequest(request.CommandId, ownedCopy.InternalRelativePath),
            cancellationToken).ConfigureAwait(false);
        if (!inspection.IsSuccess)
        {
            return await FailAndCleanAsync(
                ownedCopy.InternalRelativePath,
                Map(inspection.Error!),
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(inspection.Inspection!.FileSha256, ownedCopy.Sha256, StringComparison.OrdinalIgnoreCase)
            || inspection.Inspection.SizeBytes != ownedCopy.Length)
        {
            return await FailAndCleanAsync(
                ownedCopy.InternalRelativePath,
                Error(
                    "TPL_SOURCE_CHANGED_DURING_COPY",
                    retryable: true,
                    "Wait until the PSD is stable, then retry the import."),
                cancellationToken).ConfigureAwait(false);
        }

        var resource = new ImportedTemplateResource(
            request.ResourceId,
            request.GroupId,
            request.DraftId,
            ownedCopy.InternalRelativePath,
            ownedCopy.Length,
            ownedCopy.Sha256.ToLowerInvariant(),
            inspection.Inspection.StructureSignature.ToLowerInvariant(),
            inspection.Inspection,
            timeProvider.GetUtcNow());
        try
        {
            var saved = await resources.SaveImportedAsync(request.CommandId, resource, cancellationToken)
                .ConfigureAwait(false);
            return Success(TemplatePageImportStatus.Imported, saved, idempotent: false, duplicates);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return await FailAndCleanAsync(
                ownedCopy.InternalRelativePath,
                Error(
                    "TPL_REVISION_COMMIT_FAILED",
                    retryable: true,
                    "The previous template revision remains active. Retry the import."),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TemplatePageImportResult> CancelAndCleanAsync(
        string internalRelativePath,
        IReadOnlyList<ImportedTemplateResource> duplicates,
        CancellationToken cancellationToken)
    {
        var cleaned = await sourceFiles.DeleteOwnedCopyAsync(internalRelativePath, cancellationToken)
            .ConfigureAwait(false);
        return cleaned
            ? new TemplatePageImportResult(
                    TemplatePageImportStatus.Cancelled,
                    null,
                    duplicates,
                    null,
                    Idempotent: false)
            : Failure(Error(
                "TPL_IMPORT_CLEANUP_REQUIRED",
                retryable: true,
                "Restart the application to recover the cancelled staging copy."),
                duplicates);
    }

    private async Task<TemplatePageImportResult> FailAndCleanAsync(
        string internalRelativePath,
        TemplateImportError error,
        CancellationToken cancellationToken,
        IReadOnlyList<ImportedTemplateResource>? duplicates = null)
    {
        var cleaned = await sourceFiles.DeleteOwnedCopyAsync(internalRelativePath, cancellationToken)
            .ConfigureAwait(false);
        return cleaned
            ? Failure(error, duplicates)
            : Failure(Error(
                "TPL_IMPORT_CLEANUP_REQUIRED",
                retryable: true,
                "Restart the application to recover the incomplete internal staging copy."),
                duplicates);
    }

    private static void Validate(TemplatePageImportRequest request)
    {
        TemplateValueRules.Identifier(request.CommandId, "TPL_IMPORT_COMMAND_ID_INVALID");
        TemplateValueRules.Identifier(request.GroupId, "TPL_GROUP_ID_INVALID");
        TemplateValueRules.Identifier(request.DraftId, "TPL_DRAFT_ID_INVALID");
        TemplateValueRules.Identifier(request.ResourceId, "TPL_TEMPLATE_RESOURCE_ID_INVALID");
        if (string.IsNullOrWhiteSpace(request.SourcePsdPath))
        {
            throw new TemplateRuleViolationException("TPL_SOURCE_UNREADABLE");
        }

        if (request.SelectedDuplicateResourceId is not null)
        {
            TemplateValueRules.Identifier(
                request.SelectedDuplicateResourceId,
                "TPL_TEMPLATE_RESOURCE_ID_INVALID");
        }
    }

    private static TemplatePageImportResult Success(
        TemplatePageImportStatus status,
        ImportedTemplateResource resource,
        bool idempotent,
        IReadOnlyList<ImportedTemplateResource>? duplicates = null) => new(
            status,
            resource,
            duplicates ?? [],
            null,
            idempotent);

    private static TemplatePageImportResult Failure(
        TemplateImportError error,
        IReadOnlyList<ImportedTemplateResource>? duplicates = null) => new(
            TemplatePageImportStatus.Failed,
            null,
            duplicates ?? [],
            error,
            Idempotent: false);

    private static TemplateImportError Map(PsdInspectionError error) => new(
        error.Code,
        error.Retryable,
        "template-page",
        error.UserAction);

    private static TemplateImportError Error(string code, bool retryable, string userAction) => new(
        code,
        retryable,
        "template-page",
        userAction);
}
