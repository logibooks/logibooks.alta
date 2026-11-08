// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

namespace Logibooks.Alta.Configuration;

internal static class ConfigurationPaths
{
    public const string ConfigurationPathEnvironmentVariable = "LOGIBOOKS_ALTA_CONFIG_PATH";

    public static string GetDirectoryPath() => Path.GetDirectoryName(GetConfigurationPath())
        ?? throw new InvalidOperationException("The configuration path has no parent directory.");

    public static string GetConfigurationPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(ConfigurationPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var directory = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ServiceConstants.ConfigurationDirectoryName)
            : "/etc/logibooks/alta";
        return Path.Combine(directory, ServiceConstants.ConfigurationFileName);
    }
}
