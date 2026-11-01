// Proves the real model accepts M1's flat strict schema, through each layer the agent will use:
// the OpenAI SDK, IChatClient and Agent Framework. .github/workflows/llm-smoke.yml runs it in a
// container with a federated token file, like the review job. It sends only made-up plan data.
#:package Azure.Identity@1.21.0
#:package OpenAI@2.14.0
#:package Microsoft.Extensions.AI@10.11.0
#:package Microsoft.Extensions.AI.OpenAI@10.11.0
#:package Microsoft.Agents.AI@1.27.0
#:property PublishAot=false

#pragma warning disable OPENAI001 // The authentication policy constructor is experimental.

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

// The wire schema from docs/plan.md. Azure's strict mode needs every field required and rejects
// pattern and minItems, so finding.schema.json can't be this schema.
const string Schema = """
    {
      "type": "object",
      "properties": {
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "resource_address": { "type": "string" },
              "doc_id": { "type": "string" },
              "attribute": { "type": "string" },
              "title": { "type": "string" },
              "suggestion": { "type": ["string", "null"] }
            },
            "required": ["resource_address", "doc_id", "attribute", "title", "suggestion"],
            "additionalProperties": false
          }
        }
      },
      "required": ["findings"],
      "additionalProperties": false
    }
    """;

const string Instructions = """
    You review OpenTofu plan changes against the standards below. Report only violations.
    ADR-002: every taggable resource must have the tags owner, environment and project.
    """;

const string Plan = """
    <plan_data>{"resource_changes":{"azurerm_virtual_network.smoke":{"type":"azurerm_virtual_network",
    "actions":["create"],"after":{"name":"vnet-smoke-prod","tags":null}}}}</plan_data>
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
        Console.WriteLine($"strict sent: {requests.LastWasStrict}");
        Console.WriteLine($"finish: {answer.Finish}, tokens in: {answer.In}, out: {answer.Out}");
        Console.WriteLine($"answer: {answer.Text}");

        using var document = JsonDocument.Parse(answer.Text);
        var findings = document.RootElement.GetProperty("findings");
        var found = findings.EnumerateArray().Any(finding =>
            finding.GetProperty("resource_address").GetString() == "azurerm_virtual_network.smoke"
            && finding.GetProperty("doc_id").GetString() == "ADR-002");

        if (!requests.LastWasStrict)
        {
            throw new InvalidOperationException("The request didn't use strict mode.");
        }

        // A wrong answer isn't a failed smoke test: this proves the plumbing, and the evals judge
        // quality. It's still worth seeing.
        Console.WriteLine($"OK: valid JSON, {findings.GetArrayLength()} finding(s), expected finding: {found}");
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
    }
}
