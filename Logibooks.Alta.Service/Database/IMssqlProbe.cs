// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.Database;

internal interface IMssqlProbe
{
    Task ProbeAsync(RuntimeDatabaseTarget database, CancellationToken cancellationToken);
}
