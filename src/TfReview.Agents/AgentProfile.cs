using TfReview.Core;

namespace TfReview.Agents;

/// <summary>What makes one specialist agent different: its name, who it reports as, and what it retrieves.</summary>
public sealed record AgentProfile(string Name, Reporter Reporter, ReviewScope Scope, IReadOnlyList<string> Topics)
{
    public static readonly AgentProfile Conventions = new("conventions", Reporter.Conventions, ReviewScope.Conventions, ["naming", "tagging"]);

    public static readonly AgentProfile Security = new("security", Reporter.Security, ReviewScope.Security, ["security", "network"]);

    public static readonly AgentProfile Cost = new("cost", Reporter.Cost, ReviewScope.Cost, ["cost"]);
}
