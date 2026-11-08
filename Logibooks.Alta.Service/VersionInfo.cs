// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Reflection;

namespace Logibooks.Alta;

public static class VersionInfo
{
    public static string AppVersion { get; } =
        typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString(3)
        ?? "Unknown";
}
