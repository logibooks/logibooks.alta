// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

namespace Logibooks.Alta.Configuration;

internal sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
}
