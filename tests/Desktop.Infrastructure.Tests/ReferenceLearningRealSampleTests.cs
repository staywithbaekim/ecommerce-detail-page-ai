using Desktop.Application.Psd;
using Desktop.Application.Templates;
using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Psd;
using Desktop.Infrastructure.Templates;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Desktop.Infrastructure.Tests;

public sealed class ReferenceLearningRealSampleTests(ITestOutputHelper output)
{
    [RealSampleFact]
    public async Task RealSixPageSampleUsesStructuralCandidateCountInsteadOfRawVisibleSmartObjectCount()
    {
        // Discovery skips this fact unless all opt-in paths are present.
        var sampleRoot = Environment.GetEnvironmentVariable("TPL_LEARN_REAL_SAMPLE_ROOT")!;
        var workerScript = Environment.GetEnvironmentVariable("TPL_LEARN_WORKER_SCRIPT")!;
        var pythonDeps = Environment.GetEnvironmentVariable("TPL_LEARN_PYDEPS")!;

        var templateRoot = Path.Combine(sampleRoot, "templates", "奶奶新模板");
        var photoRoot = Path.Combine(sampleRoot, "source", "新建文件夹");

        var python = Environment.GetEnvironmentVariable("TPL_LEARN_PYTHON") ?? "python";
        var scriptLiteral = workerScript.Replace("'", "''", StringComparison.Ordinal);
        var depsLiteral = pythonDeps.Replace("'", "''", StringComparison.Ordinal);
        var command =
            $"import sys; sys.path.insert(0, r'{depsLiteral}'); exec(compile(open(r'{scriptLiteral}', encoding='utf-8').read(), r'{scriptLiteral}', 'exec'))";
        var packagedWorker = Environment.GetEnvironmentVariable("TPL_LEARN_WORKER_EXE");
        var inspection = new RecordingInspectionPort(new PsdInspectionWorkerClient(
            new LocalAppPathPolicy(templateRoot),
            string.IsNullOrWhiteSpace(packagedWorker)
                ? new PsdInspectionWorkerOptions(
                    python,
                    TimeSpan.FromMinutes(2),
                    8 * 1024 * 1024,
                    ["-c", command])
                : new PsdInspectionWorkerOptions(
                    packagedWorker,
                    TimeSpan.FromMinutes(2),
                    8 * 1024 * 1024)));
        var service = new ReferenceLearningService(
            inspection,
            new ReferencePhotoFingerprintPort(),
            new LocalAppPathPolicy(templateRoot));

        var expectedCandidates = new Dictionary<string, IReadOnlyList<(int LayerPath, string SourceFile, bool IsProduct)>>(StringComparer.Ordinal)
        {
            ["G-详情-01.psd"] = [(4, "036A7092__036A7320_9Y2A3598.png", true)],
            ["G-详情-03.psd"] = [(3, "036A7091__9Y2A3650.png", true)],
            ["G-详情-04-面料.psd"] = [(5, "I36A8881.JPG", true)],
            ["G-详情-05-细节.psd"] =
            [
                (4, "a732592fe1beb1c75e426cb227bc9ddd.png", true),
                (9, "I36A8876.JPG", true),
                (14, "I36A8887.JPG", true),
                (19, "036A7092__036A7320_9Y2A3598.png", true)
            ],
            ["H-模特图-01.psd"] =
            [
                (5, "036A7091__9Y2A3650.png", true),
                (11, "036A7092__036A7320_9Y2A1616.png", true)
            ],
            ["H-模特图-02.psd"] =
            [
                (5, "036A7166__036A7320_9Y2A3558.png", true),
                (11, "036A7166__036A7320_9Y2A3941.png", true)
            ]
        };
        var documents = new List<ReferenceLearningDocument>();
        var total = Stopwatch.StartNew();
        foreach (var (file, index) in expectedCandidates.Keys.Select((file, index) => (file, index)))
        {
            var page = Stopwatch.StartNew();
            var result = await service.AnalyzeAsync(new ReferenceLearningRequest(
                $"learn_real_{index}",
                $"page_real_{index}",
                file,
                photoRoot,
                PageName: file));
            page.Stop();
            Assert.True(
                result.IsSuccess,
                result.Error is null
                    ? "PSD 学习失败。"
                    : $"{result.Error.Code}: {result.Error.Message} / {result.Error.UserAction}");
            var document = result.Document!;
            var workerDocument = Assert.IsType<PsdInspectionDocument>(inspection.LastInspection);
            output.WriteLine(
                $"{file}|candidates={document.Candidates.Count}|wall_ms={page.Elapsed.TotalMilliseconds:0.###}|worker_ms={workerDocument.Timing.TotalMilliseconds:0.###}|peak_bytes={workerDocument.ProcessPeakWorkingSetBytes}");
            documents.Add(document);
            var expected = expectedCandidates[file];
            Assert.Equal(expected.Count, document.Candidates.Count);
            foreach (var (candidate, expectedCandidate) in document.Candidates.Zip(expected))
            {
                Assert.Equal(expectedCandidate.LayerPath, Assert.Single(candidate.LayerPath));
                Assert.Equal(expectedCandidate.IsProduct, candidate.IsProduct);
                Assert.Equal(ReferenceMatchState.Exact, candidate.MatchState);
                Assert.Equal(
                    expectedCandidate.SourceFile,
                    candidate.ReferencePhotoRelativePath?.Replace('\\', '/').Split('/')[^1]);
                Assert.NotNull(candidate.ReferenceAssetId);
                Assert.Equal(ReferenceReviewDecision.Pending, candidate.ReviewDecision);
                Assert.False(
                    string.IsNullOrWhiteSpace(candidate.SemanticLabel),
                    $"真实 PSD {file} 的图片框没有生成未来选图规则。");
                Assert.True(
                    candidate.SupersededLayerEvidence.Count > 0,
                    $"{file}:{string.Join('/', candidate.LayerPath)} did not retain its replacement history.");
                Assert.True(candidate.EffectivePixelEvidence.Available);
                Assert.True(candidate.EffectivePixelEvidence.RenderedPixelCount > 0);
                Assert.True(candidate.EffectivePixelEvidence.CompositeContributionPixelCount > 0);
                Assert.NotNull(candidate.EffectivePixelEvidence.CompositeContributionBounds);
                output.WriteLine(
                    $"  path={string.Join('/', candidate.LayerPath)}|rendered={candidate.EffectivePixelEvidence.RenderedPixelCount}|contribution={candidate.EffectivePixelEvidence.CompositeContributionPixelCount}");
            }
        }
        total.Stop();
        output.WriteLine($"total_wall_ms={total.Elapsed.TotalMilliseconds:0.###}");

        Assert.Equal(11, documents.Sum(document => document.Candidates.Count));
        Assert.Equal(11, documents.Sum(document => document.Candidates.Count(candidate => candidate.IsProduct)));
        Assert.Equal(0, documents.Sum(document => document.Candidates.Count(candidate => !candidate.IsProduct)));
        Assert.Equal(25, documents.Sum(document => document.Candidates.Sum(candidate => candidate.SupersededLayerEvidence.Count)));
        var detailPage = Assert.Single(documents, document => document.Candidates.Count == 4);
        Assert.Equal([4, 9, 14, 19], detailPage.Candidates.Select(candidate => Assert.Single(candidate.LayerPath)));
        var careLabel = Assert.Single(detailPage.Candidates, candidate => candidate.LayerPath.SequenceEqual([4]));
        Assert.Equal(
            "a732592fe1beb1c75e426cb227bc9ddd.png",
            careLabel.ReferencePhotoRelativePath?.Replace('\\', '/').Split('/')[^1]);
    }

    private sealed class RealSampleFactAttribute : FactAttribute
    {
        public RealSampleFactAttribute()
        {
            var sampleRoot = Environment.GetEnvironmentVariable("TPL_LEARN_REAL_SAMPLE_ROOT");
            var workerScript = Environment.GetEnvironmentVariable("TPL_LEARN_WORKER_SCRIPT");
            var pythonDeps = Environment.GetEnvironmentVariable("TPL_LEARN_PYDEPS");
            var packagedWorker = Environment.GetEnvironmentVariable("TPL_LEARN_WORKER_EXE");
            var workerReady = !string.IsNullOrWhiteSpace(packagedWorker)
                ? File.Exists(packagedWorker)
                : !string.IsNullOrWhiteSpace(workerScript)
                    && !string.IsNullOrWhiteSpace(pythonDeps)
                    && File.Exists(workerScript)
                    && Directory.Exists(pythonDeps);
            var sampleReady = !string.IsNullOrWhiteSpace(sampleRoot)
                && Directory.Exists(Path.Combine(sampleRoot!, "templates", "奶奶新模板"))
                && Directory.Exists(Path.Combine(sampleRoot!, "source", "新建文件夹"))
                && workerReady;
            Skip = sampleReady
                ? null
                : "设置 TPL_LEARN_REAL_SAMPLE_ROOT/WORKER_SCRIPT/PYDEPS 后运行真实 PSD 学习回归。";
        }
    }

    private sealed class RecordingInspectionPort(IPsdInspectionPort inner) : IPsdInspectionPort
    {
        public PsdInspectionDocument? LastInspection { get; private set; }

        public async Task<PsdInspectionResult> InspectAsync(
            PsdInspectionRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.InspectAsync(request, cancellationToken);
            LastInspection = result.Inspection;
            return result;
        }
    }
}
