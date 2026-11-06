// Proves the real model accepts the schema and options production sends, through each layer the agent uses: the OpenAI
// SDK, IChatClient and Agent Framework. That's the conventions and security agents' strict schema, with
// policy_suggestions answered by ID (D51) and breaks_standard (C13), at low reasoning effort, as StructuredCall sets it.
// .github/workflows/llm-smoke.yml runs it in a container with a federated token file, like the review job. It sends only
// made-up plan data. `just lint` checks that the schema is ReviewAgent's and the package pins Directory.Packages.props's.
#:package Azure.Identity@1.21.0
#:package OpenAI@2.14.0
#:package Microsoft.Extensions.AI@10.11.0
#:package Microsoft.Extensions.AI.OpenAI@10.11.0
#:package Microsoft.Agents.AI@1.27.0
#:property PublishAot=false

#pragma warning disable OPENAI001 // The authentication policy constructor and reasoning effort are experimental.

using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using SdkChat = OpenAI.Chat;

const string SchemaName = "conventions_output";
const int MaxOutputTokens = 4000;

// ReviewAgent's Schema. Azure's strict mode needs every field required and rejects pattern and minItems, so
// finding.schema.json can't be this schema.
const string Schema = """
    {
      "type": "object",
      "properties": {
        "policy_suggestions": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": { "type": "string" },
              "suggestion": { "type": ["string", "null"] }
            },
            "required": ["id", "suggestion"],
            "additionalProperties": false
          }
        },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "resource_address": { "type": "string" },
              "doc_id": { "type": "string" },
              "attribute": { "type": "string" },
              "title": { "type": "string" },
              "suggestion": { "type": ["string", "null"] },
              "breaks_standard": { "type": "boolean" }
            },
            "required": ["resource_address", "doc_id", "attribute", "title", "suggestion", "breaks_standard"],
            "additionalProperties": false
          }
        }
      },
      "required": ["policy_suggestions", "findings"],
      "additionalProperties": false
    }
    """;

const string Instructions = """
    You review OpenTofu plan changes against the standards below. Report only violations.
    ADR-001: names follow <type>-<workload>-<environment>, such as vnet-app-prod.
    ADR-002: every taggable resource must have the tags owner, environment and project.
    Each entry in <policy_findings> is already reported: answer it by its id in policy_suggestions, with the change
    that fixes it. Report in findings only what the policy findings miss, and set breaks_standard last, to whether the
    resource really breaks the standard you cite.
    """;

const string Plan = """
    <policy_findings>[{"doc_id":"ADR-002","id":"P1","resource_address":"azurerm_virtual_network.smoke",
    "title":"ADR-002: azurerm_virtual_network.smoke: missing tags owner, environment, project"}]</policy_findings>

    <plan_data>{"resource_changes":{"azurerm_virtual_network.smoke":{"type":"azurerm_virtual_network",
    "actions":["create"],"after":{"name":"smoke-network","tags":null}}}}</plan_data>
    """;

var endpoint = Environment.GetEnvironmentVariable("TF_REVIEW_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("TF_REVIEW_OPENAI_ENDPOINT isn't set.");
var deployment = Environment.GetEnvironmentVariable("TF_REVIEW_OPENAI_DEPLOYMENT")
    ?? throw new InvalidOperationException("TF_REVIEW_OPENAI_DEPLOYMENT isn't set.");

var requests = new RequestCapture();
var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
clientOptions.AddPolicy(requests, PipelinePosition.PerCall);

var client = new OpenAIClient(
    new BearerTokenPolicy(new DefaultAzureCredential(), "https://ai.azure.com/.default"),
    clientOptions);
var sdkClient = client.GetChatClient(deployment);
var chatClient = sdkClient.AsIChatClient();

var failures = 0;

await Check("OpenAI SDK", async () =>
{
    var completion = await sdkClient.CompleteChatAsync(
        [new SdkChat.SystemChatMessage(Instructions), new SdkChat.UserChatMessage(Plan)],
        new SdkChat.ChatCompletionOptions
        {
            MaxOutputTokenCount = MaxOutputTokens,
            ReasoningEffortLevel = SdkChat.ChatReasoningEffortLevel.Low,
            ResponseFormat = SdkChat.ChatResponseFormat.CreateJsonSchemaFormat(
                SchemaName, BinaryData.FromString(Schema), jsonSchemaIsStrict: true),
        });
    var value = completion.Value;
    return new Answer(
        value.Content[0].Text,
        value.FinishReason.ToString(),
        value.Usage.InputTokenCount,
        value.Usage.OutputTokenCount);
});

await Check("IChatClient", async () =>
{
    var response = await chatClient.GetResponseAsync(
        [new(ChatRole.System, Instructions), new(ChatRole.User, Plan)],
        StrictOptions());
    return new Answer(
        response.Text,
        response.FinishReason?.ToString() ?? "unknown",
        response.Usage?.InputTokenCount,
        response.Usage?.OutputTokenCount);
});

await Check("Agent Framework", async () =>
{
    var options = StrictOptions();
    options.Instructions = Instructions;
    AIAgent agent = new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions { Name = "conventions", ChatOptions = options });
    var response = await agent.RunAsync(Plan);
    return new Answer(
        response.Text,
        "unknown",
        response.Usage?.InputTokenCount,
        response.Usage?.OutputTokenCount);
});

Console.WriteLine(failures == 0 ? "PASS: every layer works." : $"FAIL: {failures} layer(s) failed.");
return failures == 0 ? 0 : 1;

static ChatOptions StrictOptions() => new()
{
    MaxOutputTokens = MaxOutputTokens,
    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
    ResponseFormat = ChatResponseFormat.ForJsonSchema(
        JsonDocument.Parse(Schema).RootElement, SchemaName),
    AdditionalProperties = new() { ["strict"] = true },
};

async Task Check(string layer, Func<Task<Answer>> call)
{
    Console.WriteLine($"--- {layer}");
    try
    {
        var answer = await call();
        Console.WriteLine($"strict sent: {requests.LastWasStrict}, low reasoning effort sent: {requests.LastHadLowEffort}");
        Console.WriteLine($"finish: {answer.Finish}, tokens in: {answer.In}, out: {answer.Out}");
        Console.WriteLine($"answer: {answer.Text}");

        // Every field production reads has to be there with its type, which strict mode should guarantee.
        using var document = JsonDocument.Parse(answer.Text);
        var policySuggestions = document.RootElement.GetProperty("policy_suggestions");
        var findings = document.RootElement.GetProperty("findings");
        foreach (var finding in findings.EnumerateArray())
        {
            _ = finding.GetProperty("breaks_standard").GetBoolean();
        }

        var answered = policySuggestions.EnumerateArray().Any(item => item.GetProperty("id").GetString() == "P1");
        var named = findings.EnumerateArray().Any(finding =>
            finding.GetProperty("resource_address").GetString() == "azurerm_virtual_network.smoke"
            && finding.GetProperty("doc_id").GetString() == "ADR-001"
            && finding.GetProperty("breaks_standard").GetBoolean());

        if (!requests.LastWasStrict)
        {
            throw new InvalidOperationException("The request didn't use strict mode.");
        }

        if (!requests.LastHadLowEffort)
        {
            throw new InvalidOperationException("The request didn't set low reasoning effort, which production does.");
        }

        // A wrong answer isn't a failed smoke test: this proves the plumbing, and the evals judge
        // quality. It's still worth seeing.
        Console.WriteLine($"OK: valid JSON, {findings.GetArrayLength()} finding(s), P1 answered: {answered}, ADR-001 name finding: {named}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"FAILED: {e.GetType().Name}: {e.Message}");
    }
}

internal sealed record Answer(string Text, string Finish, long? In, long? Out);

// Records whether the request really asked for strict mode, since a layer could drop the setting
// without any error. It never records the bearer token, which is in the headers.
internal sealed class RequestCapture : PipelinePolicy
{
    public bool LastWasStrict { get; private set; }

    public bool LastHadLowEffort { get; private set; }

    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Record(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Record(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Record(PipelineMessage message)
    {
        using var body = new MemoryStream();
        message.Request.Content?.WriteTo(body);
        var text = Encoding.UTF8.GetString(body.ToArray());
        LastWasStrict = text.Contains("\"strict\":true", StringComparison.Ordinal);

        // Chat Completions sends reasoning_effort, the Responses API reasoning.effort.
        LastHadLowEffort = text.Contains("\"reasoning_effort\":\"low\"", StringComparison.Ordinal)
            || text.Contains("\"effort\":\"low\"", StringComparison.Ordinal);
    }
}
