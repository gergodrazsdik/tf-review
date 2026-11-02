using System.Diagnostics;

namespace TfReview.Scanners;

public sealed record ProcessResult(int ExitCode, string Output);

public interface IProcessRunner
{
    /// <summary>Runs a program, and returns null if it can't start or doesn't finish in time.</summary>
    Task<ProcessResult?> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs scanners without the review's environment. A scanner reads files from a PR's plan and a config's
/// standards repo, so nothing it starts should find the review's secrets or its managed identity's endpoint.
/// </summary>
/// <param name="passedOn">Variables a scanner needs on top of the basics, which are the only others it gets.</param>
public sealed class ProcessRunner(params string[] passedOn) : IProcessRunner
{
    private static readonly string[] Basics =
    [
        "PATH", "HOME", "TMPDIR", "LANG", "LC_ALL", "PYTHONDONTWRITEBYTECODE",
        "SSL_CERT_DIR", "SSL_CERT_FILE", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
    ];

    public async Task<ProcessResult?> RunAsync(
        string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.Environment.Clear();
        foreach (var name in Basics.Concat(passedOn))
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                start.Environment[name] = value;
            }
        }

        // An argument list, never a command line: paths can't be read as options or shell syntax.
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        using (process)
        {
            try
            {
                // Read both streams at once: a full pipe would otherwise block the program forever.
                var output = process.StandardOutput.ReadToEndAsync(timer.Token);
                var errors = process.StandardError.ReadToEndAsync(timer.Token);
                await Task.WhenAll(output, errors);
                await process.WaitForExitAsync(timer.Token);
                return new ProcessResult(process.ExitCode, await output);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
        }
    }
}
