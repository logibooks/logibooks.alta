// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class MonitoringTests
{
    [Test]
    public async Task RestoredAltaDatabaseResolvesGoodsForEveryRequestedShipment()
    {
        var path = Environment.GetEnvironmentVariable("LOGIBOOKS_ALTA_SQL_TEST_CONFIG");
        var cmr = Environment.GetEnvironmentVariable("LOGIBOOKS_ALTA_SQL_TEST_CMR");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(cmr))
            Assert.Ignore("Set LOGIBOOKS_ALTA_SQL_TEST_CONFIG and LOGIBOOKS_ALTA_SQL_TEST_CMR for a restored SQL sample with complete declaration goods.");

        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
        var database = settings.RootElement.GetProperty("Databases").GetProperty("Targets").EnumerateArray()
            .First(target => target.GetProperty("Name").GetString() == "reestr_1");
        var target = new RuntimeDatabaseTarget("reestr_1", database.GetProperty("ConnectionString").GetString()!);
        var start = Stopwatch.StartNew();
        var source = new AltaSqlSource();
        var requested = new Dictionary<Guid, HashSet<Guid>>();
        var result = await source.ReadAsync(target, [cmr!], registers =>
        {
            var register = registers.Single();
            requested = register.Parcels.Values.GroupBy(parcel => parcel.Number, StringComparer.Ordinal)
                .Select(group => (Number: group.Key, Linked: group
                    .Select(parcel => (Parcel: parcel, DeclarationId: AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference)))
                    .Where(link => link.DeclarationId is { } id && register.Declarations.ContainsKey(id))
                    .ToArray()))
                .Where(group => group.Number.Length > 0 && group.Linked.Length > 0 &&
                    group.Linked.All(link => link.DeclarationId == group.Linked[0].DeclarationId))
                .GroupBy(group => group.Linked[0].DeclarationId!.Value)
                .ToDictionary(group => group.Key,
                    group => group.SelectMany(item => item.Linked).Select(link => link.Parcel.Id).ToHashSet());
            return requested;
        }, CancellationToken.None);

        var snapshot = result.Registers.Single();
        var goodsCounts = result.Shipments.Values.SelectMany(shipments => shipments.Values)
            .Select(shipment => shipment?.Goods?.Count ?? 0).ToArray();
        var requestedCount = requested.Values.Sum(ids => ids.Count);
        var resolvedCount = goodsCounts.Count(count => count > 0);
        TestContext.WriteLine($"Read {snapshot.Declarations.Count} DECs and {snapshot.Parcels.Count} IndPost rows in {start.Elapsed.TotalSeconds:F1}s.");
        TestContext.WriteLine($"Resolved goods for {resolvedCount} of {requestedCount} requested shipments. " +
            $"Most goods lines on one parcel: {goodsCounts.DefaultIfEmpty().Max()}.");
        Assert.That(snapshot.Declarations, Is.Not.Empty);
        Assert.That(snapshot.Parcels, Is.Not.Empty);
        Assert.That(requestedCount, Is.Positive);
        Assert.That(resolvedCount, Is.EqualTo(requestedCount));
        foreach (var (declarationId, shipments) in result.Shipments)
            Assert.That(shipments.Keys, Is.SubsetOf(requested[declarationId]));

        var idle = await source.ReadAsync(target, [cmr!], _ => new Dictionary<Guid, HashSet<Guid>>(), CancellationToken.None);
        Assert.That(idle.Shipments, Is.Empty);
    }

    [Test]
    public async Task SelectsGoodsAcrossRegistersAndOnlyReportsChangesAfterTheInitialPass()
    {
        using var monitor = new MonitoringFixture();
        var first = Parcel("0001");
        var unchanged = Parcel("0002");
        var second = Parcel("0003");
        monitor.Put(Snapshot("CMR-1", first, unchanged), Snapshot("CMR-2", second));

        await monitor.PollAsync();
        Assert.That(monitor.Source.Lookups.Single(), Is.EquivalentTo(new[] { "0001", "0002", "0003" }));
        Assert.That(monitor.Submissions.Select(batch => batch.Assignments.Count), Is.EqualTo(new[] { 2, 1 }));

        await monitor.PollAsync();
        Assert.That(monitor.Source.Lookups, Has.Count.EqualTo(1));
        Assert.That(monitor.Submissions, Has.Count.EqualTo(2));

        monitor.Put(Snapshot("CMR-1", first with { Decision = "90" }, unchanged), Snapshot("CMR-2", second));
        await monitor.PollAsync();
        var changed = monitor.Submissions.Last().Assignments.Single();
        Assert.That(changed.ParcelNumber, Is.EqualTo("0001"));
        Assert.That(changed.Decision.Code, Is.EqualTo(90));

        var newDec = Parcel("0004", Guid.NewGuid());
        monitor.Put(Snapshot("CMR-1", first with { Decision = "90" }, unchanged, newDec), Snapshot("CMR-2", second));
        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Last().Assignments.Single().ParcelNumber, Is.EqualTo("0004"));
    }

    [Test]
    public async Task ExistingDecAndXmlChangesAloneDoNotQueueWork()
    {
        using var monitor = new MonitoringFixture();
        var parcel = Parcel("0001");
        var snapshot = Snapshot("CMR-1", parcel);
        monitor.Put(snapshot);
        await monitor.PollAsync();

        var payments = new[] { new AltaPayment("customsFee", 12m, "RUB") };
        monitor.Source.Payments = payments;
        var editedDec = snapshot.Declarations[MainDec] with { Number = "edited" };
        var editedSnapshot = snapshot with { Declarations = new Dictionary<Guid, AltaDeclaration> { [MainDec] = editedDec } };
        monitor.Put(editedSnapshot);
        await monitor.PollAsync();
        Assert.That(monitor.Source.Lookups, Has.Count.EqualTo(1));
        Assert.That(monitor.Submissions, Has.Count.EqualTo(1));

        monitor.Put(editedSnapshot with
        {
            Parcels = new Dictionary<Guid, AltaParcel> { [parcel.Id] = parcel with { WeightKg = 2m } }
        });
        await monitor.PollAsync();
        var assignment = monitor.Submissions.Last().Assignments.Single();
        Assert.That(monitor.Submissions, Has.Count.EqualTo(2));
        Assert.That(monitor.Submissions.Last().DecCode, Is.EqualTo(editedDec.Number));
        Assert.That(assignment.Payments, Is.EqualTo(payments));
        Assert.That(assignment.WeightKg, Is.EqualTo(2m));
    }

    [Test]
    public async Task ArrivalOfDecQueuesAnAlreadyKnownIndPostRow()
    {
        using var monitor = new MonitoringFixture();
        var snapshot = Snapshot("CMR-1", Parcel("0001"));
        monitor.Put(snapshot with { Declarations = new Dictionary<Guid, AltaDeclaration>() });
        await monitor.PollAsync();
        Assert.That(monitor.Source.Lookups, Is.Empty);
        Assert.That(monitor.Submissions, Is.Empty);

        monitor.Put(snapshot);
        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Single().Assignments.Single().ParcelNumber, Is.EqualTo("0001"));
    }

    [TestCase(" LIST-DEC ", " DOC-DEC ", "LIST-DEC")]
    [TestCase(null, " DOC-DEC ", "DOC-DEC")]
    [TestCase("\t\u00a0", " DOC-DEC ", "DOC-DEC")]
    [TestCase(null, " \t", null)]
    public void DeclarationNumberUsesTheListRegistrationWithDocumentFallback(
        string? listRegNum, string? documentRegNum, string? expected) =>
        Assert.That(AltaSourceValues.ReadDeclarationNumber(listRegNum, documentRegNum), Is.EqualTo(expected));

    [TestCase(null, "10", "Missing DEC number")]
    [TestCase(" ", "10", "Missing DEC number")]
    [TestCase("DEC-1", null, "Missing or invalid customs procedure")]
    [TestCase("DEC-1", "invalid", "Missing or invalid customs procedure")]
    public async Task InvalidDeclarationFieldsDeferTheAssignmentWithASpecificWarning(
        string? number, string? procedure, string reason)
    {
        using var monitor = new MonitoringFixture();
        var snapshot = Snapshot("CMR-1", Parcel("0001"));
        monitor.Put(snapshot with
        {
            Declarations = new Dictionary<Guid, AltaDeclaration> { [MainDec] = new(MainDec, number, procedure) }
        });

        await monitor.PollAsync();

        Assert.That(monitor.Submissions, Is.Empty);
        Assert.That(monitor.Logger.Entries, Does.Contain((LogLevel.Warning, $"Deferred Alta Alta / CMR-1 / 0001: {reason}")));
    }

    [Test]
    public async Task FailedWorklistRetainsBaselineAndReentryReplaysIt()
    {
        using var monitor = new MonitoringFixture();
        var snapshot = Snapshot("CMR-1", Parcel("0001"));
        monitor.Put(snapshot);
        await monitor.PollAsync();

        monitor.WorklistStatus = HttpStatusCode.Forbidden;
        await monitor.PollAsync();
        monitor.WorklistStatus = HttpStatusCode.OK;
        await monitor.PollAsync();
        Assert.That(monitor.Submissions, Has.Count.EqualTo(1));
        Assert.That(monitor.Source.Lookups, Has.Count.EqualTo(1));

        monitor.Cmrs = [];
        await monitor.PollAsync();
        monitor.Put(snapshot);
        await monitor.PollAsync();
        Assert.That(monitor.Submissions, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task AConflictRefreshThatRetiresTheRegisterClearsItsSavedAssignments()
    {
        using var monitor = new MonitoringFixture();
        var original = Parcel("0001");
        monitor.Put(Snapshot("CMR-1", original));
        await monitor.PollAsync();

        var expanded = Snapshot("CMR-1", original, Parcel("0002"));
        monitor.Put(expanded);
        monitor.ConflictNextSubmission = true;
        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Last().Assignments.Single().ParcelNumber, Is.EqualTo("0002"));

        monitor.Put(expanded);
        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Last().Assignments.Select(item => item.ParcelNumber),
            Is.EqualTo(new[] { "0001", "0002" }));
    }

    [Test]
    public async Task TimedOutGroupsAreSplitAndAUniversalTimeoutHasABoundedBudget()
    {
        using var monitor = new MonitoringFixture();
        monitor.Put(Snapshot("CMR-1", Parcel("0001")), Snapshot("CMR-2", Parcel("0002")));
        monitor.Source.MaxRegistersPerRead = 1;
        await monitor.PollAsync();
        Assert.That(monitor.Source.GroupSizes, Is.EqualTo(new[] { 2, 1, 1 }));
        Assert.That(monitor.Submissions, Has.Count.EqualTo(2));
        monitor.Source.GroupSizes.Clear();
        await monitor.PollAsync();
        Assert.That(monitor.Source.GroupSizes, Is.EqualTo(new[] { 1, 1 }));

        monitor.Put(Enumerable.Range(10, 20).Select(i => Snapshot($"CMR-{i}", Parcel(i.ToString()))).ToArray());
        monitor.Source.GroupSizes.Clear();
        monitor.Source.MaxRegistersPerRead = 0;
        await monitor.PollAsync();
        Assert.That(monitor.Source.GroupSizes, Is.EqualTo(new[] { 20, 10, 5 }));
    }

    [Test]
    public async Task RetriesReadCurrentGoodsAndKeepPerNumberAcknowledgements()
    {
        using var monitor = new MonitoringFixture { RejectNumber = "0002" };
        var first = Parcel("0001");
        var second = Parcel("0002");
        var third = Parcel("0003");
        monitor.Put(Snapshot("CMR-1", first, second, third));
        monitor.Source.Missing = "0003";

        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Single().Assignments.Select(item => item.ParcelNumber),
            Is.EqualTo(new[] { "0001", "0002" }));

        monitor.Source.Missing = null;
        monitor.Source.Description = "Updated goods";
        monitor.Clock.UtcNow += TimeSpan.FromHours(2);
        await monitor.PollAsync();
        var retried = monitor.Submissions.Last().Assignments.Single();
        Assert.That(retried.ParcelNumber, Is.EqualTo("0003"));
        Assert.That(retried.Items.Select(item => item.Description), Is.EqualTo(new[] { "Updated goods" }));
        Assert.That(monitor.Source.Lookups.Last(), Is.EqualTo(new[] { "0003" }));
    }

    [Test]
    public async Task ConflictingGoodsForOneParcelNumberStayPending()
    {
        using var monitor = new MonitoringFixture();
        var first = Parcel("0001");
        var second = Parcel("0001");
        monitor.Put(Snapshot("CMR-1", first, second));
        monitor.Source.ParcelDescriptions[second.Id] = "Different goods";

        await monitor.PollAsync();
        Assert.That(monitor.Submissions, Is.Empty);

        monitor.Source.ParcelDescriptions.Clear();
        monitor.Clock.UtcNow += TimeSpan.FromMinutes(1);
        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Single().Assignments.Single().Items, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AStaleDispatchDoesNotAcknowledgeWorkInTheNewTarget()
    {
        using var monitor = new MonitoringFixture();
        monitor.Put(Snapshot("CMR-1", Parcel("0001")));
        monitor.OnSubmit = () => monitor.Configuration.Current = monitor.Configuration.Current with
        {
            Databases = [new RuntimeDatabaseTarget("Alta", "new source")]
        };

        await monitor.PollAsync();
        monitor.OnSubmit = null;
        await monitor.PollAsync();
        Assert.That(monitor.Submissions, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ReplacedButEquivalentCoreOptionsDuringTheSourceReadDoNotDelayThePost()
    {
        using var monitor = new MonitoringFixture();
        monitor.Put(Snapshot("CMR-1", Parcel("0001")));
        monitor.Source.AfterRead = () => monitor.Options.Set(new CoreOptions
        {
            BaseUrl = "https://core.example", Email = "writer", Password = "secret"
        });

        await monitor.PollAsync();

        Assert.That(monitor.Submissions.Single().Assignments.Single().ParcelNumber, Is.EqualTo("0001"));
    }

    [Test]
    public async Task AVerifiedLinkCoversUnassignedDuplicatesButAnUnresolvedNonNullLinkDefersTheNumber()
    {
        using var monitor = new MonitoringFixture();
        var confirmed = Parcel("0001");
        var duplicate = new AltaParcel(Guid.NewGuid(), "0001", "invalid reference", null, null);
        monitor.Put(Snapshot("CMR-1", confirmed, duplicate));
        await monitor.PollAsync();
        Assert.That(monitor.Submissions, Is.Empty);
        Assert.That(monitor.Source.Lookups, Is.Empty);

        monitor.Put(Snapshot("CMR-1", confirmed, duplicate with { DeclarationReference = null }));
        await monitor.PollAsync();
        Assert.That(monitor.Submissions.Single().Assignments.Single().ParcelNumber, Is.EqualTo("0001"));
    }

    [Test]
    public async Task QuartzDropsOverlappingAndLateFiringsThenAllowsTheNextOne()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var initial = clock.UtcNow;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var schedule = new QuartzMonitoringSchedule(clock, async _ =>
        {
            calls++;
            await release.Task;
        }, CancellationToken.None);

        var active = schedule.RunAsync(initial);
        await schedule.RunAsync(initial.AddMinutes(1));
        Assert.That(calls, Is.EqualTo(1));

        clock.UtcNow = initial.AddMinutes(4);
        release.SetResult();
        await active;
        await schedule.RunAsync(initial.AddMinutes(2));
        Assert.That(calls, Is.EqualTo(1));

        await schedule.RunAsync(initial.AddMinutes(5));
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public async Task FinalQuartzFiringStillSubmitsAfterTheScheduleHasNoNextOccurrence()
    {
        using var fixture = new MonitoringFixture();
        var settings = new ServiceOptions { Enabled = true, MonitoringCron = "0 0 20 1 1 ? 2026", MonitoringTimeZone = "UTC" };
        using var provider = new RuntimeConfigurationProvider(new TestOptionsMonitor<ServiceOptions>(settings),
            fixture.Options, new TestOptionsMonitor<DatabaseMonitoringOptions>(new()
            {
                Targets = [new DatabaseTargetOptions { Enabled = true, Name = "Alta", ConnectionString = "Server=db" }]
            }), new CoreUris(false));

        var configuration = provider.GetValidated(scheduleFrom: fixture.Clock.UtcNow);
        var cron = new CronExpression(settings.MonitoringCron) { TimeZone = TimeZoneInfo.Utc };
        var finalFiring = cron.GetNextValidTimeAfter(fixture.Clock.UtcNow)!.Value;
        fixture.Put(Snapshot("CMR-1", Parcel("0001")));
        var monitor = fixture.CreateMonitor(provider);
        await using var schedule = new QuartzMonitoringSchedule(fixture.Clock,
            token => monitor.RunAsync(configuration, token), CancellationToken.None);

        fixture.Clock.UtcNow = finalFiring.AddSeconds(1);
        Assert.That(cron.GetNextValidTimeAfter(fixture.Clock.UtcNow), Is.Null);
        await schedule.RunAsync(finalFiring);

        Assert.That(fixture.Submissions.SelectMany(batch => batch.Assignments).Select(item => item.ParcelNumber),
            Is.EqualTo(new[] { "0001" }));
    }

    [Test]
    public void ConfiguredExampleRunsAtSevenMoscowTimes()
    {
        var options = new ServiceOptions();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.MonitoringTimeZone);
        var cron = new CronExpression(options.MonitoringCron) { TimeZone = zone };
        var cursor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var localHours = new List<int>();
        for (var i = 0; i < 7; i++)
        {
            cursor = cron.GetNextValidTimeAfter(cursor)!.Value;
            localHours.Add(TimeZoneInfo.ConvertTime(cursor, zone).Hour);
        }
        Assert.That(localHours, Is.EqualTo(new[] { 8, 10, 12, 14, 16, 18, 20 }));
        Assert.That(TimeZoneInfo.ConvertTime(cron.GetNextValidTimeAfter(cursor)!.Value, zone).Day, Is.EqualTo(2));
    }

    [TestCase("0.12345678901234567890123456789", false)]
    [TestCase("-0.001", true)]
    [TestCase("001.2300", true)]
    [TestCase("1,23", false)]
    public void AltaNumericParsingRejectsRoundingAndHostSpecificSeparators(string text, bool valid)
    {
        Assert.That(AltaSourceValues.TryReadDecimal(text, out _), Is.EqualTo(valid));
    }

    [Test]
    public async Task DeclarationXmlExposesOnlyPaymentsForTheIdentifiedParcel()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var linkedGid = id.ToString("B");
        var xml = $"""
            <?xml version="1.0" encoding="windows-1251"?>
            <AltaExpressCargoDeclaration>
              <GoodsShipment_TotalPaymentAmountDetails_Amount>200.00</GoodsShipment_TotalPaymentAmountDetails_Amount>
              <GoodsShipment_HouseShipment>
                <IndGid>{linkedGid}</IndGid>
                <HouseWaybillDetails_PrDocumentNumber>0001</HouseWaybillDetails_PrDocumentNumber>
                <TaxBase_DecisionDate>2026-04-16T12:37:45+03:00</TaxBase_DecisionDate>
                <CustomsPaymentDetails><PaymentModeCode>1010</PaymentModeCode><PaymentCurrencyCode>643</PaymentCurrencyCode><PaymentAmount>100.00</PaymentAmount></CustomsPaymentDetails>
                <UnifiedGrossWeightQuantity_GoodsQuantity>0.245</UnifiedGrossWeightQuantity_GoodsQuantity>
                <UnifiedGrossWeightQuantity_MeasureUnitQualifierCode>166</UnifiedGrossWeightQuantity_MeasureUnitQualifierCode>
              </GoodsShipment_HouseShipment>
              <GoodsShipment_HouseShipment>
                <IndGid>{other:D}</IndGid>
                <HouseWaybillDetails_PrDocumentNumber>0002</HouseWaybillDetails_PrDocumentNumber>
                <CustomsPaymentDetails><PaymentModeCode>unknown</PaymentModeCode><PaymentCurrencyCode>643</PaymentCurrencyCode><PaymentAmount>100.00</PaymentAmount></CustomsPaymentDetails>
              </GoodsShipment_HouseShipment>
            </AltaExpressCargoDeclaration>
            """;
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(xml));
        var result = await AltaDeclarationDocument.ReadAsync(stream, new HashSet<Guid> { id, other }, CancellationToken.None);
        Assert.That(result[id]!.Number, Is.EqualTo("0001"));
        Assert.That(result[id]!.Payments, Is.EqualTo(new[] { new AltaPayment("customsFee", 100m, "RUB") }));
        Assert.That(result[id]!.DecisionAt, Is.EqualTo(new DateTimeOffset(2026, 4, 16, 12, 37, 45, TimeSpan.FromHours(3))));
        Assert.That(result[id]!.WeightKg, Is.EqualTo(0.245m));
        Assert.That(result[other], Is.Null);
    }

    [Test]
    public async Task DeclarationXmlReturnsEveryGoodsLineForTheSelectedIndPostRowsInOrder()
    {
        var id = Guid.NewGuid();
        var second = Guid.NewGuid();
        var other = Guid.NewGuid();
        var expected = Enumerable.Range(1, 6).Select(i =>
            new AltaGoods($"УИН: 000{i}; Goods", i == 6 ? null : "0012345678", i, i + 0.25m)).ToArray();
        expected[0] = expected[0] with { Description = $"  {expected[0].Description}  " };
        // Preserve parseable values so Core can record any business validation errors.
        expected[1] = expected[1] with { Quantity = 1.5m };
        expected[2] = expected[2] with { Quantity = 0m, TotalCost = -1m };
        expected[4] = expected[0];
        var lines = string.Concat(expected.Select(item => FormattableString.Invariant($"""
            <GoodsItemDetails>
              <GoodsDescription>{item.Description}</GoodsDescription>
              <GoodsTNVEDCode>{item.TnVed}</GoodsTNVEDCode>
              <SupplementaryQuantity_GoodsQuantity>{item.Quantity}</SupplementaryQuantity_GoodsQuantity>
              <TotalGoodsNumeric>100</TotalGoodsNumeric>
              <CAValueAmount><CurrencyQuantity>{item.TotalCost}</CurrencyQuantity><CurrencyCode>UZS</CurrencyCode></CAValueAmount>
              <CAValueAmount><CurrencyQuantity>100.00</CurrencyQuantity><CurrencyCode>RUB</CurrencyCode></CAValueAmount>
              <CustomsCost_CurrencyQuantity>200.00</CustomsCost_CurrencyQuantity>
            </GoodsItemDetails>
            """)));
        var xml = $"""
            <AltaExpressCargoDeclaration xmlns="urn:alta">
              <GoodsShipment_HouseShipment>
                <IndGid>{id:B}</IndGid>
                <HouseWaybillDetails_PrDocumentNumber>0001</HouseWaybillDetails_PrDocumentNumber>
                {lines}
              </GoodsShipment_HouseShipment>
              <GoodsShipment_HouseShipment>
                <IndGid>{other:B}</IndGid>
                <HouseWaybillDetails_PrDocumentNumber>0001</HouseWaybillDetails_PrDocumentNumber>
                {lines}
              </GoodsShipment_HouseShipment>
              <GoodsShipment_HouseShipment>
                <IndGid>{second:B}</IndGid>
                <HouseWaybillDetails_PrDocumentNumber>0002</HouseWaybillDetails_PrDocumentNumber>
                <GoodsItemDetails>
                  <GoodsDescription>Second shipment goods</GoodsDescription>
                  <SupplementaryQuantity_GoodsQuantity>1</SupplementaryQuantity_GoodsQuantity>
                  <CAValueAmount><CurrencyQuantity>5.00</CurrencyQuantity></CAValueAmount>
                </GoodsItemDetails>
              </GoodsShipment_HouseShipment>
            </AltaExpressCargoDeclaration>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await AltaDeclarationDocument.ReadAsync(stream, new HashSet<Guid> { id, second }, CancellationToken.None);

        Assert.That(result.Keys, Is.EquivalentTo(new[] { id, second }));
        Assert.That(result[id]!.Goods, Is.EqualTo(expected));
        Assert.That(result[second]!.Goods, Is.EqualTo(new[] { new AltaGoods("Second shipment goods", null, 1, 5) }));
    }

    [TestCase("123456789012345678901234567890", "10.00")]
    [TestCase("1", "0.12345678901234567890123456789")]
    [TestCase("1", "")]
    public async Task IncompleteXmlGoodsLinePreventsPartialGoodsList(string quantity, string total)
    {
        var id = Guid.NewGuid();
        var xml = $"""
            <AltaExpressCargoDeclaration>
              <GoodsShipment_HouseShipment>
                <IndGid>{id:B}</IndGid>
                <HouseWaybillDetails_PrDocumentNumber>0001</HouseWaybillDetails_PrDocumentNumber>
                <GoodsItemDetails>
                  <GoodsDescription>Valid goods</GoodsDescription>
                  <SupplementaryQuantity_GoodsQuantity>1</SupplementaryQuantity_GoodsQuantity>
                  <CAValueAmount><CurrencyQuantity>10.00</CurrencyQuantity></CAValueAmount>
                </GoodsItemDetails>
                <GoodsItemDetails>
                  <GoodsDescription>Invalid goods</GoodsDescription>
                  <SupplementaryQuantity_GoodsQuantity>{quantity}</SupplementaryQuantity_GoodsQuantity>
                  <CAValueAmount><CurrencyQuantity>{total}</CurrencyQuantity></CAValueAmount>
                </GoodsItemDetails>
              </GoodsShipment_HouseShipment>
            </AltaExpressCargoDeclaration>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await AltaDeclarationDocument.ReadAsync(stream, new HashSet<Guid> { id }, CancellationToken.None);

        Assert.That(result[id]!.Goods, Is.Null);
    }

    private static readonly Guid MainDec = Guid.NewGuid();

    private static AltaParcel Parcel(string number, Guid? dec = null) =>
        new(Guid.NewGuid(), number, "{\\<" + (dec ?? MainDec).ToString("D") + "}", "10", null);

    private static AltaRegisterSnapshot Snapshot(string cmr, params AltaParcel[] parcels)
    {
        var declarations = parcels.Where(parcel => AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference) is not null)
            .GroupBy(parcel => AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference)!.Value)
            .ToDictionary(group => group.Key, group => new AltaDeclaration(group.Key, group.Key.ToString("N"), "10"));
        return new AltaRegisterSnapshot(cmr, declarations, parcels.ToDictionary(parcel => parcel.Id));
    }

    private sealed class MonitoringFixture : IDisposable
    {
        private readonly RecordingHttpHandler _handler;
        private readonly AltaCoreClient _core;
        private readonly AltaMonitor _monitor;
        public SourceFixture Source { get; } = new();
        public ListLogger<AltaMonitor> Logger { get; } = new();
        public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public ConfigurationFixture Configuration { get; }
        public TestOptionsMonitor<CoreOptions> Options { get; }
        public List<AltaBatch> Submissions { get; } = [];
        public string[] Cmrs { get; set; } = [];
        public string? RejectNumber { get; init; }
        public HttpStatusCode WorklistStatus { get; set; } = HttpStatusCode.OK;
        public Action? OnSubmit { get; set; }
        public bool ConflictNextSubmission { get; set; }

        public MonitoringFixture()
        {
            var coreOptions = new CoreOptions { BaseUrl = "https://core.example", Email = "writer", Password = "secret" };
            Configuration = new ConfigurationFixture(new RuntimeConfiguration(
                new ServiceOptions { Enabled = true }, coreOptions, 1, [new RuntimeDatabaseTarget("Alta", "source")]));
            _handler = new RecordingHttpHandler(ReplyAsync);
            Options = new TestOptionsMonitor<CoreOptions>(coreOptions);
            _core = new AltaCoreClient(Options, new HttpFactory(_handler),
                new HttpRetryExecutor(new NoRetryDelay()), new CoreUris(false));
            _monitor = CreateMonitor(Configuration);
        }

        public AltaMonitor CreateMonitor(IRuntimeConfigurationProvider configuration) =>
            new(Source, _core, configuration, Clock, Logger);

        public Task PollAsync() => _monitor.RunAsync(Configuration.Current, CancellationToken.None);

        public void Put(params AltaRegisterSnapshot[] snapshots)
        {
            Cmrs = snapshots.Select(snapshot => snapshot.Cmr).ToArray();
            Source.Snapshots = snapshots.ToDictionary(snapshot => snapshot.Cmr);
        }

        private async Task<HttpResponseMessage> ReplyAsync(HttpRequestMessage request, int _, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return Reply(new { cmrs = Cmrs }, WorklistStatus);
            var json = await request.Content!.ReadAsStringAsync(token);
            var batch = JsonSerializer.Deserialize<AltaBatch>(json, AltaCoreClient.JsonOptions)!;
            Submissions.Add(batch);
            OnSubmit?.Invoke();
            if (ConflictNextSubmission)
            {
                ConflictNextSubmission = false;
                Cmrs = [];
                return Reply(new { code = "AltaScopeChanged" }, HttpStatusCode.Conflict);
            }
            return Reply(new AltaBatchResult(batch.CmrNumber, batch.DecCode, batch.Assignments.Count,
                batch.Assignments.Select((item, index) =>
                    new AltaItemResult(index, item.ParcelNumber == RejectNumber ? "error" : "applied")).ToArray()));
        }

        private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value, AltaCoreClient.JsonOptions)) };

        public void Dispose() => _handler.Dispose();
    }

    private sealed class ConfigurationFixture(RuntimeConfiguration configuration) : IRuntimeConfigurationProvider
    {
        public RuntimeConfiguration Current { get; set; } = configuration;
        public RuntimeConfiguration GetValidated(DateTimeOffset? scheduleFrom = null) => Current;
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SourceFixture : IAltaSource
    {
        public Dictionary<string, AltaRegisterSnapshot> Snapshots { get; set; } = [];
        public List<string[]> Lookups { get; } = [];
        public List<int> GroupSizes { get; } = [];
        public int MaxRegistersPerRead { get; set; } = int.MaxValue;
        public Action? AfterRead { get; set; }
        public string? Missing { get; set; }
        public string Description { get; set; } = "Original goods";
        public Dictionary<Guid, string> ParcelDescriptions { get; } = [];
        public IReadOnlyList<AltaPayment> Payments { get; set; } = [];

        public Task<AltaSourceRead> ReadAsync(RuntimeDatabaseTarget target, IReadOnlyList<string> cmrs,
            Func<IReadOnlyList<AltaRegisterSnapshot>, IReadOnlyDictionary<Guid, HashSet<Guid>>> selectShipments,
            CancellationToken token)
        {
            GroupSizes.Add(cmrs.Count);
            if (cmrs.Count > MaxRegistersPerRead)
                throw new TimeoutException();
            var snapshots = cmrs.Select(cmr => Snapshots[cmr]).ToArray();
            var requested = selectShipments(snapshots);
            var parcels = snapshots.SelectMany(snapshot => snapshot.Parcels.Values).ToDictionary(parcel => parcel.Id);
            if (requested.Count > 0)
                Lookups.Add(requested.Values.SelectMany(ids => ids).Select(id => parcels[id].Number)
                    .Distinct(StringComparer.Ordinal).ToArray());
            var shipments = requested.ToDictionary(declaration => declaration.Key,
                declaration => (IReadOnlyDictionary<Guid, AltaShipment?>)declaration.Value.ToDictionary(id => id, id =>
                {
                    var parcel = parcels[id];
                    return (AltaShipment?)new AltaShipment(parcel.Number, null, Payments, null,
                        parcel.Number == Missing ? null : [new(ParcelDescriptions.GetValueOrDefault(id, Description), null, 1, 0)]);
                }));
            AfterRead?.Invoke();
            return Task.FromResult(new AltaSourceRead(snapshots, shipments));
        }
    }
}
