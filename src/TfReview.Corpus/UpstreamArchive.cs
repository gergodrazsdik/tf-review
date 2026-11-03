using System.Formats.Tar;
using System.IO.Compression;

namespace TfReview.Corpus;

public sealed class ArchiveException(string message) : Exception(message);

/// <summary>A file from an upstream archive, by its path in the repo.</summary>
public sealed record UpstreamFile(string Path, byte[] Content);

/// <summary>
/// Reads a repo's tarball as a stream, keeping only the files asked for. Upstream is untrusted, so nothing is written to
/// disk, nothing is followed, and the limits below stop a run rather than letting it fill the runner's memory.
/// </summary>
public static class UpstreamArchive
{
    /// <summary>Files larger than this are counted and left out: no doc we want is anywhere near it.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    private const long MaxArchiveBytes = 16L * 1024 * 1024 * 1024;
    private const long MaxKeptBytes = 512 * 1024 * 1024;
    private const int MaxEntries = 500_000;

    private static readonly string[] LicenseFiles = ["LICENSE", "LICENSE.md", "LICENSE.txt"];

    /// <summary>What came out of an archive.</summary>
    public sealed record Contents(IReadOnlyList<UpstreamFile> Files, string? License, int TooLarge);

    /// <param name="archive">A gzipped tar from GitHub, whose entries sit under one top-level directory.</param>
    /// <param name="wanted">Whether to keep a file, by its path in the repo.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static async Task<Contents> ReadAsync(Stream archive, Func<string, bool> wanted, CancellationToken cancellationToken = default)
    {
        var files = new List<UpstreamFile>();
        string? license = null;
        var tooLarge = 0;
        long seen = 0;
        long kept = 0;
        var entries = 0;

        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            await using var reader = new TarReader(gzip);

            while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
            {
                seen += entry.Length;
                if (++entries > MaxEntries || seen > MaxArchiveBytes)
                {
                    throw new ArchiveException("The archive is larger than any source should be.");
                }

                // Links are skipped, not followed, and nothing here touches the disk.
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                {
                    continue;
                }

                var separator = entry.Name.IndexOf('/', StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                var path = entry.Name[(separator + 1)..];
                var isLicense = LicenseFiles.Contains(path, StringComparer.Ordinal);
                if (!isLicense && !wanted(path))
                {
                    continue;
                }

                if (entry.Length > MaxFileBytes)
                {
                    tooLarge++;
                    continue;
                }

                kept += entry.Length;
                if (kept > MaxKeptBytes)
                {
                    throw new ArchiveException("The files to import are larger than any source should be.");
                }

                using var content = new MemoryStream((int)entry.Length);
                await entry.DataStream.CopyToAsync(content, cancellationToken);
                if (isLicense)
                {
                    license ??= System.Text.Encoding.UTF8.GetString(content.ToArray());
                }

                if (wanted(path))
                {
                    files.Add(new UpstreamFile(path, content.ToArray()));
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new ArchiveException($"The archive can't be read: {e.GetType().Name}.");
        }

        return new Contents(files, license, tooLarge);
    }
}
