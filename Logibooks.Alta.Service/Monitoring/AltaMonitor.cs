// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Globalization;
using System.Net;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.Monitoring;

internal sealed class AltaMonitor(
    IAltaSource source,
    AltaCoreClient core,
    IRuntimeConfigurationProvider configurationProvider,
    TimeProvider clock,
    ILogger<AltaMonitor> logger,
    IMssqlProbe? databaseProbe = null)
{
    private const int RegistersPerRead = 64;
    private const int AssignmentsPerRequest = 200;
    private const int TimedOutReadsPerCycle = 3;
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);

    public void Clear() => _targets.Clear();

    public async Task RunAsync(RuntimeConfiguration configuration, CancellationToken token)
    {
        CoreOptions scope;
        try
        {
            scope = core.CaptureConfiguration();
            if (!SameCoreOptions(configuration.Core, scope))
                return;
        }
        catch (CoreConfigurationException exception)
        {
            logger.LogWarning("Could not start Alta monitoring: {Failure}",
                SensitiveDataSanitizer.Sanitize(exception.Message, configuration));
            return;
        }

        var names = configuration.Databases.Select(target => target.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _targets.Keys.Where(name => !names.Contains(name)).ToArray())
            _targets.Remove(name);

        var identity = new CoreIdentity(scope.BaseUrl.Trim().TrimEnd('/'), scope.Email.Trim());
        foreach (var target in configuration.Databases)
        {
            var revision = configuration.ScopeFor(target.Name);
            if (!_targets.TryGetValue(target.Name, out var existing) ||
                existing.Target != target || existing.Core != identity || existing.Revision != revision)
                _targets[target.Name] = new TargetState(target, identity, revision);
        }

        await Parallel.ForEachAsync(_targets.Values,
            new ParallelOptions { MaxDegreeOfParallelism = configuration.MaxConcurrentDatabaseProbes, CancellationToken = token },
            (target, cancellationToken) => new ValueTask(RunTargetAsync(target, configuration, scope, cancellationToken)));
    }

    private async Task RunTargetAsync(TargetState state, RuntimeConfiguration configuration, CoreOptions scope,
        CancellationToken token)
    {
        var target = state.Target;
        string[] cmrs;
        try
        {
            cmrs = (await RequestInScopeAsync(configuration, target,
                    current => core.RefreshWorklistAsync(current, target.Name, token), token))
                .Select(cmr => cmr.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Could not refresh Alta worklist for {DatabaseTarget}: {Failure}", target.Name,
                SensitiveDataSanitizer.Sanitize(AltaCoreClient.DescribeReadFailure(exception), configuration));
            if (IsCurrent(configuration, target, scope, token))
                await ProbeTargetAsync(target, configuration, token);
            return;
        }

        if (!IsCurrent(configuration, target, scope, token))
            return;
        var worklist = cmrs.ToHashSet(StringComparer.Ordinal);
        state.ReconcileMembership(worklist);
        logger.LogInformation("Alta worklist for {DatabaseTarget} contains {RegisterCount} register(s)", target.Name, cmrs.Length);
        if (cmrs.Length == 0)
            await ProbeTargetAsync(target, configuration, token);

        var start = Array.IndexOf(cmrs, state.ResumeAt);
        if (start > 0)
            cmrs = [.. cmrs[start..], .. cmrs[..start]];
        state.ResumeAt = null;
        var timeouts = 0;
        for (var offset = 0; offset < cmrs.Length;)
        {
            if (!IsCurrent(configuration, target, scope, token))
                return;

            var size = Math.Min(cmrs.Length - offset, state.ReadLimits.GetValueOrDefault(cmrs[offset], RegistersPerRead));
            var group = cmrs[offset..(offset + size)];
            var now = clock.GetUtcNow();
            var projection = new Dictionary<string, Dictionary<string, Delivery>>(StringComparer.Ordinal);
            AltaSourceRead read;
            try
            {
                var previous = group.Where(state.Registers.ContainsKey)
                    .ToDictionary(cmr => cmr, cmr => state.Registers[cmr].Snapshot, StringComparer.Ordinal);
                read = await source.ReadAsync(target, group, worklist, previous, snapshots =>
                {
                    foreach (var snapshot in snapshots)
                    {
                        state.Registers.TryGetValue(snapshot.Cmr, out var baseline);
                        projection.Add(snapshot.Cmr, CompareSnapshot(target, snapshot, baseline));
                    }
                    return projection.SelectMany(register => register.Value.Values
                        .Where(item => item.IsDue(now))
                        .Select(item => new AltaGoodsRequest(
                            new AltaGoodsKey(register.Key, item.Observation.ParcelNumber),
                            item.Observation.SourceParcels))).ToArray();
                }, token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Alta source read failed for {DatabaseTarget}, group starting at {Cmr}: {Failure}",
                    target.Name, group[0], SensitiveDataSanitizer.Sanitize(exception.Message, configuration));
                if (exception is TimeoutException or OperationCanceledException or SqlException { Number: -2 or 1222 })
                {
                    if (size > 1)
                    {
                        // Save both smaller groups while retaining set-based reads on the normal path.
                        var half = size / 2;
                        state.ReadLimits[group[0]] = half;
                        var second = group[half];
                        state.ReadLimits[second] = Math.Min(size - half,
                            state.ReadLimits.GetValueOrDefault(second, RegistersPerRead));
                    }
                    if (++timeouts == TimedOutReadsPerCycle)
                    {
                        state.ResumeAt = cmrs[(offset + size) % cmrs.Length];
                        return;
                    }
                    if (size > 1)
                        continue;
                }
                offset += size;
                continue;
            }

            offset += size;
            if (!IsCurrent(configuration, target, scope, token))
                return;

            foreach (var snapshot in read.Registers)
                state.Registers[snapshot.Cmr] = new RegisterState(snapshot, projection[snapshot.Cmr]);

            foreach (var snapshot in read.Registers)
            {
                var deliveries = projection[snapshot.Cmr].Values.Where(item => item.IsDue(now));
                if (!await SubmitAsync(configuration, scope, target, snapshot.Cmr, deliveries, read.Goods, token))
                {
                    if (IsCurrent(configuration, target, scope, token) &&
                        core.GetWorklistStatus(target.Name) is { IsStale: false, Cmrs: { } refreshed })
                        state.ReconcileMembership(refreshed.Select(cmr => cmr.Trim()));
                    return;
                }
            }
        }
    }

    private async Task ProbeTargetAsync(RuntimeDatabaseTarget target, RuntimeConfiguration configuration, CancellationToken token)
    {
        if (databaseProbe is null)
            return;
        try
        {
            await databaseProbe.ProbeAsync(target, token);
            logger.LogInformation("MSSQL probe succeeded for database target {DatabaseTarget}", target.Name);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("MSSQL probe failed for database target {DatabaseTarget}: {Failure}", target.Name,
                SensitiveDataSanitizer.Sanitize(exception.Message, configuration));
        }
    }

    private Dictionary<string, Delivery> CompareSnapshot(RuntimeDatabaseTarget target, AltaRegisterSnapshot snapshot,
        RegisterState? baseline)
    {
        var current = new Dictionary<string, Delivery>(StringComparer.Ordinal);
        var parcels = snapshot.Parcels.Values.Where(parcel => parcel.Number.Length > 0)
            .GroupBy(parcel => parcel.Number, StringComparer.Ordinal);
        foreach (var group in parcels)
        {
            var observation = ResolveAssignment(snapshot, group, out var failure);
            if (observation is null)
            {
                if (failure is not null)
                    logger.LogDebug("Deferred Alta {DatabaseTarget} / {Cmr} / {ParcelNumber}: {Reason}",
                        target.Name, snapshot.Cmr, group.Key, failure);
                continue;
            }

            if (baseline?.Deliveries.TryGetValue(group.Key, out var previous) == true &&
                observation.Continues(previous.Observation))
                current.Add(group.Key, previous with { Observation = observation });
            else
                current.Add(group.Key, new Delivery(observation));
        }
        return current;
    }

    private static Observation? ResolveAssignment(AltaRegisterSnapshot snapshot, IGrouping<string, AltaParcel> parcels,
        out string? failure)
    {
        failure = null;
        var linked = new List<(AltaParcel Parcel, AltaDeclaration Dec, AltaShipment Shipment, int Code)>();
        foreach (var parcel in parcels.OrderBy(parcel => parcel.Id))
        {
            if (parcel.DeclarationReference is null)
                continue;
            if (AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference) is not { } id ||
                !snapshot.Declarations.TryGetValue(id, out var dec))
            {
                failure = "Unresolved DEC reference";
                return null;
            }
            if (dec.Code is null ||
                !short.TryParse(dec.Procedure, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                !int.TryParse(parcel.Decision, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ||
                code is < 1 or > 99 ||
                dec.Shipments?.TryGetValue(parcel.Id, out var shipment) != true || shipment is null ||
                shipment.Number != parcel.Number)
            {
                failure = $"Incomplete customs data for IndPost {parcel.Id}";
                return null;
            }
            linked.Add((parcel, dec, shipment, code));
        }
        if (linked.Count == 0)
            return null;

        var first = linked[0];
        if (linked.Any(item => item.Dec.Id != first.Dec.Id || item.Code != first.Code ||
                               !item.Shipment.Payments.SequenceEqual(first.Shipment.Payments)))
        {
            failure = "Conflicting DEC or decision data for this parcel number";
            return null;
        }

        var knownTimes = linked.Select(item => item.Shipment.DecisionAt).OfType<DateTimeOffset>().Distinct().Take(2).ToArray();
        if (knownTimes.Length > 1)
        {
            failure = "Conflicting customs decision times for this parcel number";
            return null;
        }

        var time = linked.All(item => item.Shipment.DecisionAt.HasValue) ? first.Shipment.DecisionAt : null;
        var weights = linked.Select(item => item.Shipment.WeightKg ?? item.Parcel.WeightKg).Distinct().Take(2).ToArray();
        return new Observation(parcels.Key, first.Dec.Id, first.Dec.Code!,
            new AltaDecision(first.Code, first.Dec.Procedure!, time), first.Shipment.Payments,
            weights.Length == 1 ? weights[0] : null, linked.Select(item => item.Parcel.Id).ToHashSet());
    }

    private async Task<bool> SubmitAsync(RuntimeConfiguration configuration, CoreOptions scope, RuntimeDatabaseTarget target,
        string cmr, IEnumerable<Delivery> deliveries, IReadOnlyDictionary<AltaGoodsKey, AltaGoodsLookup> goods,
        CancellationToken token)
    {
        foreach (var group in deliveries.GroupBy(item => item.Observation.DecCode, StringComparer.Ordinal))
        {
            var ready = new List<(Delivery Delivery, AltaAssignment Assignment)>();
            foreach (var delivery in group.OrderBy(item => item.Observation.ParcelNumber, StringComparer.Ordinal))
            {
                var item = delivery.Observation;
                if (!goods.TryGetValue(new AltaGoodsKey(cmr, item.ParcelNumber), out var invoice) ||
                    invoice.Items is not { Count: > 0 } items)
                {
                    logger.LogWarning("Deferred Alta {DatabaseTarget} / {Cmr} / {ParcelNumber}: {Reason}",
                        target.Name, cmr, item.ParcelNumber, invoice?.Failure ?? "E2IList lookup returned no goods");
                    delivery.Defer(clock.GetUtcNow());
                    continue;
                }
                ready.Add((delivery, new AltaAssignment(item.ParcelNumber, items, item.Decision, item.Payments, item.WeightKg)));
            }

            foreach (var batch in ready.Chunk(AssignmentsPerRequest))
            {
                if (!IsCurrent(configuration, target, scope, token))
                    return false;
                try
                {
                    var payload = new AltaBatch(cmr, group.Key, batch.Select(item => item.Assignment).ToArray());
                    var result = await RequestInScopeAsync(configuration, target,
                        current => core.SubmitAsync(current, target.Name, payload, token), token);
                    if (!IsCurrent(configuration, target, scope, token))
                        return false;
                    foreach (var item in result.Results)
                    {
                        var delivery = batch[item.Index].Delivery;
                        delivery.Handled = true;
                        if (item.Outcome == "error")
                        {
                            logger.LogWarning("Core rejected Alta {DatabaseTarget} / {Cmr} / {ParcelNumber}: {Code} {Message}",
                                target.Name, cmr, delivery.Observation.ParcelNumber, item.Code,
                                SensitiveDataSanitizer.Sanitize(item.Message ?? "", configuration));
                        }
                    }
                    logger.LogInformation("Core saved {DecisionCount} Alta decisions for {DatabaseTarget} / {Cmr} / {Dec}",
                        result.AcceptedCount, target.Name, cmr, group.Key);
                }
                catch (CoreRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict &&
                    exception.Code is "AltaCmrUnavailable" or "AltaScopeChanged")
                {
                    logger.LogWarning("Core worklist changed for {DatabaseTarget} / {Cmr}: {Code}",
                        target.Name, cmr, exception.Code);
                    return false;
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    if (!IsCurrent(configuration, target, scope, token))
                        return false;
                    logger.LogWarning("Alta submission failed for {DatabaseTarget} / {Cmr}: {Failure}", target.Name, cmr,
                        SensitiveDataSanitizer.Sanitize(AltaCoreClient.DescribeReadFailure(exception), configuration));
                    foreach (var item in batch)
                        item.Delivery.Defer(clock.GetUtcNow());
                }
            }
        }
        return true;
    }

    private async Task<T> RequestInScopeAsync<T>(RuntimeConfiguration configuration, RuntimeDatabaseTarget target,
        Func<CoreOptions, Task<T>> request, CancellationToken token)
    {
        var scope = Capture();
        try
        {
            return await request(scope);
        }
        catch (CoreConfigurationException) when (IsCurrent(configuration, target, scope, token))
        {
            // Reload can replace the options object with an equivalent instance while authenticating.
            // CoreConfigurationException occurs before dispatch, or while authenticating again after an explicit 401.
            return await request(Capture());
        }

        CoreOptions Capture()
        {
            var current = core.CaptureConfiguration();
            if (!SameCoreOptions(configuration.Core, current) || !IsCurrent(configuration, target, current, token))
                throw new CoreConfigurationException("Alta monitoring configuration changed.");
            return current;
        }
    }

    private bool IsCurrent(RuntimeConfiguration configuration, RuntimeDatabaseTarget target, CoreOptions scope,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var current = configurationProvider.GetValidated();
            return current.Service.Enabled && SameCoreOptions(current.Core, scope) &&
                current.Databases.Contains(target) && current.ScopeFor(target.Name) == configuration.ScopeFor(target.Name);
        }
        catch (RuntimeConfigurationException)
        {
            return false;
        }
    }

    internal static bool SameRuntime(RuntimeConfiguration left, RuntimeConfiguration right) =>
        left.MaxConcurrentDatabaseProbes == right.MaxConcurrentDatabaseProbes &&
        SameCoreOptions(left.Core, right.Core) && left.Databases.SequenceEqual(right.Databases) &&
        left.Databases.All(target => left.ScopeFor(target.Name) == right.ScopeFor(target.Name));

    private static bool SameCoreOptions(CoreOptions left, CoreOptions right) =>
        left.Enabled == right.Enabled && left.BaseUrl.Trim() == right.BaseUrl.Trim() &&
        left.Email.Trim() == right.Email.Trim() && left.Password == right.Password &&
        left.RequestTimeoutSeconds == right.RequestTimeoutSeconds && left.JwtRefreshSkewMinutes == right.JwtRefreshSkewMinutes;

    private sealed record CoreIdentity(string BaseUrl, string Writer);

    private sealed record TargetState(RuntimeDatabaseTarget Target, CoreIdentity Core, (long Global, long Target) Revision)
    {
        public Dictionary<string, RegisterState> Registers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ReadLimits { get; } = new(StringComparer.Ordinal);
        public string? ResumeAt { get; set; }

        public void ReconcileMembership(IEnumerable<string> cmrs)
        {
            var membership = cmrs.ToHashSet(StringComparer.Ordinal);
            foreach (var retired in Registers.Keys.Where(cmr => !membership.Contains(cmr)).ToArray())
                Registers.Remove(retired);
            foreach (var retired in ReadLimits.Keys.Where(cmr => !membership.Contains(cmr)).ToArray())
                ReadLimits.Remove(retired);
        }
    }

    private sealed record RegisterState(AltaRegisterSnapshot Snapshot, Dictionary<string, Delivery> Deliveries);

    private sealed record Observation(string ParcelNumber, Guid DecId, string DecCode, AltaDecision Decision,
        IReadOnlyList<AltaPayment> Payments, decimal? WeightKg, IReadOnlySet<Guid> SourceParcels)
    {
        public bool Continues(Observation previous) =>
            DecId == previous.DecId && DecCode == previous.DecCode && Decision == previous.Decision &&
            Payments.SequenceEqual(previous.Payments) && WeightKg == previous.WeightKg &&
            SourceParcels.IsSubsetOf(previous.SourceParcels);
    }

    private sealed record Delivery(Observation Observation)
    {
        private DateTimeOffset _nextAttempt;
        private int _retries;
        public bool Handled { get; set; }
        public bool IsDue(DateTimeOffset now) => !Handled && now >= _nextAttempt;

        public void Defer(DateTimeOffset now)
        {
            _nextAttempt = now.AddMinutes(Math.Min(60, 1 << _retries));
            _retries = Math.Min(6, _retries + 1);
        }
    }
}
