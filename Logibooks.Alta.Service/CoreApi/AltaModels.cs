// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Text.Json.Serialization;

namespace Logibooks.Alta.CoreApi;

internal sealed record AltaWorklist([property: JsonRequired] IReadOnlyList<string> Cmrs);

internal sealed record AltaBatch(string CmrNumber, string DecCode, IReadOnlyList<AltaAssignment> Assignments);

internal sealed record AltaAssignment(
    string ParcelNumber, string? Uin, int Occurrence, AltaDecision Decision, IReadOnlyList<AltaPayment> Payments,
    string? TnVed = null, decimal? WeightKg = null);

internal sealed record AltaDecision(int Code, string CustomsProcedureCode, DateTimeOffset? DecisionAt = null);

internal sealed record AltaPayment(string Kind, decimal Amount, string Currency);

internal sealed record AltaBatchResult(
    [property: JsonRequired] string CmrNumber,
    [property: JsonRequired] string DecCode,
    [property: JsonRequired] int AcceptedCount,
    [property: JsonRequired] IReadOnlyList<AltaItemResult> Results);

internal sealed record AltaItemResult(
    [property: JsonRequired] int Index,
    [property: JsonRequired] string Outcome,
    string? Code = null, string? Message = null);

internal sealed record AltaWorklistStatus(IReadOnlyList<string>? Cmrs, bool IsStale, string? Failure = null);
