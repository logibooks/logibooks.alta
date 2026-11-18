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
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class MonitoringTests
{
    [Test]
    public async Task RestoredAltaDatabaseResolvesScopedDeclarationsAndGoods()
    {
        var path = Environment.GetEnvironmentVariable("LOGIBOOKS_ALTA_SQL_TEST_CONFIG");
        var cmr = Environment.GetEnvironmentVariable("LOGIBOOKS_ALTA_SQL_TEST_CMR");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(cmr))
            Assert.Ignore("Set LOGIBOOKS_ALTA_SQL_TEST_CONFIG and LOGIBOOKS_ALTA_SQL_TEST_CMR for the restored SQL sample.");

        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
        var database = settings.RootElement.GetProperty("Databases").GetProperty("Targets").EnumerateArray()
            .First(target => target.GetProperty("Name").GetString() == "reestr_1");
        var target = new RuntimeDatabaseTarget("reestr_1", database.GetProperty("ConnectionString").GetString()!);
        var start = Stopwatch.StartNew();
        var source = new AltaSqlSource();
        var worklist = new HashSet<string>(StringComparer.Ordinal) { cmr! };
        var result = await source.ReadAsync(target, [cmr!], worklist, registers =>
        {
            var register = registers.Single();
            return register.Parcels.Values.GroupBy(parcel => parcel.Number, StringComparer.Ordinal)
                .Select(group => (Number: group.Key, Linked: group
                    .Select(parcel => (Parcel: parcel, DeclarationId: AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference)))
                    .Where(link => link.DeclarationId is { } id && register.Declarations.ContainsKey(id))
                    .ToArray()))
                .Where(group => group.Number.Length > 0 && group.Linked.Length > 0 &&
                    group.Linked.All(link => link.DeclarationId == group.Linked[0].DeclarationId))
                .Select(group => new AltaAssignmentRequest(new AltaGoodsKey(cmr!, group.Number),
                    group.Linked[0].DeclarationId!.Value, group.Linked.Select(link => link.Parcel.Id).ToHashSet()))
                .ToArray();
        }, CancellationToken.None);

        var snapshot = result.Registers.Single();
        TestContext.WriteLine($"Read {snapshot.Declarations.Count} DECs and {snapshot.Parcels.Count} IndPost rows in {start.Elapsed.TotalSeconds:F1}s.");
        TestContext.WriteLine($"Resolved {result.Goods.Values.Count(goods => goods.Items is { Count: > 0 })} of {result.Goods.Count} goods lookups.");
        foreach (var failure in result.Goods.Values.Where(goods => goods.Failure is not null).GroupBy(goods => goods.Failure))
            TestContext.WriteLine($"{failure.Count()} deferred: {failure.Key}");
        Assert.That(snapshot.Declarations, Is.Not.Empty);
        Assert.That(snapshot.Parcels, Is.Not.Empty);
        Assert.That(result.Shipments.Values.Sum(shipments => shipments.Count), Is.Positive);
        Assert.That(result.Goods.Values.Any(goods => goods.Items is { Count: > 0 }), Is.True);

        var idle = await source.ReadAsync(target, [cmr!], worklist, _ => [], CancellationToken.None);
        Assert.That(idle.Shipments, Is.Empty);
        Assert.That(idle.Goods, Is.Empty);
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
        var editedDec = snapshot.Declarations[MainDec] with { Code = "edited" };
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
        Assert.That(monitor.Source.WorklistSizes, Is.EqualTo(new[] { 2, 2, 2 }));
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

    [Test]
    public void ProjectsSparseGoodsColumnsInOrderWithoutCombiningRepeatedItems()
    {
        var first = new AltaGoodsSlot(1, "УИН: 00042", "0012345678", "3", "10.00", null);
        var duplicate = first with { Position = 4, TnVed = null };
        var invoice = new Dictionary<Guid, List<AltaGoodsSlot>>
        {
            [Guid.NewGuid()] = [duplicate, new(2, null, null, null, null, null), first]
        };

        var result = AltaSqlSource.ReadInvoice(invoice);

        Assert.That(result.Items, Is.EqualTo(new[] { new AltaGoods("УИН: 00042", "0012345678", 3, 10.00m),
            new AltaGoods("УИН: 00042", null, 3, 10.00m) }));
        invoice.Values.Single().Add(new(3, null, null, null, null, "mark only"));
        Assert.That(AltaSqlSource.ReadInvoice(invoice).Items, Is.Null);
        invoice.Add(Guid.NewGuid(), [first]);
        Assert.That(AltaSqlSource.ReadInvoice(invoice).Failure, Does.Contain("Several"));
    }

    [TestCase("0.12345678901234567890123456789", false)]
    [TestCase("-0.001", true)]
    [TestCase("001.2300", true)]
    [TestCase("1,23", false)]
    public void AltaNumericParsingRejectsRoundingAndHostSpecificSeparators(string text, bool valid)
    {
        Assert.That(AltaSqlSource.TryReadDecimal(text, out _), Is.EqualTo(valid));
    }

    [TestCase("123456789012345678901234567890", "10.00")]
    [TestCase("1", "0.12345678901234567890123456789")]
    public void GoodsLookupRejectsValuesThatCannotBeReadAsDecimals(string quantity, string total)
    {
        var invoice = new Dictionary<Guid, List<AltaGoodsSlot>>
        {
            [Guid.NewGuid()] = [new(1, "Goods", null, quantity, total, null)]
        };

        var result = AltaSqlSource.ReadInvoice(invoice);
        Assert.That(result.Items, Is.Null);
        Assert.That(result.Failure, Does.Contain("goods slot 1"));
    }

    [Test]
    public void DuplicateInvoiceNumbersRequireOneChildOfTheSelectedIndPostDocuments()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var indPost = Guid.NewGuid();
        var otherIndPost = Guid.NewGuid();
        var invoices = new Dictionary<Guid, List<AltaGoodsSlot>>
        {
            [first] = [new(1, "Previous invoice", null, "1", "10", null)],
            [second] = [new(1, "Applicable invoice", null, "1", "15", null)]
        };
        var parents = new Dictionary<Guid, Guid> { [first] = otherIndPost, [second] = indPost };

        Assert.That(AltaSqlSource.ReadInvoice(invoices, parents, new HashSet<Guid> { indPost }).Items!.Single().Description,
            Is.EqualTo("Applicable invoice"));
        Assert.That(AltaSqlSource.ReadInvoice(invoices, parents, new HashSet<Guid> { indPost, otherIndPost }).Items, Is.Null);
    }

    [Test]
    public void AUniqueNumberCannotUseAnInvoiceLinkedToAnotherIndPost()
    {
        var invoice = Guid.NewGuid();
        var indPostA = Guid.NewGuid();
        var indPostB = Guid.NewGuid();
        var invoices = new Dictionary<Guid, List<AltaGoodsSlot>>
        {
            [invoice] = [new(1, "Goods", null, "1", "15", null)]
        };
        var parents = new Dictionary<Guid, Guid> { [invoice] = indPostA };

        Assert.That(AltaSqlSource.ReadInvoice(invoices, parents, new HashSet<Guid> { indPostA }).Items, Is.Not.Null);
        Assert.That(AltaSqlSource.ReadInvoice(invoices, parents, new HashSet<Guid> { indPostB }).Items, Is.Null);
        Assert.That(AltaSqlSource.ReadInvoice(invoices, new Dictionary<Guid, Guid>(), new HashSet<Guid> { indPostB }).Items,
            Is.Not.Null);
        Assert.That(AltaSqlSource.ReadInvoice(invoices, allowParentless: false).Items, Is.Null);
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
        var result = await AltaDeclarationDocument.ReadAsync(stream, CancellationToken.None);
        Assert.That(result[id]!.Number, Is.EqualTo("0001"));
        Assert.That(result[id]!.Payments, Is.EqualTo(new[] { new AltaPayment("customsFee", 100m, "RUB") }));
        Assert.That(result[id]!.DecisionAt, Is.EqualTo(new DateTimeOffset(2026, 4, 16, 12, 37, 45, TimeSpan.FromHours(3))));
        Assert.That(result[id]!.WeightKg, Is.EqualTo(0.245m));
        Assert.That(result[other], Is.Null);
    }

    private static readonly Guid MainDec = Guid.NewGuid();

    private static AltaParcel Parcel(string number, Guid? dec = null) =>
        new(Guid.NewGuid(), number, "{\\<" + (dec ?? MainDec).ToString("D") + "}", "10", null);

    private static AltaRegisterSnapshot Snapshot(string cmr, params AltaParcel[] parcels)
    {
        var declarations = parcels.Where(parcel => AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference) is not null)
            .GroupBy(parcel => AltaSourceValues.ReadDeclarationReference(parcel.DeclarationReference)!.Value)
            .ToDictionary(group => group.Key, group => new AltaDeclaration(group.Key, group.Key.ToString("N"), "10", null, null));
        return new AltaRegisterSnapshot(cmr, declarations, parcels.ToDictionary(parcel => parcel.Id));
    }

    private sealed class MonitoringFixture : IDisposable
    {
        private readonly RecordingHttpHandler _handler;
        private readonly AltaCoreClient _core;
        private readonly AltaMonitor _monitor;
        public SourceFixture Source { get; } = new();
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
            new(Source, _core, configuration, Clock, NullLogger<AltaMonitor>.Instance);

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
        public RuntimeConfiguration GetValidated(bool allowDisabledConsole = false, DateTimeOffset? scheduleFrom = null) => Current;
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
        public List<int> WorklistSizes { get; } = [];
        public int MaxRegistersPerRead { get; set; } = int.MaxValue;
        public Action? AfterRead { get; set; }
        public string? Missing { get; set; }
        public string Description { get; set; } = "Original goods";
        public IReadOnlyList<AltaPayment> Payments { get; set; } = [];

        public Task<AltaSourceRead> ReadAsync(RuntimeDatabaseTarget target, IReadOnlyList<string> cmrs, IReadOnlySet<string> worklist,
            Func<IReadOnlyList<AltaRegisterSnapshot>, IReadOnlyCollection<AltaAssignmentRequest>> selectAssignments,
            CancellationToken token)
        {
            GroupSizes.Add(cmrs.Count);
            WorklistSizes.Add(worklist.Count);
            if (cmrs.Count > MaxRegistersPerRead)
                throw new TimeoutException();
            var snapshots = cmrs.Select(cmr => Snapshots[cmr]).ToArray();
            var goodsRequests = selectAssignments(snapshots);
            if (goodsRequests.Count > 0)
                Lookups.Add(goodsRequests.Select(item => item.Key.ParcelNumber).Distinct(StringComparer.Ordinal).ToArray());
            var goods = goodsRequests.Where(item => item.Key.ParcelNumber != Missing)
                .ToDictionary(item => item.Key, _ => new AltaGoodsLookup([new(Description, null, 1, 0)]));
            var shipments = goodsRequests.GroupBy(request => request.DeclarationId).ToDictionary(group => group.Key,
                group => (IReadOnlyDictionary<Guid, AltaShipment?>)group
                    .SelectMany(request => request.IndPostIds.Select(id => Snapshots[request.Key.Cmr].Parcels[id]))
                    .DistinctBy(parcel => parcel.Id)
                    .ToDictionary(parcel => parcel.Id, parcel => (AltaShipment?)new AltaShipment(parcel.Number, null, Payments, null)));
            AfterRead?.Invoke();
            return Task.FromResult(new AltaSourceRead(snapshots, goods, shipments));
        }
    }
}
