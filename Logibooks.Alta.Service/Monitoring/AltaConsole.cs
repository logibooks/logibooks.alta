// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Globalization;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.Monitoring;

internal interface IAltaConsole
{
    Task RunAsync(CancellationToken modeToken, CancellationToken stoppingToken);
}

internal sealed class AltaConsole(
    IAltaConsoleTerminal terminal,
    AltaCoreClient core,
    ICoreApiProbe probe,
    IOptionsMonitor<DatabaseMonitoringOptions> databases,
    IOptionsMonitor<ServiceOptions> service,
    IHostApplicationLifetime lifetime) : IAltaConsole
{
    private const string Commands = "Commands: login, worklist [database], submit, status, help, exit";
    private string? _lastSubmission;
    private string? _lastLogin;

    public async Task RunAsync(CancellationToken modeToken, CancellationToken stoppingToken)
    {
        terminal.WriteLine(Commands);
        while (!modeToken.IsCancellationRequested)
        {
            try
            {
                var line = await ReadAsync("alta> ", modeToken);
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                    continue;

                switch (parts[0].ToLowerInvariant())
                {
                    case "login":
                        await LoginAsync(modeToken);
                        break;
                    case "worklist":
                        await ShowWorklistAsync(parts.Length == 2 ? parts[1] : null, modeToken);
                        break;
                    case "submit":
                        await SubmitAsync(modeToken, stoppingToken);
                        break;
                    case "status":
                        ShowStatus();
                        break;
                    case "exit":
                        lifetime.StopApplication();
                        return;
                    case "help":
                        terminal.WriteLine(Commands);
                        break;
                    default:
                        terminal.WriteLine("Unknown command. Type help.");
                        break;
                }
            }
            catch (EndOfStreamException)
            {
                lifetime.StopApplication();
                return;
            }
            catch (OperationCanceledException) when (modeToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsReadFailure(ex) || ex is FormatException)
            {
                terminal.WriteLine(SafeText(ex is FormatException ? ex.Message : AltaCoreClient.DescribeReadFailure(ex)));
            }
        }
    }

    private async Task LoginAsync(CancellationToken token)
    {
        try
        {
            core.CaptureConfiguration();
            await probe.ProbeAsync(token);
            _lastLogin = "Core login verified.";
        }
        catch (Exception ex) when (IsReadFailure(ex) && !token.IsCancellationRequested)
        {
            _lastLogin = $"Core login failed: {AltaCoreClient.DescribeReadFailure(ex)}";
        }
        terminal.WriteLine(SafeText(_lastLogin));
    }

    private async Task ShowWorklistAsync(string? database, CancellationToken token)
    {
        var scope = core.CaptureConfiguration();
        var targets = database is null ? GetTargets() : [RequireTarget(database)];
        foreach (var target in targets)
        {
            try
            {
                var cmrs = await core.RefreshWorklistAsync(scope, target, token);
                terminal.WriteLine(SafeText($"{target}: {(cmrs.Count == 0 ? "(no CMRs)" : string.Join(", ", cmrs))}"));
            }
            catch (Exception ex) when (IsReadFailure(ex) && !token.IsCancellationRequested)
            {
                terminal.WriteLine(SafeText($"{target}: {AltaCoreClient.DescribeReadFailure(ex)}"));
            }
        }
        if (targets.Length == 0)
            terminal.WriteLine("No enabled Alta database targets are configured.");
    }

    private async Task SubmitAsync(CancellationToken modeToken, CancellationToken stoppingToken)
    {
        var scope = core.CaptureConfiguration();
        var targets = GetTargets();
        if (targets.Length == 0)
            throw new CoreConfigurationException("No enabled Alta database targets are configured.");
        terminal.WriteLine(SafeText($"Databases: {string.Join(", ", targets)}"));
        var database = RequireTarget(await ReadAsync("Database: ", modeToken));
        var cmrs = await core.RefreshWorklistAsync(scope, database, modeToken);
        if (cmrs.Count == 0)
            throw new FormatException("The Core worklist has no CMRs for this database.");
        terminal.WriteLine(SafeText($"CMRs: {string.Join(", ", cmrs)}"));
        var cmr = await ReadAsync("CMR: ", modeToken);
        RequireCmr(cmrs, cmr);

        var dec = await RequiredAsync("DEC code: ", modeToken);
        var parcel = await RequiredAsync("Parcel number: ", modeToken);
        var code = ReadInt(await RequiredAsync("Decision code: ", modeToken), 1, 99, "Decision code");
        var procedure = await RequiredAsync("Customs procedure code: ", modeToken);
        _ = ReadInt(procedure, 0, short.MaxValue, "Customs procedure code");
        var dateText = await ReadAsync("Decision time (ISO 8601 with time zone, optional): ", modeToken);
        DateTimeOffset? date = dateText.Length == 0 ? null : ReadDate(dateText);
        var payments = await ReadPaymentsAsync(modeToken);
        var weightText = await ReadAsync("Weight in kg (optional): ", modeToken);
        decimal? weight = weightText.Length == 0 ? null : ReadDecimal(weightText, "Weight");

        var items = new List<AltaGoods>();
        while (true)
        {
            var description = await ReadAsync(
                items.Count == 0 ? "Goods description: " : "Next goods description (blank to finish): ", modeToken);
            if (description.Length == 0)
            {
                if (items.Count == 0)
                    throw new FormatException("At least one goods description is required.");
                break;
            }

            var tnVed = await ReadAsync("TN VED (optional): ", modeToken);
            var quantity = ReadDecimal(await RequiredAsync("Quantity: ", modeToken), "Quantity");
            if (quantity < 1 || quantity != decimal.Truncate(quantity))
                throw new FormatException("Quantity must be a positive whole number.");
            var total = ReadDecimal(await RequiredAsync("Total cost for the full quantity: ", modeToken), "Total cost");
            items.Add(new AltaGoods(description, BlankToNull(tnVed), quantity, total));
        }

        var assignment = new AltaAssignment(parcel, items, new AltaDecision(code, procedure, date), payments, weight);
        var batch = new AltaBatch(cmr, dec, [assignment]);
        terminal.WriteLine(SafeText($"Core: {scope.BaseUrl.Trim()} | database: {database}"));
        terminal.WriteLine(JsonSerializer.Serialize(batch, new JsonSerializerOptions(AltaCoreClient.JsonOptions) { WriteIndented = true }));
        if (!string.Equals(await ReadAsync("Send this update? [y/N]: ", modeToken), "y", StringComparison.OrdinalIgnoreCase))
        {
            terminal.WriteLine("Submission cancelled.");
            return;
        }

        RequireTarget(database);
        RequireCmr(await core.RefreshWorklistAsync(scope, database, modeToken), cmr);
        modeToken.ThrowIfCancellationRequested();
        if (service.CurrentValue.Enabled)
            throw new CoreConfigurationException("Monitoring is enabled. The manual submission was cancelled.");

        try
        {
            // Mode changes cancel input and preflight reads. A dispatched write keeps its own deadline.
            var result = await core.SubmitAsync(scope, database, batch, stoppingToken);
            var item = result.Results[0];
            _lastSubmission = $"{database} / {cmr}: {item.Outcome} (saved decisions: {result.AcceptedCount})";
            if (item.Code is not null)
                _lastSubmission += $" [{item.Code}]";
            if (item.Message is not null)
                _lastSubmission += $" {item.Message}";
        }
        catch (Exception ex) when (IsReadFailure(ex) &&
                                   (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested))
        {
            _lastSubmission = $"{database} / {cmr}: {AltaCoreClient.DescribeReadFailure(ex)}";
        }
        terminal.WriteLine(SafeText(_lastSubmission));
    }

    private async Task<List<AltaPayment>> ReadPaymentsAsync(CancellationToken token)
    {
        var payments = new List<AltaPayment>();
        while (true)
        {
            var kind = await ReadAsync("Payment (dutiesAndTaxes/customsFee, blank to finish): ", token);
            if (kind.Length == 0)
                return payments;
            if (kind is not ("dutiesAndTaxes" or "customsFee"))
                throw new FormatException("Choose dutiesAndTaxes or customsFee.");
            var amount = ReadDecimal(await RequiredAsync("Payment amount: ", token), "Payment amount");
            var currency = (await RequiredAsync("Currency (three letters): ", token)).ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
                throw new FormatException("Currency must contain three letters.");
            payments.Add(new AltaPayment(kind, amount, currency));
        }
    }

    private void ShowStatus()
    {
        terminal.WriteLine(SafeText(_lastLogin ?? "Core login has not been checked in this console."));
        foreach (var target in GetTargets())
        {
            var status = core.GetWorklistStatus(target);
            var description = status is null ? "not fetched" : $"{status.Cmrs?.Count ?? 0} CMR(s)";
            if (status?.IsStale == true)
                description += $", stale ({status.Failure})";
            terminal.WriteLine(SafeText($"{target}: {description}"));
        }
        if (_lastSubmission is not null)
            terminal.WriteLine(SafeText(_lastSubmission));
    }

    private string[] GetTargets() => (databases.CurrentValue.Targets ?? [])
        .Where(target => target.Enabled && !string.IsNullOrWhiteSpace(target.Name))
        .Select(target => target.Name.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    private string RequireTarget(string name) => GetTargets().Contains(name, StringComparer.Ordinal)
        ? name : throw new CoreConfigurationException("Choose the exact name of an enabled Alta database target.");

    private static void RequireCmr(IReadOnlyList<string> cmrs, string cmr)
    {
        if (!cmrs.Contains(cmr, StringComparer.Ordinal))
            throw new CoreConfigurationException("This CMR is no longer available in the target's Core worklist.");
    }

    private async Task<string> ReadAsync(string prompt, CancellationToken token) =>
        (await terminal.ReadLineAsync(prompt, token))?.Trim() ?? throw new EndOfStreamException();

    private async Task<string> RequiredAsync(string prompt, CancellationToken token) =>
        BlankToNull(await ReadAsync(prompt, token)) ?? throw new FormatException($"{prompt.TrimEnd(':', ' ')} is required.");

    private static string? BlankToNull(string value) => value.Length == 0 ? null : value;

    private static int ReadInt(string value, int min, int max, string field) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= min && result <= max
            ? result : throw new FormatException($"{field} must be an integer from {min} to {max}.");

    private static decimal ReadDecimal(string value, string field) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result : throw new FormatException($"{field} must be a nonnegative number. Use a decimal point.");

    private static DateTimeOffset ReadDate(string value)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];
        return value.Length > 19 && (value.EndsWith('Z') || value[^6] is '+' or '-') &&
               DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new FormatException("Enter an ISO 8601 decision time with Z or a numeric offset.");
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is CoreApiException or HttpRequestException or OperationCanceledException or JsonException;

    private static string SafeText(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
