// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.ServiceProcess;
using System.Runtime.Versioning;

namespace Logibooks.Alta.Installation;

internal interface IWindowsServiceInstaller
{
    Task InstallAsync(CancellationToken cancellationToken);
    Task UninstallAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsServiceInstaller(
    string executablePath,
    IConfigurationProvisioner configurationProvisioner,
    ICommandRunner commandRunner,
    IServiceStopWaiter serviceStopWaiter) : IWindowsServiceInstaller
{
    private const string ScExecutable = "sc.exe";

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        configurationProvisioner.EnsureConfiguration();
        await EnsureEventLogSourceAsync(cancellationToken);

        var quotedExecutablePath = $"\"{executablePath}\"";

        var exists = await ServiceExistsAsync(cancellationToken);
        if (!exists)
        {
            await RunRequiredAsync(
                [
                    "create", ServiceConstants.ServiceName,
                    "binPath=", quotedExecutablePath,
                    "start=", "delayed-auto",
                    "obj=", "NT AUTHORITY\\LocalService",
                    "DisplayName=", ServiceConstants.DisplayName
                ],
                cancellationToken);
        }

        await RunRequiredAsync(
            [
                "config", ServiceConstants.ServiceName,
                "binPath=", quotedExecutablePath,
                "start=", "delayed-auto",
                "obj=", "NT AUTHORITY\\LocalService",
                "DisplayName=", ServiceConstants.DisplayName
            ],
            cancellationToken);
        await RunRequiredAsync(
            ["description", ServiceConstants.ServiceName, ServiceConstants.Description],
            cancellationToken);
        await RunRequiredAsync(
            [
                "failure", ServiceConstants.ServiceName,
                "reset=", "86400",
                "actions=", "restart/5000/restart/15000/restart/60000"
            ],
            cancellationToken);
        await RunRequiredAsync(
            ["failureflag", ServiceConstants.ServiceName, "1"],
            cancellationToken);

        var startResult = await commandRunner.RunAsync(
            ScExecutable,
            ["start", ServiceConstants.ServiceName],
            cancellationToken);
        if (startResult.ExitCode != 0 && !IsServiceError(startResult, 1056))
        {
            throw CreateCommandException("start", startResult.ExitCode);
        }
    }

    public async Task UninstallAsync(CancellationToken cancellationToken)
    {
        if (!await ServiceExistsAsync(cancellationToken))
        {
            return;
        }

        var stopResult = await commandRunner.RunAsync(
            ScExecutable,
            ["stop", ServiceConstants.ServiceName],
            cancellationToken);
        if (stopResult.ExitCode != 0 &&
            !IsServiceError(stopResult, 1060) &&
            !IsServiceError(stopResult, 1062))
        {
            throw CreateCommandException("stop", stopResult.ExitCode);
        }

        if (!IsServiceError(stopResult, 1060))
        {
            serviceStopWaiter.WaitForStopped(TimeSpan.FromSeconds(30));
        }

        var deleteResult = await commandRunner.RunAsync(
            ScExecutable,
            ["delete", ServiceConstants.ServiceName],
            cancellationToken);
        if (deleteResult.ExitCode != 0 &&
            !IsServiceError(deleteResult, 1060) &&
            !IsServiceError(deleteResult, 1072))
        {
            throw CreateCommandException("delete", deleteResult.ExitCode);
        }
    }

    private async Task<bool> ServiceExistsAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            ScExecutable,
            ["query", ServiceConstants.ServiceName],
            cancellationToken);
        if (result.ExitCode == 0)
        {
            return true;
        }

        if (IsServiceError(result, 1060))
        {
            return false;
        }

        throw CreateCommandException("query", result.ExitCode);
    }

    private async Task EnsureEventLogSourceAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            "eventcreate.exe",
            [
                "/L", "APPLICATION",
                "/T", "INFORMATION",
                "/SO", ServiceConstants.DisplayName,
                "/ID", "1",
                "/D", $"{ServiceConstants.DisplayName} event source initialized."
            ],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException("eventcreate", result.ExitCode);
        }
    }

    private async Task RunRequiredAsync(
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(ScExecutable, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(arguments.First(), result.ExitCode);
        }
    }

    private static InvalidOperationException CreateCommandException(string command, int exitCode) =>
        new($"Windows Service Control Manager command '{command}' failed with exit code {exitCode}.");

    private static bool IsServiceError(CommandResult result, int errorCode) =>
        result.ExitCode == errorCode ||
        result.CombinedOutput.Contains(errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

internal interface IServiceStopWaiter
{
    void WaitForStopped(TimeSpan timeout);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceStopWaiter : IServiceStopWaiter
{
    public void WaitForStopped(TimeSpan timeout)
    {
        using var service = new ServiceController(ServiceConstants.ServiceName);
        service.Refresh();
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
    }
}
