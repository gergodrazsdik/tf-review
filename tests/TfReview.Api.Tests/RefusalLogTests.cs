namespace TfReview.Api.Tests;

/// <summary>SEC-7: a flood of bad tokens writes a count, not a line each.</summary>
public class RefusalLogTests
{
    [Test]
    public async Task A_flood_writes_one_line_a_period_with_its_count()
    {
        var written = new List<long>();
        var time = new ManualTime();
        var log = new RefusalLog(written.Add, TimeSpan.FromMinutes(5), time);

        for (var index = 0; index < 1000; index++)
        {
            log.Refused();
        }

        time.Now += TimeSpan.FromMinutes(5);
        log.Refused();

        await Assert.That(written).IsEquivalentTo(new long[] { 1, 1000 });
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
