namespace TfReview.Rag;

/// <summary>A corpus already on disk, for tests and local runs. It ignores the repository and commit in the config.</summary>
public sealed class LocalDirectorySource(string directory) : IStandardsSource
{
    private static readonly string[] KeptExtensions = [".md", ".rego"];

    public async Task FetchAsync(TfReviewConfig config, string targetDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            throw new StandardsFetchException("The local standards directory doesn't exist.");
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!KeptExtensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(targetDirectory, Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = File.OpenRead(file);
            await using var destination = File.Create(target);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }
}
