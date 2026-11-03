namespace TfReview.Rag.Tests;

public class LockFileTests
{
    private const string OpenTofu = """
        # This file is maintained automatically by "tofu init".

        provider "registry.opentofu.org/hashicorp/random" {
          version = "3.7.2"
        }

        provider "registry.opentofu.org/hashicorp/azurerm" {
          version     = "5.12.0"
          constraints = "~> 5.12"
          hashes = [
            "h1:abc=",
          ]
        }
        """;

    [Test]
    public async Task Reads_azurerms_version_and_not_another_providers()
    {
        await Assert.That(LockFile.AzurermVersion(OpenTofu)).IsEqualTo("5.12.0");
    }

    [Test]
    public async Task Reads_a_terraform_lock_file_too()
    {
        var terraform = OpenTofu.Replace("registry.opentofu.org", "registry.terraform.io", StringComparison.Ordinal);

        await Assert.That(LockFile.AzurermVersion(terraform)).IsEqualTo("5.12.0");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("provider \"registry.opentofu.org/hashicorp/aws\" {\n  version = \"6.0.0\"\n}\n")]
    [Arguments("provider \"registry.opentofu.org/someone/azurerm\" {\n  version = \"5.12.0\"\n}\n")]
    public async Task A_lock_file_without_azurerm_gives_no_version(string? lockFile)
    {
        await Assert.That(LockFile.AzurermVersion(lockFile)).IsNull();
    }
}
