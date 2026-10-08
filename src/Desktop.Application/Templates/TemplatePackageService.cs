using Desktop.Application.Files;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed class TemplatePackageService(
    ITemplateRepository templates,
    ITemplateResourceRepository resources,
    ITemplatePackagePort packages,
    IFileStore files,
    IPathPolicy paths,
    ITemplateWorkspaceService workspace,
    TimeProvider timeProvider,
    ITemplateOwnedStorage? ownedStorage = null) : ITemplatePackageService
{
    public async Task ExportCurrentAsync(
        string groupId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var group = await templates.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_GROUP_NOT_FOUND");
        var revision = group.CurrentRevisionId is null
            ? null
            : await templates.GetRevisionAsync(group.CurrentRevisionId, cancellationToken).ConfigureAwait(false);
        if (revision is null || revision.State != TemplateRevisionState.Committed || revision.RuleDigest is null)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        }

        var entries = new List<TemplatePackageEntry>(revision.Pages.Length);
        var sourceFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var pageDocuments = new List<TemplatePackagePage>(revision.Pages.Length);
        foreach (var page in revision.Pages.OrderBy(page => page.Order))
        {
            var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");
            var actualHash = await files.ComputeSha256Async(resource.InternalRelativePath, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new IOException("TPL_PACKAGE_ENTRY_MISSING");
            if (!string.Equals(actualHash, resource.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("TPL_PACKAGE_HASH_MISMATCH");
            }
            if (!sourceFiles.ContainsKey(resource.ResourceId))
            {
                entries.Add(new TemplatePackageEntry(
                    resource.ResourceId,
                    $"resources/{resource.ResourceId}.psd",
                    resource.Length,
                    resource.Sha256));
                sourceFiles[resource.ResourceId] = paths.ResolveOwnedPath(resource.InternalRelativePath);
            }
            pageDocuments.Add(ToPackagePage(
                page,
                resource,
                revision.FrameBindings.Where(binding => string.Equals(
                    binding.PageId,
                    page.PageId,
                    StringComparison.Ordinal))));
        }

        await packages.ExportAsync(
            new TemplatePackageManifest(
                TemplatePackageProtocol.CurrentVersion,
                group.Name,
                revision.RevisionNo,
                revision.RevisionId,
                revision.RuleDigest,
                entries,
                pageDocuments,
                revision.RuleWorkspace),
            sourceFiles,
            destinationPath,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateGroupDetails> ImportAsNewGroupAsync(
        string packagePath,
        string? groupName,
        CancellationToken cancellationToken)
    {
        var package = await packages.ValidateAsync(packagePath, cancellationToken).ConfigureAwait(false);
        var manifest = package.Manifest ?? throw new InvalidDataException("TPL_PACKAGE_INVALID");
        if (manifest.Pages is null || manifest.Pages.Count == 0
            || manifest.Pages.Any(page => page.Inspection is null))
        {
            throw new InvalidDataException("TPL_PACKAGE_INVALID");
        }

        var name = string.IsNullOrWhiteSpace(groupName) ? manifest.GroupName : groupName.Trim();
        var session = await workspace.BeginCreateAsync(name, cancellationToken).ConfigureAwait(false);
        try
        {
            var pageIdMap = manifest.Pages.ToDictionary(
                page => page.PageId,
                _ => NewId("page"),
                StringComparer.Ordinal);
            var resourceIdMap = manifest.Entries.ToDictionary(
                entry => entry.LogicalId,
                _ => NewId("resource"),
                StringComparer.Ordinal);
            var frameIdMap = manifest.Pages.SelectMany(page => page.Bindings ?? []).ToDictionary(
                frame => frame.FrameId, _ => NewId("frame"), StringComparer.Ordinal);
            var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");

            foreach (var packageEntry in manifest.Entries)
            {
                var resourceId = resourceIdMap[packageEntry.LogicalId];
                var relativePath =
                    $"templates/groups/{session.GroupId}/drafts/{session.DraftRevisionId}/staging/{resourceId}/template.psd";
                var absolutePath = paths.ResolveOwnedPath(relativePath);
                await packages.CopyValidatedEntryAsync(
                    packagePath,
                    packageEntry,
                    absolutePath,
                    cancellationToken).ConfigureAwait(false);
                var packagePage = manifest.Pages.First(page => string.Equals(
                    page.ResourceLogicalId,
                    packageEntry.LogicalId,
                    StringComparison.Ordinal));
                var resource = new ImportedTemplateResource(
                    resourceId,
                    session.GroupId,
                    session.DraftRevisionId,
                    relativePath,
                    packageEntry.Length,
                    packageEntry.Sha256.ToLowerInvariant(),
                    packagePage.Inspection!.StructureSignature.ToLowerInvariant(),
                    manifest.Protocol == TemplatePackageProtocol.Version
                        ? packagePage.Inspection with
                        {
                            // The frozen v1 package uses one-based inspection paths.
                            // Translate at import into the worker's zero-based representation.
                            Layers = packagePage.Inspection.Layers.Select(layer => layer with
                            {
                                Path = layer.Path.Select(segment => segment - 1).ToArray(),
                                ClippingBase = layer.ClippingBase is { } basis ? basis with
                                { Path = basis.Path.Select(segment => segment - 1).ToArray() } : null
                            }).ToArray()
                        } : packagePage.Inspection,
                    timeProvider.GetUtcNow());
                await resources.SaveImportedAsync(NewId("import"), resource, cancellationToken).ConfigureAwait(false);
            }

            foreach (var packagePage in manifest.Pages.OrderBy(page => page.Order))
            {
                var resourceId = resourceIdMap[packagePage.ResourceLogicalId];
                revision = revision.AddPage(TemplatePage.Create(
                    pageIdMap[packagePage.PageId],
                    session.DraftRevisionId,
                    packagePage.Order,
                    packagePage.Name,
                    ToDomain(packagePage.ContentType),
                    ToDomain(packagePage.GenerationMode),
                    packagePage.VariantCapacity,
                    packagePage.Enabled,
                    resourceId, notes: packagePage.Notes, sizeTableCalibration: packagePage.SizeTableCalibration));
            }

            foreach (var packagePage in manifest.Pages)
            {
                foreach (var binding in packagePage.Bindings ?? [])
                {
                    revision = revision.AddFrameBinding(ToDomain(binding, pageIdMap[packagePage.PageId], frameIdMap[binding.FrameId]));
                }
            }

            if (manifest.RuleWorkspace is { } rules)
                revision = revision.WithRuleWorkspace(TemplateRuleCodec.Remap(rules, pageIdMap, frameIdMap));
            await templates.SaveDraftRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
            if (revision.RuleWorkspace is not null)
            {
                var editor = workspace as ITemplateRuleEditingService
                    ?? throw new TemplateRuleViolationException("TPL_RULE_STORE_UNAVAILABLE");
                var view = await editor.GetRulesAsync(session, cancellationToken).ConfigureAwait(false);
                return await editor.ConfirmRulesAsync(session, new(view.Version, view.RuleDigest), cancellationToken).ConfigureAwait(false);
            }
            return await workspace.CommitDraftAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await templates.DeleteGroupAsync(session.GroupId, CancellationToken.None).ConfigureAwait(false);
                if (ownedStorage is not null)
                {
                    await ownedStorage.DeleteGroupFilesAsync(session.GroupId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Preserve the original import error; startup recovery can remove the unreferenced draft.
            }
            throw;
        }
    }

    public async Task<TemplateGroupDetails> SaveAsNewGroupAsync(
        string groupId,
        string newGroupName,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.RequiredText(newGroupName, 256, "TPL_GROUP_NAME_INVALID");
        var operationId = NewId("package");
        var relativePath = $"template-package-staging/{operationId}/clone.tplpkg";
        await files.WriteAtomicallyAsync(relativePath, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        var absolutePath = paths.ResolveOwnedPath(relativePath);
        try
        {
            await ExportCurrentAsync(groupId, absolutePath, cancellationToken).ConfigureAwait(false);
            return await ImportAsNewGroupAsync(absolutePath, newGroupName, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await files.DeleteAsync(relativePath, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static TemplatePackagePage ToPackagePage(
        TemplatePage page,
        ImportedTemplateResource resource,
        IEnumerable<FrameBinding> bindings) => new(
            page.PageId,
            page.Order,
            page.Name,
            ToOption(page.ContentType),
            ToOption(page.GenerationMode),
            page.VariantCapacity,
            page.Enabled,
            resource.ResourceId,
            resource.Inspection with
            {
                Preview = null,
                // Worker inspections use bare SHA-256; the package contract uses a digest.
                // Keep the stored evidence unchanged and let the package validator reject malformed values.
                StructureSignature = resource.Inspection.StructureSignature is { Length: 64 } signature
                    && signature.All(Uri.IsHexDigit) ? "sha256:" + signature.ToLowerInvariant()
                    : resource.Inspection.StructureSignature,
                // Output validation evidence is not a portable template rule or part of template-package/v1.
                Layers = resource.Inspection.Layers.Select(layer => layer with { ContentEvidence = null }).ToArray()
            },
            bindings.Select(ToPackageBinding).ToArray(), page.SizeTableCalibration, page.Notes);

    internal static TemplatePackageFrameBinding ToPackageBinding(FrameBinding binding) => new(
        binding.FrameId,
        binding.PageId,
        binding.LayerPath.Segments,
        binding.ExpectedLayer.LayerId,
        binding.ExpectedLayer.NameBefore,
        binding.ExpectedLayer.Visible,
        binding.ExpectedLayer.Clipping,
        binding.ExpectedLayer.UserMask,
        binding.ExpectedLayer.VectorMask,
        binding.ExpectedLayer.Opacity,
        binding.ExpectedLayer.BlendMode,
        binding.ExpectedLayer.StructureSignature.Value,
        binding.SelectionRequirement.Description,
        binding.SelectionRequirement.Criteria.Select(item =>
            new TemplatePackageSelectionCriterion(item.Key, item.Value)).ToArray(),
        binding.LayoutIntent.Intent,
        binding.LayoutIntent.Scale,
        binding.LayoutIntent.OffsetX,
        binding.LayoutIntent.OffsetY,
        binding.LayoutIntent.SafeBounds.Left,
        binding.LayoutIntent.SafeBounds.Top,
        binding.LayoutIntent.SafeBounds.Right,
        binding.LayoutIntent.SafeBounds.Bottom,
        binding.SupersededLayers.IsEmpty ? null : binding.SupersededLayers.Select(layer =>
            new TemplatePackageSupersededLayer(layer.LayerPath.Segments, "smartObject",
                layer.ExpectedVisible, layer.ExpectedClipping, layer.ExpectedLayerId)).ToArray());

    internal static FrameBinding ToDomain(TemplatePackageFrameBinding binding, string pageId, string frameId) =>
        FrameBinding.Create(
            frameId,
            pageId,
            LayerPath.Create(binding.LayerPath),
            ExpectedLayerEvidence.Create(
                binding.LayerId,
                binding.LayerName,
                binding.Visible,
                binding.Clipping,
                binding.UserMask,
                binding.VectorMask,
                binding.Opacity,
                binding.BlendMode,
                LayerStructureSignature.Create(binding.StructureSignature)),
            (binding.SupersededLayers ?? []).Select(layer => SupersededLayerEvidence.Create(
                LayerPath.Create(layer.LayerPath), layer.ExpectedClipping, layer.ExpectedLayerId)),
            SelectionRequirement.Create(
                binding.SelectionDescription,
                binding.SelectionCriteria.Select(item => SelectionCriterion.Create(item.Key, item.Value))),
            LayoutIntent.Create(
                binding.Intent,
                binding.Scale,
                binding.OffsetX,
                binding.OffsetY,
                TemplateRect.Create(
                    binding.SafeLeft,
                    binding.SafeTop,
                    binding.SafeRight,
                    binding.SafeBottom)));

    private static TemplatePageContentTypeOption ToOption(TemplatePageContentType value) => value switch
    {
        TemplatePageContentType.DynamicSizeTable => TemplatePageContentTypeOption.DynamicSizeTable,
        _ => TemplatePageContentTypeOption.NormalComposition
    };

    private static TemplateGenerationModeOption ToOption(TemplateGenerationMode value) => value switch
    {
        TemplateGenerationMode.OncePerVariant => TemplateGenerationModeOption.OncePerVariant,
        TemplateGenerationMode.VariantCapacityCombination => TemplateGenerationModeOption.VariantCapacityCombination,
        _ => TemplateGenerationModeOption.OncePerProduct
    };

    private static TemplatePageContentType ToDomain(TemplatePageContentTypeOption value) => value switch
    {
        TemplatePageContentTypeOption.DynamicSizeTable => TemplatePageContentType.DynamicSizeTable,
        _ => TemplatePageContentType.NormalComposition
    };

    private static TemplateGenerationMode ToDomain(TemplateGenerationModeOption value) => value switch
    {
        TemplateGenerationModeOption.OncePerVariant => TemplateGenerationMode.OncePerVariant,
        TemplateGenerationModeOption.VariantCapacityCombination => TemplateGenerationMode.VariantCapacityCombination,
        _ => TemplateGenerationMode.OncePerProduct
    };

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
