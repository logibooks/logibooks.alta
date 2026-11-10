// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Serilog;
using Serilog.Extensions.Logging;

namespace Logibooks.Alta.Monitoring;

internal static class RollingFileLogging
{
    public static void AddRollingFile(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string configurationDirectory)
    {
        var settings = configuration.GetSection("Logging:File");
        if (!settings.GetValue("Enabled", OperatingSystem.IsWindows()))
        {
            return;
        }

        var directory = Path.GetFullPath(settings["Directory"] ?? ServiceConstants.LogDirectoryName, configurationDirectory);
        var path = Path.Combine(directory, "alta-.log");
        logging.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(
            new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    path,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger(),
            dispose: true));
    }
}
