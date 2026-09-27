using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Desktop.Application.Templates;
using Microsoft.Win32;

namespace Desktop.App;

public sealed record TemplateLibraryCardViewModel(
    string GroupId,
    string Name,
    string Meta,
    string UpdatedText,
    string Status,
    bool HasCommittedRevision = false)
{
    public TemplateLibraryCardViewModel(TemplateLibraryItem item)
        : this(
            item.GroupId,
            item.Name,
            $"{item.PageCount} 个页面 · {item.Status}",
            item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            item.Status,
            item.CurrentRevisionId is not null)
    {
    }
}

public sealed record TemplatePageDetailsRowViewModel(
    string PageId,
    string OrderText,
    string Name,
    string Meta,
    string Status,
    string Action)
{
    public TemplatePageDetailsRowViewModel(TemplatePageProjection page)
        : this(
            page.PageId,
            (page.Order + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
            page.Name,
            $"{TemplatePageRowViewModel.ContentTypeLabel(page.ContentType)} · {TemplatePageRowViewModel.GenerationModeLabel(page.GenerationMode)}",
            "已校准",
            "编辑")
    {
    }
}

public sealed record ImportedPsdRowViewModel(
    string PageId,
    string ResourceId,
    string FileName,
    string InspectionSummary,
    string Status)
{
    public ImportedPsdRowViewModel(TemplateDraftPage page)
        : this(
            page.Page.PageId,
            page.Resource.ResourceId,
            $"{page.Page.Name}.psd",
            $"{page.Resource.Inspection.Canvas.Width} × {page.Resource.Inspection.Canvas.Height} · "
            + $"{page.Resource.Inspection.Canvas.Depth} 位 {page.Resource.Inspection.Canvas.ColorMode}",
            "可用")
    {
    }
}

public sealed class TemplatePageRowViewModel : INotifyPropertyChanged
{
    private readonly Action<TemplatePageRowViewModel> _changed;
    private readonly Action<TemplatePageRowViewModel>? _presentationChanged;
    private string _name;
    private TemplatePageContentTypeOption _contentType;
    private TemplateGenerationModeOption _generationMode;
    private int? _variantCapacity;
    private bool _enabled;
    private int _displayOrder;
    private string _notes;
    private double _canvasX;
    private double _canvasY;

    public TemplatePageRowViewModel(
        TemplateDraftPage page,
        Action<TemplatePageRowViewModel> changed,
        Action<TemplatePageRowViewModel>? presentationChanged = null)
    {
        _changed = changed;
        _presentationChanged = presentationChanged;
        PageId = page.Page.PageId;
        ResourceId = page.Resource.ResourceId;
        _displayOrder = page.Page.Order + 1;
        _name = page.Page.Name;
        _contentType = page.Page.ContentType;
        _generationMode = page.Page.GenerationMode;
        _variantCapacity = page.Page.VariantCapacity;
        _enabled = page.Page.Enabled;
        _notes = page.Page.Notes;
        _canvasX = page.Page.CanvasX == 0 && page.Page.CanvasY == 0 && page.Page.Order > 0
            ? (page.Page.Order % 3) * 360
            : page.Page.CanvasX;
        _canvasY = page.Page.CanvasX == 0 && page.Page.CanvasY == 0 && page.Page.Order > 0
            ? (page.Page.Order / 3) * 440
            : page.Page.CanvasY;
        IsAffected = page.IsAffected;
        CanvasSummary = $"{page.Resource.Inspection.Canvas.Width} × {page.Resource.Inspection.Canvas.Height}";
        PreviewSourceReference = page.PreviewSourceReference;
        InternalPsdRelativePath = page.Resource.InternalRelativePath;
    }

    public static IReadOnlyList<string> ContentTypeOptions { get; } = ["普通套图页", "动态尺码表页"];

    public static IReadOnlyList<string> GenerationModeOptions { get; } =
        ["每款生成一次", "每变体生成一次", "按变体数量组合"];

    public string PageId { get; }

    public string ResourceId { get; }

    public bool IsAffected { get; }

    public bool IsReadOnly => !IsAffected;

    public bool CanRemove => IsAffected;

    public string Status => IsAffected ? "新增" : "已校准";

    public string RuleSummary => IsAffected
        ? $"{ContentTypeLabel(ContentType)} · {GenerationModeLabel(GenerationMode)}"
        : "原规则保持";

    public string CanvasSummary { get; }

    public string? PreviewSourceReference { get; }

    public string InternalPsdRelativePath { get; }

    public string Notes
    {
        get => _notes;
        set
        {
            if (Set(ref _notes, value, invokeChanged: false))
            {
                _presentationChanged?.Invoke(this);
            }
        }
    }

    public double CanvasX
    {
        get => _canvasX;
        set
        {
            if (Set(ref _canvasX, Math.Max(0, value), invokeChanged: false))
            {
                _presentationChanged?.Invoke(this);
            }
        }
    }

    public double CanvasY
    {
        get => _canvasY;
        set
        {
            if (Set(ref _canvasY, Math.Max(0, value), invokeChanged: false))
            {
                _presentationChanged?.Invoke(this);
            }
        }
    }

    public string DisplayOrder => _displayOrder.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string ContentTypeText
    {
        get => ContentTypeLabel(ContentType);
        set => ContentType = value == "动态尺码表页"
            ? TemplatePageContentTypeOption.DynamicSizeTable
            : TemplatePageContentTypeOption.NormalComposition;
    }

    public string GenerationModeText
    {
        get => GenerationModeLabel(GenerationMode);
        set => GenerationMode = value switch
        {
            "每变体生成一次" => TemplateGenerationModeOption.OncePerVariant,
            "按变体数量组合" => TemplateGenerationModeOption.VariantCapacityCombination,
            _ => TemplateGenerationModeOption.OncePerProduct
        };
    }

    public TemplatePageContentTypeOption ContentType
    {
        get => _contentType;
        private set
        {
            if (Set(ref _contentType, value))
            {
                OnPropertyChanged(nameof(ContentTypeText));
            }
        }
    }

    public TemplateGenerationModeOption GenerationMode
    {
        get => _generationMode;
        private set
        {
            if (Set(ref _generationMode, value))
            {
                if (value != TemplateGenerationModeOption.VariantCapacityCombination)
                {
                    _variantCapacity = null;
                    OnPropertyChanged(nameof(VariantCapacity));
                }

                OnPropertyChanged(nameof(GenerationModeText));
                OnPropertyChanged(nameof(IsVariantCapacityVisible));
            }
        }
    }

    public int? VariantCapacity
    {
        get => _variantCapacity;
        set => Set(ref _variantCapacity, value);
    }

    public bool IsVariantCapacityVisible => GenerationMode == TemplateGenerationModeOption.VariantCapacityCombination;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string ContentTypeLabel(TemplatePageContentTypeOption contentType) => contentType switch
    {
        TemplatePageContentTypeOption.DynamicSizeTable => "动态尺码表页",
        _ => "普通套图页"
    };

    public static string GenerationModeLabel(TemplateGenerationModeOption generationMode) => generationMode switch
    {
        TemplateGenerationModeOption.OncePerVariant => "每变体生成一次",
        TemplateGenerationModeOption.VariantCapacityCombination => "按变体数量组合",
        _ => "每款生成一次"
    };

    public void SetDisplayOrder(int displayOrder)
    {
        _displayOrder = displayOrder;
        OnPropertyChanged(nameof(DisplayOrder));
    }

    private bool Set<T>(ref T field, T value, bool invokeChanged = true, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(RuleSummary));
        if (invokeChanged)
        {
            _changed(this);
        }
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
