// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

namespace Logibooks.Alta.Configuration;

internal sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public bool Enabled { get; set; }
    public string MonitoringCron { get; set; } = "0 0 8-20/2 * * ?";
    public string MonitoringTimeZone { get; set; } = "Europe/Moscow";
}
