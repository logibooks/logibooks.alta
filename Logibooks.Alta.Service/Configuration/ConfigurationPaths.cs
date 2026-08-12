namespace Logibooks.Alta.Configuration;

internal static class ConfigurationPaths
{
    public static string GetDirectoryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ServiceConstants.ConfigurationDirectoryName);

    public static string GetConfigurationPath() => Path.Combine(
        GetDirectoryPath(),
        ServiceConstants.ConfigurationFileName);
}
