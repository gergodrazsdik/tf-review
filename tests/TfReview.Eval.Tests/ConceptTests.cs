namespace TfReview.Eval.Tests;

/// <summary>
/// Each case's concept patterns on sentences that do and don't express the concept. The ones that don't come from the
/// 2026-11-06 review (MNT-2), which found patterns scoring generic words, words inside other words, and the very
/// attribute that forces a replacement. Patterns stay about the concept, not one model's wording.
/// </summary>
public class ConceptTests
{
    [Test]
    // The attribute that forces the replacement isn't the backup or the in-place concept.
    [Arguments("c27-replace-postgres", "backup", "Turning on `geo_redundant_backup_enabled` forces a new server.", false)]
    [Arguments("c27-replace-postgres", "in_place", "Turning on `geo_redundant_backup_enabled` forces a new server.", false)]
    [Arguments("c27-replace-postgres", "backup", "Geo-redundant backups can only be enabled when a server is created.", false)]
    [Arguments("c27-replace-postgres", "in_place", "Create a separate new server and switch over.", false)]
    [Arguments("c27-replace-postgres", "replica", "Backups are replicated to a paired region.", false)]
    [Arguments("c27-replace-postgres", "backup", "Back up the data with pg_dump before the replacement.", true)]
    [Arguments("c27-replace-postgres", "backup", "Confirm the server's data is backed up or disposable.", true)]
    [Arguments("c27-replace-postgres", "in_place", "Change the plan to avoid replacing the server.", true)]
    [Arguments("c27-replace-postgres", "in_place", "Keep `geo_redundant_backup_enabled` unchanged to avoid the replacement.", true)]
    [Arguments("c27-replace-postgres", "replica", "Add a read replica first.", true)]
    [Arguments("c20-replace-server", "in_place", "Changing administrator_login forces replacement.", false)]
    [Arguments("c20-replace-server", "in_place", "Revert the administrator_login change so the server updates in place.", true)]
    [Arguments("c20-replace-server", "backup", "Export a bacpac before applying.", true)]
    // "move" inside "remove", and "unchanged" about something else.
    [Arguments("c25-replace-key-vault", "backup", "Remove the rename.", false)]
    [Arguments("c25-replace-key-vault", "in_place", "Tags stay unchanged.", false)]
    [Arguments("c25-replace-key-vault", "backup", "Back up the secrets, keys and certificates first.", true)]
    [Arguments("c25-replace-key-vault", "backup", "Move the secrets to the new vault before applying.", true)]
    [Arguments("c25-replace-key-vault", "in_place", "Keep the existing vault name so it updates in place.", true)]
    [Arguments("c25-replace-key-vault", "in_place", "Keep the key vault name unchanged.", true)]
    [Arguments("c25-replace-key-vault", "soft_delete", "Turn on purge protection.", true)]
    // ADR-003 asks to confirm the increase is intended: a bare "confirm" isn't that, nor is "standard".
    [Arguments("c30-business-critical-db", "confirm", "Confirm this meets the team's cost standard.", false)]
    [Arguments("c30-business-critical-db", "smaller_option", "Confirm this meets the team's cost standard.", false)]
    [Arguments("c30-business-critical-db", "confirm", "Confirm the Business Critical tier is intended.", true)]
    [Arguments("c30-business-critical-db", "smaller_option", "Use the General Purpose tier.", true)]
    [Arguments("c30-business-critical-db", "smaller_option", "The Standard tier may be enough.", true)]
    [Arguments("c30-business-critical-db", "pricing", "Apply Azure Hybrid Benefit with license_type.", true)]
    [Arguments("c26-memory-vm", "confirm", "Confirm the tags are correct.", false)]
    [Arguments("c26-memory-vm", "confirm", "Confirm the workload needs four times the memory.", true)]
    [Arguments("c26-memory-vm", "confirm", "Confirm the VM size change is intentional.", true)]
    [Arguments("c26-memory-vm", "pricing", "Buy a reservation or a savings plan.", true)]
    [Arguments("c28-delete-cosmos", "confirm", "Confirm the data has been backed up.", false)]
    [Arguments("c28-delete-cosmos", "confirm", "Confirm the account is no longer needed.", true)]
    [Arguments("c28-delete-cosmos", "confirm", "Confirm the deletion is intended.", true)]
    [Arguments("c28-delete-cosmos", "guard", "Add a CanNotDelete resource lock.", true)]
    // "narrow" about the source, and any 21.
    [Arguments("c29-port-range-ssh", "narrow_ports", "Narrow the source to a known address range.", false)]
    [Arguments("c29-port-range-ssh", "narrow_ports", "Rule 21 of the policy applies.", false)]
    [Arguments("c29-port-range-ssh", "narrow_ports", "Remove internet access to the admin port.", false)]
    [Arguments("c29-port-range-ssh", "narrow_ports", "Exclude port 22 from the range.", true)]
    [Arguments("c29-port-range-ssh", "narrow_ports", "Allow only ports 20-21 for FTP.", true)]
    [Arguments("c29-port-range-ssh", "narrow_ports", "Remove the admin port from the destination port range.", true)]
    // "SAS" inside "disastrous".
    [Arguments("c22-storage-public-update", "alternative_access", "This could be a disastrous data leak.", false)]
    [Arguments("c22-storage-public-update", "alternative_access", "Share blobs with SAS tokens instead.", true)]
    [Arguments("c22-storage-public-update", "alternative_access", "Grant Storage Blob Data Reader through Entra ID.", true)]
    // A private endpoint isn't about clients or secure transfer.
    [Arguments("c31-storage-http", "clients", "Use a private endpoint.", false)]
    [Arguments("c31-storage-http", "secure_transfer", "Use a private endpoint.", false)]
    [Arguments("c31-storage-http", "clients", "Update clients to use HTTPS endpoints.", true)]
    [Arguments("c31-storage-http", "secure_transfer", "Require secure transfer.", true)]
    [Arguments("c24-owner-management-group", "scope", "Owner applies to every subscription under the management group.", false)]
    [Arguments("c24-owner-management-group", "scope", "Assign it at a single subscription or a resource group.", true)]
    [Arguments("c18-storage-transport", "tls", "Set min_tls_version to TLS1_2.", true)]
    [Arguments("c18-storage-transport", "network", "Set the firewall's default_action to Deny.", true)]
    [Arguments("c19-access-admin", "role", "Use Role Based Access Control Administrator with conditions.", true)]
    [Arguments("c21-rdp-internet", "just_in_time", "Use just-in-time VM access.", true)]
    [Arguments("c21-rdp-internet", "just_in_time", "Adjust the rule.", false)]
    [Arguments("c23-inline-ssh", "private_path", "Reach it through Azure Bastion.", true)]
    public async Task A_pattern_scores_its_concept_only(string caseName, string concept, string sentence, bool expected)
    {
        var score = SuggestionScore.Load(Path.Combine("cases", caseName))!;
        var pattern = score.Findings.Single().Concepts[concept];

        await Assert.That(SuggestionScore.Mentions(sentence, pattern)).IsEqualTo(expected);
    }

    [Test]
    public async Task Every_pattern_is_tested()
    {
        var tested = typeof(ConceptTests).GetMethod(nameof(A_pattern_scores_its_concept_only))!
            .GetCustomAttributes(typeof(ArgumentsAttribute), false)
            .Cast<ArgumentsAttribute>()
            .Select(arguments => ((string)arguments.Values[0]!, (string)arguments.Values[1]!))
            .ToHashSet();
        var all = Directory.GetDirectories("cases")
            .Select(directory => (Name: Path.GetFileName(directory), Score: SuggestionScore.Load(directory)))
            .Where(item => item.Score is not null)
            .SelectMany(item => item.Score!.Findings.SelectMany(finding => finding.Concepts.Keys).Select(concept => (item.Name, concept)))
            .ToList();

        // Every case has at least one sentence; the untested concepts are listed so they can be added.
        var untestedCases = all.Select(item => item.Name).Distinct().Where(name => !tested.Any(test => test.Item1 == name)).ToList();
        await Assert.That(untestedCases).IsEmpty();
    }
}
