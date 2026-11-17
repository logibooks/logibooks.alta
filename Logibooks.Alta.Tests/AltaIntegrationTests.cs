// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Net;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Hosting;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class AltaIntegrationTests
{
    [Test]
    public async Task WorklistUsesExactNameAndKeepsLastListStaleUntilSuccessfulEmptyRefresh()
    {
        using var handler = new RecordingHttpHandler((_, call, _) => Task.FromResult(call switch
        {
            1 => Reply("""{"cmrs":["CMR-1"]}"""),
            2 => Reply("""{"code":"Denied"}""", HttpStatusCode.Forbidden),
            _ => Reply("""{"cmrs":[]}""")
        }));
        var options = new TestOptionsMonitor<CoreOptions>(TestCoreOptions());
        var client = CreateClient(handler, options);
        var scope = client.CaptureConfiguration();
        const string target = "Alta East";

        Assert.That(await client.RefreshWorklistAsync(scope, target, CancellationToken.None), Is.EqualTo(new[] { "CMR-1" }));
        Assert.That(handler.Requests.First().Uri!.AbsoluteUri, Is.EqualTo("https://core.example/root/api/alta/v1/databases/Alta%20East/worklist"));
        options.Set(TestCoreOptions());
        scope = client.CaptureConfiguration();
        Assert.That(async () => await client.RefreshWorklistAsync(scope, target, CancellationToken.None),
            Throws.TypeOf<CoreRequestException>().With.Property(nameof(CoreRequestException.StatusCode)).EqualTo(HttpStatusCode.Forbidden));
        Assert.That(client.GetWorklistStatus(target), Is.EqualTo(new AltaWorklistStatus(
            client.GetWorklistStatus(target)!.Cmrs, true, "HTTP 403 (Denied)")));
        Assert.That(client.GetWorklistStatus(target)!.Cmrs, Is.EqualTo(new[] { "CMR-1" }));
        Assert.That(await client.RefreshWorklistAsync(scope, target, CancellationToken.None), Is.Empty);
        Assert.That(client.GetWorklistStatus(target)!.IsStale, Is.False);
        var differentAccount = TestCoreOptions();
        differentAccount.Email = "other@test";
        options.Set(differentAccount);
        Assert.That(client.GetWorklistStatus(target), Is.Null);
    }

    [Test]
    public async Task BatchSubmitsOneDecWithExplicitNullableUinAndReturnsMixedItemResults()
    {
        using var handler = new RecordingHttpHandler((_, _, _) => Task.FromResult(Reply("""
            {"cmrNumber":"CMR-1","decCode":"DEC-1","acceptedCount":2,
             "results":[{"index":0,"outcome":"applied"},{"index":1,"outcome":"error","code":"ErrorParcelNotFound","message":"Не найдено"}]}
            """)));
        var client = CreateClient(handler);
        var batch = Batch() with { Assignments = [Item(), Item() with { Uin = "00012", Occurrence = 2 }] };
        var result = await client.SubmitAsync(client.CaptureConfiguration(), "Alta", batch, CancellationToken.None);
        var request = handler.Requests.Single();
        using var json = JsonDocument.Parse(request.Body!);
        var body = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Uri!.AbsolutePath, Is.EqualTo("/root/api/alta/v1/databases/Alta/parcel-change-batches"));
            Assert.That(body.GetProperty("decCode").GetString(), Is.EqualTo("DEC-1"));
            Assert.That(body.GetProperty("assignments")[0].GetProperty("uin").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(body.GetProperty("assignments")[0].GetProperty("parcelNumber").GetString(), Is.EqualTo("00042"));
            Assert.That(body.GetProperty("assignments")[1].GetProperty("uin").GetString(), Is.EqualTo("00012"));
            Assert.That(body.TryGetProperty("sequence", out _), Is.False);
            Assert.That(result.AcceptedCount, Is.EqualTo(2));
            Assert.That(result.Results[1], Is.EqualTo(new AltaItemResult(1, "error", "ErrorParcelNotFound", "Не найдено")));
        });
    }

    [TestCase("lost")]
    [TestCase("invalid")]
    [TestCase("unknown")]
    public void UnknownSubmissionDoesNotRetry(string failure)
    {
        using var handler = new RecordingHttpHandler((_, _, _) => failure switch
        {
            "lost" => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection lost")),
            "invalid" => Task.FromResult(Reply("{}")),
            _ => Task.FromResult(Reply("""{"code":"AltaOutcomeUnknown"}""", HttpStatusCode.InternalServerError))
        });
        var client = CreateClient(handler);
        Assert.That(async () => await client.SubmitAsync(client.CaptureConfiguration(), "Alta", Batch(), CancellationToken.None),
            Throws.TypeOf<AltaOutcomeUnknownException>());
        Assert.That(handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public void ConflictRefreshesWorklistWithoutResendingBatch()
    {
        using var handler = new RecordingHttpHandler((_, call, _) => Task.FromResult(call == 1
            ? Reply("""{"code":"AltaCmrUnavailable"}""", HttpStatusCode.Conflict)
            : Reply("""{"cmrs":[]}""")));
        var client = CreateClient(handler);
        Assert.That(async () => await client.SubmitAsync(client.CaptureConfiguration(), "Alta", Batch(), CancellationToken.None),
            Throws.TypeOf<CoreRequestException>().With.Property(nameof(CoreRequestException.Code)).EqualTo("AltaCmrUnavailable"));
        Assert.That(handler.Requests.Select(request => request.Method), Is.EqualTo(new[] { HttpMethod.Post, HttpMethod.Get }));
        Assert.That(client.GetWorklistStatus("Alta")!.Cmrs, Is.Empty);
    }

    [Test]
    public async Task ConflictMarksWorklistStaleWhenRefreshIsCancelled()
    {
        using var stopping = new CancellationTokenSource();
        using var handler = new RecordingHttpHandler((_, call, token) =>
        {
            if (call == 1)
                return Task.FromResult(Reply("""{"cmrs":["CMR-1"]}"""));
            if (call == 2)
                return Task.FromResult(Reply("""{"code":"AltaScopeChanged"}""", HttpStatusCode.Conflict));
            stopping.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        var client = CreateClient(handler);
        var scope = client.CaptureConfiguration();
        await client.RefreshWorklistAsync(scope, "Alta", CancellationToken.None);
        Assert.That(async () => await client.SubmitAsync(scope, "Alta", Batch(), stopping.Token),
            Throws.TypeOf<CoreRequestException>().With.Property(nameof(CoreRequestException.Code)).EqualTo("AltaScopeChanged"));
        Assert.That(client.GetWorklistStatus("Alta")!.IsStale, Is.True);
        Assert.That(client.GetWorklistStatus("Alta")!.Cmrs, Is.EqualTo(new[] { "CMR-1" }));
        Assert.That(handler.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task ConflictAfterConfigurationReloadStillMarksSameAccountWorklistStale()
    {
        var options = new TestOptionsMonitor<CoreOptions>(TestCoreOptions());
        using var handler = new RecordingHttpHandler((_, call, _) =>
        {
            if (call == 1)
                return Task.FromResult(Reply("""{"cmrs":["CMR-1"]}"""));
            options.Set(TestCoreOptions());
            return Task.FromResult(Reply("""{"code":"AltaScopeChanged"}""", HttpStatusCode.Conflict));
        });
        var client = CreateClient(handler, options);
        var scope = client.CaptureConfiguration();
        await client.RefreshWorklistAsync(scope, "Alta", CancellationToken.None);
        Assert.That(async () => await client.SubmitAsync(scope, "Alta", Batch(), CancellationToken.None),
            Throws.TypeOf<CoreRequestException>().With.Property(nameof(CoreRequestException.Code)).EqualTo("AltaScopeChanged"));
        Assert.That(client.GetWorklistStatus("Alta")!.IsStale, Is.True);
        Assert.That(client.GetWorklistStatus("Alta")!.Cmrs, Is.EqualTo(new[] { "CMR-1" }));
        Assert.That(handler.Calls, Is.EqualTo(2));
    }

    [TestCase("n", false)]
    [TestCase("y", true)]
    public async Task ConsolePreviewsAndRequiresConfirmationForSingleItem(string confirmation, bool sendsUpdate)
    {
        using var handler = new RecordingHttpHandler((request, _, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Reply("""{"cmrs":["CMR-1"]}""")
            : Reply("""{"cmrNumber":"CMR-1","decCode":"DEC-1","acceptedCount":1,"results":[{"index":0,"outcome":"applied"}]}""")));
        var terminal = new ScriptedTerminal("submit", "Alta", "CMR-1", "DEC-1", "00042", "", "", "10", "40", "",
            "dutiesAndTaxes", "125.50", "rub", "", "6105100000", "0.265", confirmation, "status", "exit");
        using var lifetime = new TestLifetime();
        var console = new AltaConsole(terminal, CreateClient(handler), new UnusedProbe(),
            new TestOptionsMonitor<DatabaseMonitoringOptions>(new() { Targets = [new() { Name = "Alta" }] }),
            new TestOptionsMonitor<ServiceOptions>(new()), lifetime);

        await console.RunAsync(CancellationToken.None, lifetime.ApplicationStopping);

        Assert.That(lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
        Assert.That(terminal.Output, Has.Some.Contains("\"uin\": null"));
        Assert.That(handler.Requests.Count(request => request.Method == HttpMethod.Post), Is.EqualTo(sendsUpdate ? 1 : 0));
        Assert.That(handler.Requests.Count(request => request.Method == HttpMethod.Get), Is.EqualTo(sendsUpdate ? 2 : 1));
        if (sendsUpdate)
        {
            using var document = JsonDocument.Parse(handler.Requests.Last().Body!);
            var item = document.RootElement.GetProperty("assignments").EnumerateArray().Single();
            Assert.That(item.GetProperty("occurrence").GetInt32(), Is.EqualTo(1));
            Assert.That(item.GetProperty("payments")[0].GetProperty("currency").GetString(), Is.EqualTo("RUB"));
            Assert.That(terminal.Output, Has.Some.Contains("applied (saved decisions: 1)"));
        }
    }

    private static AltaBatch Batch() => new("CMR-1", "DEC-1", [Item()]);
    private static AltaAssignment Item() => new("00042", null, 1, new(10, "40"), []);

    private static CoreOptions TestCoreOptions() => new() { BaseUrl = "https://core.example/root", Email = "alta@test", Password = "secret" };

    private static AltaCoreClient CreateClient(HttpMessageHandler handler, TestOptionsMonitor<CoreOptions>? options = null) => new(
        options ?? new TestOptionsMonitor<CoreOptions>(TestCoreOptions()),
        new FreshHttpClientFactory(handler), new HttpRetryExecutor(new NoRetryDelay()), new CoreUris(false));

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class FreshHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ScriptedTerminal(params string[] lines) : IAltaConsoleTerminal
    {
        private readonly Queue<string> _lines = new(lines);
        public List<string> Output { get; } = [];
        public Task<string?> ReadLineAsync(string prompt, CancellationToken token) =>
            Task.FromResult(_lines.TryDequeue(out var line) ? line : null);
        public void WriteLine(string text) => Output.Add(text);
    }

    private sealed class UnusedProbe : ICoreApiProbe
    {
        public Task ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
