using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;

namespace TfReview.Agents;

/// <summary>
/// The model behind every agent, through Entra ID, since key auth is off on the resource. In the review job
/// the credential uses workload identity from a federated token file, because the image has no az.
/// </summary>
public static class LlmClient
{
    private const string Scope = "https://ai.azure.com/.default";

    public static IChatClient Create(Uri endpoint, string deployment)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,

            // The SDK retries 429s and 5xx with backoff, honouring Retry-After, within the agent's own deadline.
            NetworkTimeout = TimeSpan.FromMinutes(2),
        };

#pragma warning disable OPENAI001 // The authentication policy constructor is experimental.
        var client = new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(), Scope), options);
#pragma warning restore OPENAI001

        return client.GetChatClient(deployment).AsIChatClient();
    }
}
