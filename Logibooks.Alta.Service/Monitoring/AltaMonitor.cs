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
                read = await source.ReadAsync(target, group, snapshots =>
                {
                    foreach (var snapshot in snapshots)
                    {
                        state.Registers.TryGetValue(snapshot.Cmr, out var baseline);
                        projection.Add(snapshot.Cmr, CompareSnapshot(target, snapshot, baseline));
                    }
                    return projection.Values.SelectMany(deliveries => deliveries.Values)
                        .Where(item => item.IsDue(now))
                        .GroupBy(item => item.Observation.Declaration.Id)
                        .ToDictionary(declaration => declaration.Key,
                            declaration => declaration.SelectMany(item => item.Observation.Parcels)
                                .Select(parcel => parcel.Id).ToHashSet());
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
                if (!await SubmitAsync(configuration, scope, target, snapshot.Cmr, deliveries, read, token))
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
            var observation = ResolveObservation(snapshot, group, out var failure);
            if (observation is null)
            {
                if (failure is not null)
                    logger.LogDebug("Deferred Alta {DatabaseTarget} / {Cmr} / {ParcelNumber}: {Reason}",
                        target.Name, snapshot.Cmr, group.Key, failure);
                continue;
            }

            // Existing DEC records are immutable for polling. A new linked DEC or a changed linked IndPost row
            // queues this number. A row that disappears or unlinks does not by itself create a Core assignment.
            if (baseline?.Deliveries.TryGetValue(group.Key, out var previous) == true &&
                observation.Declaration.Id == previous.Observation.Declaration.Id &&
                observation.Parcels.All(parcel => baseline.Snapshot.Parcels.GetValueOrDefault(parcel.Id) == parcel))
                current.Add(group.Key, previous with { Observation = observation });
            else
                current.Add(group.Key, new Delivery(observation));
        }
        return current;
    }

    private static Observation? ResolveObservation(AltaRegisterSnapshot snapshot, IGrouping<string, AltaParcel> parcels,
        out string? failure)
    {
        failure = null;
        var linked = new List<AltaParcel>();
        AltaDeclaration? declaration = null;
        var decisionCode = 0;
        foreach (var parcel in parcels.OrderBy(parcel => parcel.Id))
        {
            if (parcel.DeclarationReference is null)
                continue;
            if (AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference) is not { } id)
            {
                failure = string.IsNullOrWhiteSpace(parcel.DeclarationReference)
                    ? $"Missing DEC reference in IndPostList.REESTRGID for IndPost {parcel.Id}"
                    : $"Invalid DEC reference in IndPostList.REESTRGID for IndPost {parcel.Id} (expected a DEC GUID)";
                return null;
            }
            if (!snapshot.Declarations.TryGetValue(id, out var dec))
            {
                failure = $"DEC {id} referenced by IndPostList.REESTRGID for IndPost {parcel.Id} is missing from ExpressCargoDeclarationList for this CMR";
                return null;
            }
            if (!int.TryParse(parcel.Decision, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ||
                code is < 1 or > 99)
            {
                failure = string.IsNullOrWhiteSpace(parcel.Decision)
                    ? $"Missing customs decision code in IndPostList.DECISION for IndPost {parcel.Id}"
                    : $"Invalid customs decision code in IndPostList.DECISION for IndPost {parcel.Id} (expected an integer from 1 to 99)";
                return null;
            }
            if (declaration is not null && declaration.Id != dec.Id)
            {
                failure = $"Conflicting IndPostList.REESTRGID links for IndPost {linked[0].Id} and {parcel.Id}";
                return null;
            }
            if (declaration is not null && decisionCode != code)
            {
                failure = $"Conflicting IndPostList.DECISION codes for IndPost {linked[0].Id} and {parcel.Id}";
                return null;
            }
            declaration = dec;
            decisionCode = code;
            linked.Add(parcel);
        }
        return declaration is null ? null : new Observation(parcels.Key, declaration, decisionCode, linked);
    }

    private AltaAssignment? BuildAssignment(string databaseTarget, string cmr, Observation observation,
        AltaSourceRead read, out string? failure)
    {
        var declaration = observation.Declaration;
        if (string.IsNullOrWhiteSpace(declaration.Number))
        {
            failure = $"Missing DEC number for DEC {declaration.Id} (ExpressCargoDeclarationList.RegNum and Docs.REGNUM are both blank or absent)";
            return null;
        }
        if (!short.TryParse(declaration.Procedure, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            failure = string.IsNullOrWhiteSpace(declaration.Procedure)
                ? $"Missing customs procedure in ExpressCargoDeclarationList.CustomsModeCode for DEC {declaration.Id}"
                : $"Invalid customs procedure in ExpressCargoDeclarationList.CustomsModeCode for DEC {declaration.Id} (expected an integer from 0 to {short.MaxValue})";
            return null;
        }

        // Use shipment details only for a queued assignment. XML values never participate in snapshot comparison.
        if (!read.DeclarationDetails.TryGetValue(declaration.Id, out var details))
        {
            failure = $"Missing Docs record for DEC {declaration.Id} with DOCTYP ExpressCargoDeclaration";
            return null;
        }
        if (details.Failure is { } documentFailure)
        {
            failure = $"DEC {declaration.Id}: {documentFailure}";
            return null;
        }

        var linked = new List<(AltaParcel Parcel, AltaShipment Shipment)>();
        foreach (var parcel in observation.Parcels)
        {
            if (!details.Shipments.TryGetValue(parcel.Id, out var result))
            {
                failure = $"DEC {declaration.Id} XML has no GoodsShipment_HouseShipment with IndGid matching IndPost {parcel.Id}";
                return null;
            }
            if (result.Shipment is not { } shipment)
            {
                failure = $"DEC {declaration.Id} XML for IndPost {parcel.Id}: {result.Failure}";
                return null;
            }
            if (shipment.Number != parcel.Number)
            {
                var reason = string.IsNullOrEmpty(shipment.Number)
                    ? "Missing DEC XML field HouseWaybillDetails_PrDocumentNumber"
                    : "DEC XML HouseWaybillDetails_PrDocumentNumber does not match IndPostList.Num";
                logger.LogDebug("Alta {DatabaseTarget} / {Cmr} / {ParcelNumber}: {Reason} for DEC {DecId}, IndPost {IndPostId}",
                    databaseTarget, cmr, parcel.Number, reason, declaration.Id, parcel.Id);
            }
            linked.Add((parcel, shipment));
        }

        var first = linked[0];
        if (linked.Any(item => !item.Shipment.Payments.SequenceEqual(first.Shipment.Payments)))
        {
            failure = $"Conflicting DEC XML CustomsPaymentDetails across shipments for DEC {declaration.Id}";
            return null;
        }
        var knownTimes = linked.Select(item => item.Shipment.DecisionAt).OfType<DateTimeOffset>().Distinct().Take(2).ToArray();
        if (knownTimes.Length > 1)
        {
            failure = $"Conflicting DEC XML TaxBase_DecisionDate values across shipments for DEC {declaration.Id}";
            return null;
        }

        if (linked.Any(item => !item.Shipment.Goods.SequenceEqual(first.Shipment.Goods)))
        {
            failure = $"Conflicting DEC XML GoodsItemDetails across shipments for DEC {declaration.Id}";
            return null;
        }

        var time = linked.All(item => item.Shipment.DecisionAt.HasValue) ? first.Shipment.DecisionAt : null;
        var weights = linked.Select(item => item.Shipment.WeightKg ?? item.Parcel.WeightKg).Distinct().Take(2).ToArray();
        failure = null;
        return new AltaAssignment(observation.ParcelNumber, first.Shipment.Goods,
            new AltaDecision(observation.DecisionCode, declaration.Procedure!, time), first.Shipment.Payments,
            weights.Length == 1 ? weights[0] : null);
    }

    private async Task<bool> SubmitAsync(RuntimeConfiguration configuration, CoreOptions scope, RuntimeDatabaseTarget target,
        string cmr, IEnumerable<Delivery> deliveries, AltaSourceRead read,
        CancellationToken token)
    {
        foreach (var group in deliveries.GroupBy(item => item.Observation.Declaration.Number, StringComparer.Ordinal))
        {
            var ready = new List<(Delivery Delivery, AltaAssignment Assignment)>();
            foreach (var delivery in group.OrderBy(item => item.Observation.ParcelNumber, StringComparer.Ordinal))
            {
                var assignment = BuildAssignment(target.Name, cmr, delivery.Observation, read, out var failure);
                if (assignment is null)
                {
                    logger.LogWarning("Deferred Alta {DatabaseTarget} / {Cmr} / {ParcelNumber}: {Reason}",
                        target.Name, cmr, delivery.Observation.ParcelNumber, failure);
                    delivery.Defer(clock.GetUtcNow());
                    continue;
                }
                ready.Add((delivery, assignment));
            }

            foreach (var batch in ready.Chunk(AssignmentsPerRequest))
            {
                if (!IsCurrent(configuration, target, scope, token))
                    return false;
                try
                {
                    var payload = new AltaBatch(cmr, group.Key!, batch.Select(item => item.Assignment).ToArray());
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

    private sealed record Observation(string ParcelNumber, AltaDeclaration Declaration, int DecisionCode,
        IReadOnlyList<AltaParcel> Parcels);

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
