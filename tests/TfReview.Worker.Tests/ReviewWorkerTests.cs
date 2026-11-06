using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TfReview.Core;
using TfReview.GitHub;
using TfReview.Service;

namespace TfReview.Worker.Tests;

/// <summary>The real worker and GitHub code, over a fake GitHub and fake storage.</summary>
public class ReviewWorkerTests
{
    private const string Head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Newer = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Base = "dddddddddddddddddddddddddddddddddddddddd";
    private const string Plan = """{"format_version":"1.2","terraform_version":"1.13.2","planned_values":{"root_module":{}},"resource_changes":[],"configuration":{}}""";
    private const string Config = "standards:\n  repo: a/b\n";

    private static readonly ReviewJob Job = new("review1", "1441359044", "owner/repo", 7, Head, ".tf-review.yml");

    [Test]
    public async Task An_empty_queue_does_nothing()
    {
        var world = new World { Queued = null };

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.GitHub.Requests).IsEmpty();
        await Assert.That(world.Engine.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_review_posts_its_comment_and_check_run_then_finishes_the_message()
    {
        var world = new World();

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.GitHub.Comments.Count).IsEqualTo(1);
        var check = world.GitHub.CheckRuns.Single();
        await Assert.That(check.GetProperty("head_sha").GetString()).IsEqualTo(Head);
        await Assert.That(check.GetProperty("conclusion").GetString()).IsEqualTo("success");
        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Done);
        await Assert.That(world.Statuses.Last.Verdict).IsEqualTo("no_findings");
    }

    [Test]
    public async Task A_review_reports_its_outcome()
    {
        var reported = new ConcurrentBag<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meters) =>
        {
            if (instrument.Name == "tfreview.reviews")
            {
                meters.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                reported.Add(tag.Value?.ToString());
            }
        });
        listener.Start();

        await new World().RunAsync();

        await Assert.That(reported).Contains("owner/repo");
    }

    [Test]
    public async Task The_config_comes_from_the_base_commit()
    {
        var world = new World();

        await world.RunAsync();

        await Assert.That(world.Engine.Config).IsEqualTo(Config);
        await Assert.That(world.GitHub.Requests).Contains($"GET /repos/owner/repo/contents/.tf-review.yml?ref={Base}");
        await Assert.That(world.GitHub.Requests.Any(request => request.Contains($"ref={Head}", StringComparison.Ordinal))).IsFalse();
    }

    // The result cache's scope (D53): another repo's answers must never come back.
    [Test]
    public async Task The_review_is_scoped_to_the_jobs_repo()
    {
        var world = new World();

        await world.RunAsync();

        await Assert.That(world.Engine.RepositoryId).IsEqualTo(Job.RepositoryId);
    }

    [Test]
    public async Task The_lock_file_comes_from_the_working_directory_at_the_base_commit()
    {
        var world = new World { Queued = Job with { WorkingDirectory = "infra" } };

        await world.RunAsync();

        await Assert.That(world.GitHub.Requests).Contains($"GET /repos/owner/repo/contents/infra/.terraform.lock.hcl?ref={Base}");
        await Assert.That(world.Engine.LockFile).IsEqualTo(Config);
    }

    // BUG-20: the contents API takes the root's file as a plain name, not under ".".
    [Test]
    [Arguments(".", ".terraform.lock.hcl")]
    [Arguments("./", ".terraform.lock.hcl")]
    [Arguments("./infra/", "infra/.terraform.lock.hcl")]
    public async Task A_working_directory_at_the_root_reads_the_roots_lock_file(string directory, string path)
    {
        var world = new World { Queued = Job with { WorkingDirectory = directory } };

        await world.RunAsync();

        await Assert.That(world.GitHub.Requests).Contains($"GET /repos/owner/repo/contents/{path}?ref={Base}");
    }

    // Callers pinned to a workflow from before the working directory was sent.
    [Test]
    public async Task A_job_without_a_working_directory_reads_no_lock_file()
    {
        var world = new World();

        await world.RunAsync();

        await Assert.That(world.GitHub.Requests.Any(request => request.Contains(".terraform.lock.hcl", StringComparison.Ordinal))).IsFalse();
        await Assert.That(world.Engine.LockFile).IsNull();
    }

    [Test]
    public async Task A_missing_config_reaches_the_review_as_none()
    {
        var world = new World();
        world.GitHub.ConfigFound = false;

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(1);
        await Assert.That(world.Engine.Config).IsNull();
    }

    [Test]
    public async Task The_token_is_revoked_after_the_review()
    {
        var world = new World();

        await world.RunAsync();

        await Assert.That(world.GitHub.Requests.Last()).IsEqualTo("DELETE /installation/token");
    }

    [Test]
    public async Task A_commit_no_longer_at_the_head_is_dropped_without_a_review()
    {
        var world = new World();
        world.GitHub.Heads.Enqueue(Newer);

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(0);
        await Assert.That(world.GitHub.Comments).IsEmpty();
        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Superseded);
    }

    // The push landed during the review, and its own review may already have posted.
    [Test]
    public async Task A_push_during_the_review_stops_it_overwriting_the_newer_result()
    {
        var world = new World();
        world.GitHub.Heads.Enqueue(Head);
        world.GitHub.Heads.Enqueue(Newer);

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(1);
        await Assert.That(world.GitHub.Comments).IsEmpty();
        await Assert.That(world.GitHub.CheckRuns).IsEmpty();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Superseded);
    }

    [Test]
    [Arguments(ReviewState.Done)]
    [Arguments(ReviewState.Failed)]
    [Arguments(ReviewState.Superseded)]
    public async Task A_duplicate_message_for_a_finished_review_is_dropped(ReviewState state)
    {
        var world = new World();
        await world.Statuses.WriteAsync(world.StatusOf(state, world.Now));

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(0);
        await Assert.That(world.Completed).IsTrue();
    }

    [Test]
    public async Task A_duplicate_message_for_a_running_review_is_dropped()
    {
        var world = new World();
        await world.Statuses.WriteAsync(world.StatusOf(ReviewState.Running, world.Now.AddMinutes(-1)));

        await world.RunAsync();

        await Assert.That(world.Engine.Calls).IsEqualTo(0);
    }

    // The job died mid-review, and this is its own message coming back.
    [Test]
    public async Task A_review_left_running_past_a_jobs_life_runs_again()
    {
        var world = new World();
        await world.Statuses.WriteAsync(world.StatusOf(ReviewState.Running, world.Now - ReviewWorker.Visibility));

        await world.RunAsync();

        await Assert.That(world.Engine.Calls).IsEqualTo(1);
    }

    // Its own message returned just as its hiding time ended, which is under 20 minutes after the status was written.
    [Test]
    public async Task A_message_coming_back_runs_again_however_recent_the_status()
    {
        var world = new World { Attempt = 2 };
        await world.Statuses.WriteAsync(world.StatusOf(ReviewState.Running, world.Now.AddMinutes(-1)));

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(1);
        await Assert.That(world.GitHub.Comments.Count).IsEqualTo(1);
    }

    // Each earlier try died before it could give up, and another would only spend more on the model.
    [Test]
    public async Task A_message_past_its_tries_says_not_reviewed_without_another_review()
    {
        var world = new World { Attempt = ReviewWorker.MaxAttempts + 1 };

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.Engine.Calls).IsEqualTo(0);
        await Assert.That(world.GitHub.CheckRuns.Single().GetProperty("output").GetProperty("title").GetString()).IsEqualTo("Not reviewed");
        await Assert.That(world.Completed).IsTrue();
    }

    // The deadline has passed, so nothing can use the job's own token any more.
    [Test]
    public async Task Running_out_of_time_on_the_last_try_still_says_not_reviewed()
    {
        var world = new World { Attempt = ReviewWorker.MaxAttempts };
        using var deadline = new CancellationTokenSource();
        await deadline.CancelAsync();
        world.Engine.Throws = new OperationCanceledException(deadline.Token);

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.GitHub.Comments.Count).IsEqualTo(1);
        await Assert.That(world.Completed).IsTrue();
    }

    [Test]
    public async Task A_message_that_cant_be_read_is_dropped()
    {
        var world = new World();
        world.Queued = null;
        world.Unreadable = true;

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.GitHub.Requests).IsEmpty();
    }

    [Test]
    public async Task A_missing_plan_says_not_reviewed()
    {
        var world = new World();
        world.Plans.Saved.Clear();

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(0);
        await Assert.That(world.GitHub.CheckRuns.Single().GetProperty("conclusion").GetString()).IsEqualTo("neutral");
        await Assert.That(world.Statuses.Last.Verdict).IsEqualTo("not_reviewed");
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_failure_leaves_the_message_to_come_back(int attempt)
    {
        var world = new World { Attempt = attempt };
        world.Engine.Throws = new InvalidOperationException("CANARY plan value");

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.Completed).IsFalse();
        await Assert.That(world.GitHub.Comments).IsEmpty();
        await Assert.That(string.Join('\n', world.Log)).DoesNotContain("CANARY");
    }

    [Test]
    public async Task The_last_try_says_not_reviewed()
    {
        var world = new World { Attempt = ReviewWorker.MaxAttempts };
        world.Engine.Throws = new InvalidOperationException("CANARY plan value");

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.GitHub.Comments.Count).IsEqualTo(1);
        await Assert.That(world.GitHub.Comments.Single()).DoesNotContain("CANARY");
        await Assert.That(world.GitHub.CheckRuns.Single().GetProperty("output").GetProperty("title").GetString()).IsEqualTo("Not reviewed");
        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Failed);
    }

    // OPS-1: setup such as reading the App's key happens after the message is taken, so it fails a try like a review does.
    [Test]
    public async Task A_setup_failure_counts_as_a_try()
    {
        var world = new World { Attempt = 1 };
        world.OpenThrows = new InvalidOperationException("Key Vault is down");

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.Completed).IsFalse();
        await Assert.That(world.Engine.Calls).IsEqualTo(0);
    }

    // OPS-1: with no App to post through, the review still ends, loudly, and is counted as given up.
    [Test]
    public async Task A_setup_failure_on_the_last_try_gives_up_loudly()
    {
        using var measured = new Measured("tfreview.give_ups");
        var world = new World { Attempt = ReviewWorker.MaxAttempts, Queued = Job with { Repository = "owner/setup-fails" } };
        world.OpenThrows = new InvalidOperationException("Key Vault is down");

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Failed);
        await Assert.That(world.Log.Any(line => line.StartsWith("ERROR: review review1 gave up", StringComparison.Ordinal))).IsTrue();
        var giveUp = measured.For("owner/setup-fails").Single();
        await Assert.That(giveUp["cause"]).IsEqualTo("InvalidOperationException");
        await Assert.That(giveUp["posted"] is false).IsTrue();
    }

    [Test]
    public async Task A_give_up_is_counted_with_its_cause()
    {
        using var measured = new Measured("tfreview.give_ups");
        var world = new World { Attempt = ReviewWorker.MaxAttempts + 1, Queued = Job with { Repository = "owner/used-up" } };

        await world.RunAsync();

        var giveUp = measured.For("owner/used-up").Single();
        await Assert.That(giveUp["cause"]).IsEqualTo("tries_used_up");
        await Assert.That(giveUp["posted"] is true).IsTrue();
    }

    // BUG-1: a newer push has had its own review, which a give-up for this commit mustn't replace.
    [Test]
    public async Task A_give_up_for_a_commit_no_longer_at_the_head_posts_nothing()
    {
        var world = new World { Attempt = ReviewWorker.MaxAttempts + 1 };
        world.GitHub.Heads.Enqueue(Newer);

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.GitHub.Comments).IsEmpty();
        await Assert.That(world.GitHub.CheckRuns).IsEmpty();
        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Superseded);
    }

    [Test]
    public async Task Running_out_of_time_says_so()
    {
        var world = new World { Attempt = ReviewWorker.MaxAttempts };
        world.Engine.Throws = new OperationCanceledException();

        await world.RunAsync();

        await Assert.That(world.GitHub.Comments.Single()).Contains("timed out");
    }

    // A post that fails comes back as a retry, which mustn't count the review again.
    [Test]
    public async Task A_review_whose_post_fails_isnt_counted()
    {
        using var measured = new Measured("tfreview.reviews");
        var world = new World { Queued = Job with { Repository = "owner/post-fails" } };
        world.GitHub.FailWrites = true;

        await world.RunAsync();

        await Assert.That(measured.For("owner/post-fails")).IsEmpty();
    }

    // OPS-19: the result is kept, so a bad review can be looked into after its plan has gone.
    [Test]
    public async Task The_result_is_kept_by_repo_and_review()
    {
        var world = new World();

        await world.RunAsync();

        var (key, json) = world.Results.Saved.Single();
        await Assert.That(key).IsEqualTo($"{Job.RepositoryId}/{Job.ReviewId}");
        await Assert.That(ReviewResult.Parse(json).Commit).IsEqualTo(Head);
    }

    [Test]
    public async Task A_result_that_cant_be_kept_is_still_posted()
    {
        var world = new World();
        world.Results.Fail = true;

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.GitHub.Comments.Count).IsEqualTo(1);
    }

    // D62: past the day's limit the model isn't called, and the PR is told why.
    [Test]
    public async Task A_spent_daily_budget_says_not_reviewed_without_a_review()
    {
        var world = new World { Budget = new FakeBudget { Reached = true } };

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(0);
        await Assert.That(world.GitHub.Comments.Single()).Contains("the daily model budget was reached");
        await Assert.That(world.Statuses.Last.Verdict).IsEqualTo("not_reviewed");
    }

    [Test]
    public async Task A_reviews_spend_is_recorded_even_when_it_fails()
    {
        var world = new World { Budget = new FakeBudget() };
        world.Engine.Throws = new InvalidOperationException("failed");

        await world.RunAsync();

        await Assert.That(world.Budget!.Recorded).IsEqualTo(1);
    }

    // The Azure budget's alerts are still behind it, and a store outage mustn't stop every review.
    [Test]
    public async Task A_budget_that_cant_be_read_lets_the_review_go_ahead()
    {
        var world = new World { Budget = new FakeBudget { Fails = true } };

        await Assert.That(await world.RunAsync()).IsEqualTo(0);

        await Assert.That(world.Engine.Calls).IsEqualTo(1);
    }

    // GitHub is the thing that's down: there's nowhere to say so, but the queue mustn't keep it forever.
    [Test]
    public async Task The_last_try_still_finishes_when_it_cant_post()
    {
        var world = new World { Attempt = ReviewWorker.MaxAttempts };
        world.GitHub.FailWrites = true;

        await Assert.That(await world.RunAsync()).IsEqualTo(1);

        await Assert.That(world.Completed).IsTrue();
        await Assert.That(world.Statuses.Last.State).IsEqualTo(ReviewState.Failed);
    }

    [Test]
    public async Task Findings_are_a_neutral_check_because_a_check_is_advisory()
    {
        var world = new World();
        world.Engine.Result = new ReviewResult(
            1,
            Head,
            Verdict.Findings,
            [new Finding("azurerm_virtual_network.a", "ADR-001", Severity.Medium, Reporter.Scanner, true, "Name", [new DocEvidence("ADR-001")])],
            [],
            new Dictionary<string, int>());

        await world.RunAsync();

        var check = world.GitHub.CheckRuns.Single();
        await Assert.That(check.GetProperty("conclusion").GetString()).IsEqualTo("neutral");
        await Assert.That(check.GetProperty("output").GetProperty("title").GetString()).IsEqualTo("1 finding");
    }

    // D56: a finding a reviewer ticked as wrong stays ticked on the next push, marked as already counted.
    [Test]
    public async Task A_dismissal_carries_over_to_the_next_push()
    {
        var finding = new Finding("azurerm_virtual_network.a", "ADR-001", Severity.Medium, Reporter.Scanner, true, "Name", [new DocEvidence("ADR-001")]);
        var result = new ReviewResult(1, Head, Verdict.Findings, [finding], [], new Dictionary<string, int>());
        var world = new World();
        world.Engine.Result = result;
        world.GitHub.PreviousComment = CommentRenderer.Render(result, null).Replace("- [ ]", "- [x]", StringComparison.Ordinal);

        await world.RunAsync();

        var dismissal = Dismissals.Ticked(world.GitHub.Comments.Single()).Single();
        await Assert.That(dismissal.Key).IsEqualTo(Dismissals.Key(finding));
        await Assert.That(dismissal.Recorded).IsTrue();
    }

    // Each dismissal reaches the metric once: a fresh tick counts, and one carried over from an earlier push doesn't.
    [Test]
    [Arguments("TEST-FRESH", false, 1)]
    [Arguments("TEST-CARRIED", true, 0)]
    public async Task A_dismissal_is_counted_once(string rule, bool recorded, int expected)
    {
        var counted = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meters) =>
        {
            if (instrument.Name == "tfreview.dismissals")
            {
                meters.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rule" && tag.Value is string value)
                {
                    counted.Add(value);
                }
            }
        });
        listener.Start();

        var finding = new Finding("azurerm_virtual_network.a", rule, Severity.Medium, Reporter.Scanner, true, "Name", [new DocEvidence(rule)]);
        var result = new ReviewResult(1, Head, Verdict.Findings, [finding], [], new Dictionary<string, int>());
        var world = new World();
        world.Engine.Result = result;
        var ticked = CommentRenderer.Render(result, null).Replace("- [ ]", "- [x]", StringComparison.Ordinal);
        world.GitHub.PreviousComment = recorded ? CommentRenderer.Rewrite(result, null, ticked, record: true).Body : ticked;

        await world.RunAsync();

        await Assert.That(counted.Count(value => value == rule)).IsEqualTo(expected);
    }

    // BUG-7: a tick is counted once the comment holding its mark is written, so a failed write and its retry count it once.
    [Test]
    public async Task A_dismissal_isnt_counted_when_the_comment_cant_be_written()
    {
        using var measured = new Measured("tfreview.dismissals");
        var finding = new Finding("azurerm_virtual_network.a", "ADR-001", Severity.Medium, Reporter.Scanner, true, "Name", [new DocEvidence("ADR-001")]);
        var result = new ReviewResult(1, Head, Verdict.Findings, [finding], [], new Dictionary<string, int>());
        var world = new World { Queued = Job with { Repository = "owner/write-fails" } };
        world.Engine.Result = result;
        world.GitHub.PreviousComment = CommentRenderer.Render(result, null).Replace("- [ ]", "- [x]", StringComparison.Ordinal);
        world.GitHub.FailWrites = true;

        await world.RunAsync();

        await Assert.That(measured.For("owner/write-fails")).IsEmpty();
    }

    // BUG-2: the give-up's "not reviewed" keeps the tick, hidden, for the finding's return.
    [Test]
    public async Task A_give_up_keeps_the_ticks()
    {
        var finding = new Finding("azurerm_virtual_network.a", "ADR-001", Severity.Medium, Reporter.Scanner, true, "Name", [new DocEvidence("ADR-001")]);
        var result = new ReviewResult(1, Head, Verdict.Findings, [finding], [], new Dictionary<string, int>());
        var world = new World { Attempt = ReviewWorker.MaxAttempts + 1 };
        world.GitHub.PreviousComment = CommentRenderer.Render(result, null).Replace("- [ ]", "- [x]", StringComparison.Ordinal);

        await world.RunAsync();

        var dismissal = Dismissals.Ticked(world.GitHub.Comments.Single()).Single();
        await Assert.That(dismissal.Key).IsEqualTo(Dismissals.Key(finding));
        await Assert.That(dismissal.Recorded).IsTrue();
    }

    // D55: a repo that opted in gets a failed check only for what the runner marked blocking.
    [Test]
    public async Task A_blocking_finding_fails_the_check()
    {
        var world = new World();
        world.Engine.Result = new ReviewResult(
            1,
            Head,
            Verdict.Findings,
            [
                new Finding("azurerm_network_security_rule.a", "POL-002", Severity.High, Reporter.Security, true, "SSH", [new DocEvidence("POL-002")]) { Blocking = true },
                new Finding("azurerm_virtual_network.a", "ADR-001", Severity.Low, Reporter.Scanner, true, "Name", [new DocEvidence("ADR-001")]),
            ],
            [],
            new Dictionary<string, int>())
        { Gate = GateMode.Blocking };

        await world.RunAsync();

        var check = world.GitHub.CheckRuns.Single();
        await Assert.That(check.GetProperty("conclusion").GetString()).IsEqualTo("failure");
        await Assert.That(check.GetProperty("output").GetProperty("title").GetString()).IsEqualTo("2 findings, 1 blocking");
    }

    // An outage can't block a merge, even in a repo that requires the check.
    [Test]
    public async Task Not_reviewed_stays_neutral_under_a_blocking_gate()
    {
        var world = new World();
        world.Engine.Result = ReviewResult.Failed(Head, ReviewScope.Plan, NotReviewedReason.Unavailable) with { Gate = GateMode.Blocking };

        await world.RunAsync();

        await Assert.That(world.GitHub.CheckRuns.Single().GetProperty("conclusion").GetString()).IsEqualTo("neutral");
    }

    private sealed class World
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        public ReviewJob? Queued { get; set; } = Job;

        public bool Unreadable { get; set; }

        /// <summary>What opening the repo throws, like a setup failure such as reading the App's key.</summary>
        public Exception? OpenThrows { get; set; }

        public int Attempt { get; init; } = 1;

        public bool Completed { get; private set; }

        public FakeGitHub GitHub { get; } = new();

        public FakePlans Plans { get; } = new();

        public FakeStatuses Statuses { get; } = new();

        public FakeEngine Engine { get; } = new();

        public FakeBudget? Budget { get; init; }

        public FakeResults Results { get; } = new();

        public List<string> Log { get; } = [];

        public ReviewStatus StatusOf(ReviewState state, DateTimeOffset at) =>
            new(Job.ReviewId, Job.RepositoryId, Job.Repository, Job.PullRequest, Job.Commit, state, at);

        public Task<int> RunAsync()
        {
            var worker = new ReviewWorker(
                new FakeQueue(this),
                this.Plans,
                this.Statuses,
                (repository, _) => this.OpenThrows is { } failure
                    ? throw failure
                    : Task.FromResult(new RepositorySession(
                        new HttpClient(this.GitHub) { BaseAddress = new Uri("https://api.example.test/") }, repository)),
                this.Engine.RunAsync,
                this.Log.Add,
                new FixedTime(this.Now),
                this.Budget,
                this.Results);
            return worker.RunOnceAsync();
        }

        public void Complete() => this.Completed = true;
    }

    private sealed class FakeResults : IResultStore
    {
        public List<(string Key, string Json)> Saved { get; } = [];

        public bool Fail { get; set; }

        public Task SaveAsync(string repositoryId, string reviewId, string resultJson, CancellationToken cancellationToken = default)
        {
            if (this.Fail)
            {
                throw new InvalidOperationException("No container.");
            }

            this.Saved.Add(($"{repositoryId}/{reviewId}", resultJson));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBudget : IModelBudget
    {
        public bool Reached { get; init; }

        public bool Fails { get; init; }

        public int Recorded { get; private set; }

        public Task<bool> ReachedAsync(CancellationToken cancellationToken) =>
            this.Fails ? throw new InvalidOperationException("The store is down.") : Task.FromResult(this.Reached);

        public Task RecordAsync(CancellationToken cancellationToken)
        {
            this.Recorded++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeQueue(World world) : IReviewQueue
    {
        public Task EnqueueAsync(ReviewJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReceivedJob?> ReceiveAsync(TimeSpan visibility, CancellationToken cancellationToken = default)
        {
            ReceivedJob? received = world.Queued is null && !world.Unreadable
                ? null
                : new ReceivedJob(world.Queued, world.Attempt, _ =>
                {
                    world.Complete();
                    return Task.CompletedTask;
                });
            return Task.FromResult(received);
        }
    }

    private sealed class FakePlans : IPlanStore
    {
        public ConcurrentDictionary<string, string> Saved { get; } = new() { [Job.ReviewId] = Plan };

        public Task SaveAsync(string reviewId, string plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> ReadAsync(string reviewId, CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Saved.GetValueOrDefault(reviewId));
    }

    private sealed class FakeStatuses : IReviewStatusStore
    {
        private ReviewStatus? status;

        public ReviewStatus Last => this.status ?? throw new InvalidOperationException("No status was written.");

        public Task<ReviewStatus?> ReadAsync(string repositoryId, string reviewId, CancellationToken cancellationToken = default) =>
            Task.FromResult(this.status);

        public Task WriteAsync(ReviewStatus status, CancellationToken cancellationToken = default)
        {
            this.status = status;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEngine
    {
        public int Calls { get; private set; }

        public string? Config { get; private set; }

        public string? LockFile { get; private set; }

        public string? RepositoryId { get; private set; }

        public Exception? Throws { get; set; }

        public ReviewResult Result { get; set; } = new(1, Head, Verdict.NoFindings, [], [], new Dictionary<string, int>());

        public Task<ReviewResult> RunAsync(
            string plan, string? config, string? lockFile, string commit, string repositoryId, CancellationToken cancellationToken)
        {
            this.Calls++;
            this.RepositoryId = repositoryId;
            this.Config = config;
            this.LockFile = lockFile;
            return this.Throws is { } failure ? throw failure : Task.FromResult(this.Result);
        }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> Comments { get; } = [];

        public List<JsonElement> CheckRuns { get; } = [];

        /// <summary>The PR's head on each read in turn, and the job's commit once these run out.</summary>
        public Queue<string> Heads { get; } = new();

        public bool ConfigFound { get; set; } = true;

        public bool FailWrites { get; set; }

        /// <summary>The App's comment already on the PR, if any.</summary>
        public string? PreviousComment { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            this.Requests.Add($"{request.Method} {path}");

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get)
            {
                return path switch
                {
                    _ when path.EndsWith("/pulls/7", StringComparison.Ordinal) => Json(new JsonObject
                    {
                        ["head"] = new JsonObject { ["sha"] = this.Heads.Count > 0 ? this.Heads.Dequeue() : Head },
                        ["base"] = new JsonObject { ["sha"] = Base },
                    }.ToJsonString()),
                    _ when path.Contains("/contents/", StringComparison.Ordinal) => this.ConfigFound
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Config) }
                        : new HttpResponseMessage(HttpStatusCode.NotFound),
                    _ when path.Contains("/comments", StringComparison.Ordinal) => Json(this.PreviousComment is null
                        ? "[]"
                        : new JsonArray(new JsonObject
                        {
                            ["id"] = 42,
                            ["user"] = new JsonObject { ["login"] = "tf-review-gergodrazsdik[bot]" },
                            ["body"] = this.PreviousComment,
                        }).ToJsonString()),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            if (this.FailWrites)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            if (path.Contains("/check-runs", StringComparison.Ordinal))
            {
                this.CheckRuns.Add(body);
            }
            else
            {
                this.Comments.Add(body.GetProperty("body").GetString()!);
            }

            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// What one instrument records while it lives. Tests run in parallel, so each looks only at its own repository's.
    /// </summary>
    private sealed class Measured : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<Dictionary<string, object?>> measurements = new();

        public Measured(string instrumentName)
        {
            this.listener.InstrumentPublished = (instrument, meters) =>
            {
                if (instrument.Name == instrumentName)
                {
                    meters.EnableMeasurementEvents(instrument);
                }
            };
            this.listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var measurement = new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = value };
                foreach (var tag in tags)
                {
                    measurement[tag.Key] = tag.Value;
                }

                this.measurements.Enqueue(measurement);
            });
            this.listener.Start();
        }

        public List<Dictionary<string, object?>> For(string repository) =>
            [.. this.measurements.Where(measurement => Equals(measurement.GetValueOrDefault("repository"), repository))];

        public void Dispose() => this.listener.Dispose();
    }
}
