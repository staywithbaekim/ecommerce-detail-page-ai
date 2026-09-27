using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Desktop.Application.Templates;

namespace Desktop.App;

public sealed class ReferenceLearningFrameViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public ReferenceLearningFrameViewModel(ReferenceFrameCandidate candidate)
    {
        Candidate = candidate;
    }

    public ReferenceFrameCandidate Candidate { get; }

    public string FrameId => Candidate.FrameId;

    public string LayerName => string.IsNullOrWhiteSpace(Candidate.LayerName)
        ? $"图片框 {Candidate.LayerId}"
        : Candidate.LayerName;

    public ReferenceReviewDecision Decision => Candidate.ReviewDecision;

    public string? ReferenceAssetId => Candidate.ReferenceAssetId;

    public bool IsProduct => Candidate.IsProduct;

    public bool IsAutoConfirmable => Candidate.IsAutoConfirmable;

    public string StructuralSummary => Candidate.IsProduct
        ? "可替换素材框 · 已验证源照片与最终显示"
        : $"固定内容 · {Candidate.StructuralReasonText}";

    public string SemanticLabel => Candidate.SemanticLabel;

    public double Scale => Candidate.LayoutScale;

    public double OffsetX => Candidate.LayoutOffsetX;

    public double OffsetY => Candidate.LayoutOffsetY;

    public double Left => Candidate.LayoutSafeLeft;

    public double Top => Candidate.LayoutSafeTop;

    public double Width => Candidate.LayoutSafeRight - Candidate.LayoutSafeLeft;

    public double Height => Candidate.LayoutSafeBottom - Candidate.LayoutSafeTop;

    public string MatchText => !Candidate.IsProduct
        ? Candidate.StructuralReasonText
        : Candidate.MatchState switch
        {
            ReferenceMatchState.Exact => "嵌入内容与源照片 SHA-256 精确匹配",
            ReferenceMatchState.ManuallyConfirmed => "已由用户明确选择源照片",
            ReferenceMatchState.Ambiguous => "多个源照片内容相同，需要人工选择",
            ReferenceMatchState.Modified => "文件名相似但内容不同，不能据此匹配",
            ReferenceMatchState.ExternalLink => "外链智能对象不可读取",
            ReferenceMatchState.Unsupported => "智能对象结构不支持自动学习",
            _ => "未找到内容完全一致的源照片"
        };

    public string ConfidenceText => $"{Math.Round(Candidate.Confidence * 100):0}%";

    public string RegistrationLabel => !Candidate.IsProduct
        ? Candidate.ReviewDecision == ReferenceReviewDecision.Rejected
            ? "保留模板内容"
            : "未复核"
        : Candidate.ReviewDecision switch
        {
            ReferenceReviewDecision.Accepted => "已确认规则",
            ReferenceReviewDecision.Rejected => "保留模板内容",
            ReferenceReviewDecision.ManualCalibration => "需人工校准",
            _ => "未复核"
        };

    public string ReviewStatusText => RegistrationLabel;

    public bool IsReviewed => Candidate.ReviewDecision != ReferenceReviewDecision.Pending;

    public bool IsPending => Candidate.ReviewDecision == ReferenceReviewDecision.Pending;

    public bool IsNeedsManualCalibration =>
        Candidate.ReviewDecision == ReferenceReviewDecision.ManualCalibration;

    public string StatusTone => !Candidate.IsProduct
        ? "Fixed"
        : Candidate.ReviewDecision switch
        {
            ReferenceReviewDecision.Accepted => "Completed",
            ReferenceReviewDecision.ManualCalibration => "Blocked",
            ReferenceReviewDecision.Pending => "Pending",
            _ => "Fixed"
        };

    public bool IsSelected => _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetSelected(bool selected)
    {
        if (_isSelected == selected)
        {
            return;
        }

        _isSelected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }
}

public sealed record ReferenceLearningPhotoViewModel(
    string AssetId,
    string DisplayName,
    string Meta,
    string? SourcePath,
    ImageSource? PreviewSource)
{
    public ReferenceLearningPhotoViewModel(ReferencePhotoFingerprint photo, string? sourcePath)
        : this(
            photo.AssetId,
            photo.FileName,
            $"{photo.Width} × {photo.Height} · {photo.MimeType[6..].ToUpperInvariant()}",
            sourcePath,
            LoadPreview(sourcePath))
    {
    }


    private static BitmapImage? LoadPreview(string? sourcePath)
    {
        return string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : BoundedPreviewImageCache.Get(sourcePath, BoundedPreviewImageCache.DefaultMaxDimension);
    }
}
