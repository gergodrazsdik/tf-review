using System.Globalization;
using System.Text;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using TfReview.Agents;
using TfReview.Core;
using TfReview.Rag;
using TfReview.Service;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TfReview.Eval;

/// <summary>
/// Measures what each seeded case's agent would get as references, against docs picked by hand, for each ranking
/// (D5). Agent evals can't show this, because a reference can't create a finding, so a bad one only shows as a worse
/// suggestion. It reads the real index as whoever runs it, read-only, and each query costs a fraction of a cent.
/// </summary>
internal static class RetrievalEval
{
    private const string ProviderVersion = "5.12.0";

    private static readonly ReferenceRanking[] Rankings = [ReferenceRanking.Metadata, ReferenceRanking.Vector, ReferenceRanking.Hybrid];

    public static async Task<int> RunAsync(string labelsPath, string casesDirectory, string standardsDirectory, string? reportPath)
    {
        var labels = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<LabelFile>(await File.ReadAllTextAsync(labelsPath));
        var corpus = await StandardsCorpus.LoadAsync(standardsDirectory);

        // Locally, the default credential hangs probing for a managed identity, so only developer credentials are tried.
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true, ExcludeWorkloadIdentityCredential = true });
        var container = new CosmosClient(Required("TF_REVIEW_COSMOS_ENDPOINT"), credential, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway })
            .GetContainer("tfreview", "references");
        var retriever = new CosmosReferenceRetriever(
            container, AzureQueryEmbedder.Create(new Uri(Required("TF_REVIEW_OPENAI_ENDPOINT")), "text-embedding-3-small", credential));

        var rows = new List<Row>();
        foreach (var label in labels.Cases)
        {
            var slice = await SliceAsync(label, casesDirectory, corpus);
            foreach (var ranking in Rankings)
            {
                var query = ReviewRunner.ReferenceQueryFor(Profile(label.Agent), slice, ProviderVersion, ranking);
                var result = await retriever.RetrieveAsync(query);
                var found = label.Relevant.Count(relevant => result.Docs.Any(doc => relevant.Matches(doc)));
                if (Environment.GetEnvironmentVariable("TF_REVIEW_EVAL_DEBUG") == label.Case)
                {
                    Console.WriteLine($"{label.Case} {ranking}: {query.Text}");
                    foreach (var doc in result.Docs)
                    {
                        Console.WriteLine($"  {doc.SourceUrl[(doc.SourceUrl.IndexOf("/blob/", StringComparison.Ordinal) + 48)..]} | {doc.Heading}");
                    }
                }
                rows.Add(new Row(label.Case, ranking, found, label.Relevant.Count, result.Docs.Count, result.Docs.Sum(doc => TokenEstimate.Of(doc.Text))));
            }
        }

        var report = Report(rows);
        Console.WriteLine(report);
        if (reportPath is not null)
        {
            await File.WriteAllTextAsync(reportPath, report);
        }

        return 0;
    }

    /// <summary>The resources the agent's standards cover, as a review routes them (D35).</summary>
    private static async Task<CondensedPlan> SliceAsync(Label label, string casesDirectory, StandardsCorpus corpus)
    {
        var plan = await File.ReadAllTextAsync(Path.Combine(casesDirectory, label.Case, "plan.json"));
        var condensed = new PlanRedactor().Redact(plan) is Redacted redacted
            ? CondensedPlan.From(redacted.Plan)
            : throw new InvalidOperationException($"{label.Case}'s plan doesn't redact.");
        var profile = Profile(label.Agent);
        var standards = await new FileRetriever(corpus).RetrieveAsync(new RetrievalQuery(profile.Topics, condensed.ResourceTypes()));
        return condensed.OnlyTypes(type => standards.Docs.Any(doc => doc.AppliesToType(type)));
    }

    private static string Report(IReadOnlyList<Row> rows)
    {
        var text = new StringBuilder()
            .AppendLine("| Ranking | Cases with something relevant | Relevant docs found | Chunks per agent | Tokens per agent |")
            .AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var ranking in Rankings)
        {
            var mine = rows.Where(row => row.Ranking == ranking).ToList();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {ranking} | {mine.Count(row => row.Found > 0)} of {mine.Count} | {mine.Sum(row => row.Found)} of {mine.Sum(row => row.Relevant)} ({100.0 * mine.Sum(row => row.Found) / mine.Sum(row => row.Relevant):0}%) | {mine.Average(row => row.Chunks):0.0} | {mine.Average(row => row.Tokens):0} |");
        }

        text.AppendLine().AppendLine("| Case | " + string.Join(" | ", Rankings) + " |").AppendLine("| --- |" + string.Concat(Rankings.Select(_ => " --- |")));
        foreach (var group in rows.GroupBy(row => row.Case))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {string.Join(" | ", Rankings.Select(ranking => group.Single(row => row.Ranking == ranking)).Select(row => $"{row.Found} of {row.Relevant}"))} |");
        }

        return text.ToString();
    }

    private static AgentProfile Profile(string name) => name switch
    {
        "conventions" => AgentProfile.Conventions,
        "security" => AgentProfile.Security,
        "cost" => AgentProfile.Cost,
        _ => throw new InvalidOperationException($"No agent {name}."),
    };

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} isn't set.");

    private sealed record Row(string Case, ReferenceRanking Ranking, int Found, int Relevant, int Chunks, int Tokens);

    private sealed class LabelFile
    {
        public List<Label> Cases { get; set; } = [];
    }

    private sealed class Label
    {
        public string Case { get; set; } = string.Empty;

        public string Agent { get; set; } = string.Empty;

        public List<Relevant> Relevant { get; set; } = [];
    }

    /// <summary>A doc by its path in its repo, written as a bare path or with a heading that narrows it to one section.</summary>
    private sealed class Relevant : IYamlConvertible
    {
        public string Path { get; set; } = string.Empty;

        public string? Heading { get; set; }

        public bool Matches(ReferenceDoc doc) =>
            doc.SourceUrl.EndsWith("/" + Path.Replace(" ", "%20", StringComparison.Ordinal), StringComparison.Ordinal)
                || doc.SourceUrl.EndsWith("/" + Path, StringComparison.Ordinal)
            ? Heading is null || doc.Heading == Heading
            : false;

        public void Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                this.Path = scalar.Value;
                return;
            }

            var mapping = (Dictionary<string, string>)nestedObjectDeserializer(typeof(Dictionary<string, string>))!;
            this.Path = mapping["path"];
            this.Heading = mapping.GetValueOrDefault("heading");
        }

        public void Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer) => throw new NotSupportedException();
    }
}
