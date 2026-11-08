// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Logibooks.Alta.Installation;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Logging.EventLog;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1)
        {
            if (string.Equals(args[0], "/Version", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(VersionInfo.AppVersion);
                return 0;
            }

            if (string.Equals(args[0], "/Install", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "/Uninstall", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "/Rollback", StringComparison.OrdinalIgnoreCase))
            {
                return await RunInstallerCommandAsync(args[0]);
            }
        }

        await BuildHost(args).RunAsync();
        return 0;
    }

    internal static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Configuration.AddJsonFile(
            ConfigurationPaths.GetConfigurationPath(),
            optional: true,
            reloadOnChange: true);
        // The external operational file is added after the host defaults, so re-add
        // environment variables last to preserve standard container override behavior.
        builder.Configuration.AddEnvironmentVariables();

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = ServiceConstants.ServiceName);
            builder.Logging.AddEventLog(new EventLogSettings
            {
                LogName = "Application",
                SourceName = ServiceConstants.DisplayName
            });
        }

        builder.Services
            .Configure<ServiceOptions>(builder.Configuration.GetSection(ServiceOptions.SectionName))
            .Configure<CoreOptions>(builder.Configuration.GetSection(CoreOptions.SectionName))
            .Configure<DatabaseMonitoringOptions>(
                builder.Configuration.GetSection(DatabaseMonitoringOptions.SectionName));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRuntimeConfigurationProvider, RuntimeConfigurationProvider>();
        builder.Services.AddSingleton<IConfigurationReloadSignal, ConfigurationReloadSignal>();
        builder.Services.AddSingleton<IDatabaseConnectionFactory, SqlConnectionFactory>();
        builder.Services.AddSingleton<IDatabaseRetryDelay, DatabaseRetryDelay>();
        builder.Services.AddSingleton<IMssqlProbe, MssqlProbe>();
        builder.Services.AddSingleton<IRetryDelay, SystemRetryDelay>();
        builder.Services.AddSingleton<IHttpRetryExecutor, HttpRetryExecutor>();
        builder.Services.AddSingleton<ICoreSessionManager, CoreSessionManager>();
        builder.Services.AddTransient<JwtSessionHandler>();
        builder.Services.AddSingleton<ICoreApiProbe, CoreApiProbe>();
        builder.Services.AddHostedService<ProbeWorker>();

        builder.Services
            .AddHttpClient(HttpClientNames.Authentication, client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services
            .AddHttpClient(HttpClientNames.Api, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<JwtSessionHandler>();

        return builder.Build();
    }

    private static async Task<int> RunInstallerCommandAsync(string command)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Service installation commands are supported only on Windows.");
            }

            var executablePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Logibooks.Alta.Adapter.exe");
            var configurationDirectory = ConfigurationPaths.GetDirectoryPath();
            var configurationPath = ConfigurationPaths.GetConfigurationPath();
            var templatePath = Path.Combine(
                AppContext.BaseDirectory,
                ServiceConstants.ConfigurationTemplateFileName);

            var provisioner = new ConfigurationProvisioner(
                templatePath,
                configurationDirectory,
                configurationPath,
                new WindowsFileAclProtector());
            var installer = new WindowsServiceInstaller(
                executablePath,
                provisioner,
                new ProcessCommandRunner(),
                new WindowsServiceStopWaiter());

            if (string.Equals(command, "/Install", StringComparison.OrdinalIgnoreCase))
            {
                await installer.InstallAsync(CancellationToken.None);
            }
            else
            {
                await installer.UninstallAsync(CancellationToken.None);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Installer command failed: {exception.Message}");
            return 1;
        }
    }
}
