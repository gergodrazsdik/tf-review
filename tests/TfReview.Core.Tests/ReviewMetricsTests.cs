using System.Diagnostics.Metrics;

namespace TfReview.Core.Tests;

[NotInParallel]
public class ReviewMetricsTests
{
    private const string Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public async Task A_model_call_reports_its_tokens_latency_and_cost_by_agent()
    {
        using var metrics = new Collector();

        ReviewMetrics.RecordCall("security", 4, "gpt-5.4-mini", "stop", 1_000_000, 0, 0, TimeSpan.FromMilliseconds(250));

        await Assert.That(metrics.Total("tfreview.llm.input_tokens")).IsEqualTo(1_000_000d);
        await Assert.That(metrics.Total("tfreview.llm.cost")).IsEqualTo(0.825d);
        await Assert.That(metrics.Total("tfreview.llm.latency")).IsEqualTo(250d);
        await Assert.That(metrics.Tag("tfreview.llm.cost", "agent")).IsEqualTo("security");
        await Assert.That(metrics.Tag("tfreview.llm.cost", "prompt_version")).IsEqualTo("4");
    }

    [Test]
    public async Task Cached_tokens_are_reported_and_cost_less()
    {
        using var metrics = new Collector();

        ReviewMetrics.RecordCall("security", 4, "gpt-5.4-mini", "stop", 1_000_000, 1_000_000, 0, TimeSpan.Zero);

        await Assert.That(metrics.Total("tfreview.llm.cached_input_tokens")).IsEqualTo(1_000_000d);
        await Assert.That(metrics.Total("tfreview.llm.cost")).IsEqualTo(0.0825d);
    }

    [Test]
    public async Task A_model_with_no_known_price_reports_tokens_and_no_cost()
    {
        using var metrics = new Collector();

        ReviewMetrics.RecordCall("security", 4, "another-model", "stop", 10, 0, 0, TimeSpan.Zero);

        await Assert.That(metrics.Total("tfreview.llm.input_tokens")).IsEqualTo(10d);
        await Assert.That(metrics.Has("tfreview.llm.cost")).IsFalse();
    }

    [Test]
    public async Task A_result_reports_its_verdict_findings_drops_and_gaps()
    {
        using var metrics = new Collector();
        var result = new ReviewResult(
            1,
            Commit,
            Verdict.Findings,
            [
                new Finding("azurerm_a.a", "ADR-001", Severity.Low, Reporter.Conventions, false, "CANARY title", [new DocEvidence("ADR-001")]),
                new Finding("azurerm_a.b", "ADR-001", Severity.Low, Reporter.Conventions, false, "CANARY title", [new DocEvidence("ADR-001")]),
            ],
            [new NotReviewed(ReviewScope.Cost, NotReviewedReason.ScannerFailed)],
            new Dictionary<string, int> { ["attribute_unchanged"] = 3 });

        ReviewMetrics.RecordResult(result, "owner/repo");

        await Assert.That(metrics.Total("tfreview.reviews")).IsEqualTo(1d);
        await Assert.That(metrics.Tag("tfreview.reviews", "verdict")).IsEqualTo("Findings");
        await Assert.That(metrics.Tag("tfreview.reviews", "repository")).IsEqualTo("owner/repo");
        await Assert.That(metrics.Total("tfreview.findings")).IsEqualTo(2d);
        await Assert.That(metrics.Tag("tfreview.findings", "agent")).IsEqualTo("Conventions");
        await Assert.That(metrics.Total("tfreview.evidence_drops")).IsEqualTo(3d);
        await Assert.That(metrics.Tag("tfreview.evidence_drops", "reason")).IsEqualTo("attribute_unchanged");
        await Assert.That(metrics.Tag("tfreview.not_reviewed", "scope")).IsEqualTo("Cost");

        // These reach App Insights, which isn't where a plan's or a model's words belong.
        await Assert.That(metrics.AllTags()).DoesNotContain("CANARY");
    }

    private sealed class Collector : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<(string Name, double Value, Dictionary<string, string?> Tags)> measurements = [];

        public Collector()
        {
            this.listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ReviewMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            this.listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => this.Add(instrument, value, tags));
            this.listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => this.Add(instrument, value, tags));
            this.listener.Start();
        }

        public bool Has(string name) => this.measurements.Any(measurement => measurement.Name == name);

        public double Total(string name) => this.measurements.Where(measurement => measurement.Name == name).Sum(measurement => measurement.Value);

        public string? Tag(string name, string tag) => this.measurements.First(measurement => measurement.Name == name).Tags[tag];

        public string AllTags() => string.Join(' ', this.measurements.SelectMany(measurement => measurement.Tags.Values));

        public void Dispose() => this.listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copied = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value?.ToString();
            }

            this.measurements.Add((instrument.Name, value, copied));
        }
    }
}
