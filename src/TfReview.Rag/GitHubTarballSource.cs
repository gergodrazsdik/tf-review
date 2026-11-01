using System.Formats.Tar;
using System.IO.Compression;

namespace TfReview.Rag;

public sealed class StandardsFetchException(string message) : Exception(message);

/// <summary>
/// Downloads the standards corpus at an exact commit, because the image has no git. The repository comes
/// from a config, so the archive is treated as hostile: it can't write outside the target directory, can't
/// create links, and can't exhaust the disk or memory. Only documents and policies are unpacked.
/// </summary>
public sealed class GitHubTarballSource(HttpClient http) : IStandardsSource
{
    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private const long MaxUnpackedBytes = 50 * 1024 * 1024;
    private const int MaxEntries = 5_000;

    private static readonly string[] KeptExtensions = [".md", ".rego"];

    public async Task FetchAsync(TfReviewConfig config, string targetDirectory, CancellationToken cancellationToken = default)
    {
        using var archive = await DownloadWithRetryAsync(config, cancellationToken);
        await UnpackAsync(archive, targetDirectory, cancellationToken);
    }

    /// <summary>Unpacks a gzipped tar from GitHub, whose entries all sit under one top-level directory.</summary>
    internal static async Task UnpackAsync(Stream archive, string targetDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        long unpacked = 0;
        var entries = 0;
        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            await using var reader = new TarReader(gzip);

            while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
            {
                if (++entries > MaxEntries)
                {
                    throw new StandardsFetchException("The standards archive has too many entries.");
                }

                // Links are skipped, not followed: one could point a later entry outside the target.
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                var relative = RelativePath(entry.Name);
                if (relative is null || !KeptExtensions.Contains(Path.GetExtension(relative), StringComparer.Ordinal))
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root, StringComparison.Ordinal))
                {
                    throw new StandardsFetchException("The standards archive tries to write outside its directory.");
                }

                if (entry.DataStream is not { } data)
                {
                    continue;
                }

                unpacked += entry.Length;
                if (entry.Length < 0 || unpacked > MaxUnpackedBytes)
                {
                    throw new StandardsFetchException("The standards archive is too large.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                await data.CopyToAsync(file, cancellationToken);
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or IOException)
        {
            throw new StandardsFetchException("The standards archive isn't valid.");
        }
    }

    private async Task<MemoryStream> DownloadWithRetryAsync(TfReviewConfig config, CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadAsync(config, cancellationToken);
        }
        catch (Exception e) when (IsTransient(e, cancellationToken))
        {
            try
            {
                return await DownloadAsync(config, cancellationToken);
            }
            catch (Exception retry) when (IsTransient(retry, cancellationToken))
            {
                throw new StandardsFetchException("Downloading the standards failed.");
            }
        }
    }

    private async Task<MemoryStream> DownloadAsync(TfReviewConfig config, CancellationToken cancellationToken)
    {
        // Both parts passed the config's patterns, and the commit is a full SHA, so this can't move.
        var url = $"{config.Repository}/tar.gz/{config.Commit}";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new StandardsFetchException($"Downloading the standards failed with HTTP {(int)response.StatusCode}.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var archive = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (archive.Length + read > MaxDownloadBytes)
            {
                throw new StandardsFetchException("The standards download is too large.");
            }

            archive.Write(buffer, 0, read);
        }

        archive.Position = 0;
        return archive;
    }

    // A timeout is an OperationCanceledException that the caller didn't ask for, and the body can also fail
    // part way through as an IOException. Any of these is worth one more try, and then a clear answer.
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Removes GitHub's top-level directory. Null for the directory itself and absolute or parent paths.</summary>
    private static string? RelativePath(string name)
    {
        if (name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new StandardsFetchException("The standards archive tries to write outside its directory.");
        }

        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains(".."))
        {
            throw new StandardsFetchException("The standards archive tries to write outside its directory.");
        }

        return parts.Length < 2 ? null : Path.Combine(parts[1..]);
    }
}
