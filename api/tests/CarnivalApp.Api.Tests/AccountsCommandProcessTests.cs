using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The host command as vm-deploy.sh runs it, in its own process: stdout carries exactly the link
/// line, everything else goes to stderr, and the web server never starts.
/// </summary>
public sealed class AccountsCommandProcessTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carnival-cli-" + Guid.NewGuid().ToString("N"));

    public AccountsCommandProcessTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CarnivalApp.Api.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Database__Provider"] = "Sqlite";
        start.Environment["Database__SqlitePath"] = Path.Combine(_directory, "cli.db");
        start.Environment["Database__SeedSyntheticData"] = "false";
        start.Environment["Site__Address"] = "carnival.example.org";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Each_command_writes_only_its_line_to_stdout()
    {
        var (exit, output, error) = await RunAsync("accounts", "first-admin");

        Assert.True(exit == 0, $"exit {exit}; stderr: {error}");
        Assert.Matches(
            new Regex(@"^First administrator: open https://carnival\.example\.org/#/setup/[A-Za-z0-9_-]{43}  \(valid 24 hours\)\r?\n$"),
            output);
        Assert.DoesNotContain("listening", error, StringComparison.OrdinalIgnoreCase);

        var recovery = await RunAsync("accounts", "setup-link", "backup-admin");
        Assert.Equal(0, recovery.Exit);
        Assert.Matches(new Regex(@"^Setup link for backup-admin: open https://\S+  \(valid 24 hours\)\r?\n$"),
            recovery.Output);

        var again = await RunAsync("accounts", "first-admin");
        Assert.Equal(2, again.Exit);
        Assert.Equal("", again.Output);
        Assert.Contains("already exists", again.Error);

        var unknown = await RunAsync("accounts", "nonsense");
        Assert.Equal(1, unknown.Exit);
        Assert.Equal("", unknown.Output);
    }
}
