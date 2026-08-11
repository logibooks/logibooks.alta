using System.Diagnostics;

namespace Logibooks.Alta.Installation;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
}

internal interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {executable}.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }
}
