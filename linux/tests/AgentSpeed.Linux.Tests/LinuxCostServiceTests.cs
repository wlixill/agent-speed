using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxCostServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-speed-cost-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OfflineDefaultAndManualRefreshNeverSendRequestsAndAllCurrenciesRemainAvailable()
    {
        var transport = new FakeTransport();
        using var cost = new LinuxCostService(new AppStorage(_directory), transport: transport, now: () => Noon);
        cost.Start();
        await cost.RefreshAsync();
        await cost.CheckScheduleAsync();
        cost.SetOptions(false, "CNY");
        await cost.RefreshAsync();
        Assert.Empty(transport.Requests);
        Assert.Equal(38, cost.CurrencyTable.Currencies.Count);
        Assert.Equal("CNY", cost.CurrencyCode);
        Assert.False(cost.Status.NetworkEnabled);
        Assert.False(cost.Status.IsBusy);
        Assert.True(cost.Status.RatesFallback);
        Assert.Contains("离线", cost.Status.ToString());
    }

    [Fact]
    public async Task ManualPriceRefreshPersistsANewerTableAndUsdNeverFetchesExchangeRates()
    {
        var transport = new FakeTransport();
        var next = PriceBook.Bundled.Version + 1;
        transport.Reply = (request, _) => Task.FromResult(new HttpFetchResponse(200, NewTable(next), "\"new-table\"", request.Url));
        using var cost = new LinuxCostService(new AppStorage(_directory), true, transport: transport, now: () => Noon, version: "1.4.1-4");
        var accepted = 0;
        cost.PricesChanged += () => accepted++;
        await cost.RefreshAsync();
        Assert.Equal(next, cost.PriceBook.CurrentVersion);
        Assert.Equal(next, new PriceTableStore(_directory).LoadTable()?.Version);
        Assert.Equal(1, accepted);
        Assert.Single(transport.Requests);
        Assert.Equal(PriceTableSync.Urls[0], transport.Requests.Single().Url);
        Assert.Equal("AgentSpeed/1.4.1-4 (Linux)", transport.Requests.Single().UserAgent);
        Assert.Equal(Noon, cost.Status.LastPriceCheck);
        Assert.Null(cost.Status.PriceError);
        Assert.False(cost.Status.IsBusy);
    }

    [Fact]
    public async Task DailyScheduleUsesLaunchDelayAndStoredSuccessInsteadOfPollingEveryRequest()
    {
        var now = Noon;
        var transport = new FakeTransport
        {
            Reply = (request, _) => Task.FromResult(new HttpFetchResponse(200, NewTable(PriceBook.Bundled.Version), finalUrl: request.Url)),
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, transport: transport, now: () => now);
        now = now.AddSeconds(59);
        await cost.CheckScheduleAsync();
        Assert.Empty(transport.Requests);
        now = now.AddSeconds(1);
        await cost.CheckScheduleAsync();
        Assert.Single(transport.Requests);
        now = now.AddHours(23);
        await cost.CheckScheduleAsync();
        Assert.Single(transport.Requests);
        now = now.AddHours(1);
        await cost.CheckScheduleAsync();
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task NonUsdRefreshUsesPublicUsdListsCachesRatesAndPreservesConversionOffline()
    {
        var transport = new FakeTransport
        {
            Reply = (request, _) => Task.FromResult(request.Url.Host == "open.er-api.com"
                ? new HttpFetchResponse(200, RateBody(), "\"rates\"", request.Url)
                : new HttpFetchResponse(503, finalUrl: request.Url)),
        };
        var storage = new AppStorage(_directory);
        using (var cost = new LinuxCostService(storage, true, "CNY", transport, () => Noon))
        {
            await cost.RefreshAsync();
            Assert.Equal(7.25m, cost.Rates.Rates["CNY"]);
            Assert.False(cost.Status.RatesFallback);
            Assert.True(cost.Status.NeedsAttribution);
            Assert.Equal(ExchangeRates.ErApiHost, cost.Status.RatesProvider);
            Assert.Null(cost.Status.RatesError);
            Assert.NotNull(new ExchangeRateStore(_directory).Load());
            Assert.DoesNotContain(transport.Requests, request => request.Url.AbsoluteUri.Contains("cny", StringComparison.OrdinalIgnoreCase));
        }
        var offlineTransport = new FakeTransport();
        using var offline = new LinuxCostService(storage, false, "CNY", offlineTransport, () => Noon.AddDays(8));
        await offline.RefreshAsync();
        Assert.Empty(offlineTransport.Requests);
        Assert.Equal(7.25m, offline.Rates.Rates["CNY"]);
        Assert.True(offline.Status.RatesStale);
        Assert.Contains("超过 7 天", offline.Status.ToString());
    }

    [Fact]
    public async Task NetworkOffCancelsAnInFlightRequestAndDoesNotStartANewOne()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            Reply = async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return new HttpFetchResponse(503);
            },
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, transport: transport, now: () => Noon);
        var refresh = cost.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cost.Status.IsBusy);
        cost.SetOptions(false, "USD");
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cost.RefreshAsync();
        Assert.Single(transport.Requests);
        Assert.False(cost.Status.NetworkEnabled);
        Assert.False(cost.Status.IsBusy);
        Assert.Equal(PriceBook.Bundled.Version, cost.PriceBook.CurrentVersion);
    }

    [Fact]
    public async Task FailedRefreshKeepsTheLastValidPriceTableAndReportsTheFailure()
    {
        var next = PriceBook.Bundled.Version + 1;
        new PriceTableStore(_directory).SaveTable(NewTable(next));
        var transport = new FakeTransport();
        using var cost = new LinuxCostService(new AppStorage(_directory), true, transport: transport, now: () => Noon);
        await cost.RefreshAsync();
        Assert.Equal(next, cost.PriceBook.CurrentVersion);
        Assert.Equal(next, new PriceTableStore(_directory).LoadTable()?.Version);
        Assert.NotNull(cost.Status.PriceError);
        Assert.Contains("失败", cost.Status.ToString());
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public void MissingSelectedCurrencyReportsTheDateOfItsActualBundledFallback()
    {
        var store = new ExchangeRateStore(_directory);
        var partialRates = new ExchangeRateSet(ExchangeRates.ErApiHost, ExchangeRateSync.Providers[0].Url.AbsoluteUri,
            Noon.ToUnixTimeMilliseconds(), Noon.ToUnixTimeMilliseconds(), new Dictionary<string, decimal> { ["USD"] = 1 });
        store.Save(StoredRates.From(partialRates, new Dictionary<string, string>()));
        using var cost = new LinuxCostService(new AppStorage(_directory), false, "CNY", new FakeTransport(), () => Noon);
        Assert.True(cost.Status.CurrencyRateFallback);
        Assert.True(cost.Status.RatesFallback);
        Assert.Equal(CurrencyTable.Bundled.FallbackRates.DateMs, cost.Status.RatesDate?.ToUnixTimeMilliseconds());
        Assert.Null(cost.Status.RatesProvider);
        Assert.False(cost.Status.NeedsAttribution);
        Assert.Contains("内置参考汇率", cost.Status.ToString());
    }

    [Fact]
    public async Task AutomaticFailuresUseTheSharedHourlyThenDailyBackoff()
    {
        var now = Noon;
        var transport = new FakeTransport();
        using var cost = new LinuxCostService(new AppStorage(_directory), true, transport: transport, now: () => now);
        now = now.AddSeconds(60);
        await cost.CheckScheduleAsync();
        Assert.Equal(2, transport.Requests.Count);
        now = now.AddMinutes(59);
        await cost.CheckScheduleAsync();
        Assert.Equal(2, transport.Requests.Count);
        now = now.AddMinutes(1);
        await cost.CheckScheduleAsync();
        Assert.Equal(4, transport.Requests.Count);
        now = now.AddHours(23);
        await cost.CheckScheduleAsync();
        Assert.Equal(4, transport.Requests.Count);
        now = now.AddHours(1);
        await cost.CheckScheduleAsync();
        Assert.Equal(6, transport.Requests.Count);
    }

    [Fact]
    public async Task SelectingUsdCancelsOnlyTheInFlightExchangeRateWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            Reply = async (request, token) =>
            {
                if (request.Url == PriceTableSync.Urls[0]) return new HttpFetchResponse(200, NewTable(PriceBook.Bundled.Version), finalUrl: request.Url);
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpFetchResponse(503);
            },
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, "CNY", transport, () => Noon);
        var refresh = cost.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cost.SetOptions(true, "USD");
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cost.Status.NetworkEnabled);
        Assert.False(cost.Status.IsBusy);
        Assert.Equal("USD", cost.CurrencyCode);
        Assert.Equal(Noon, cost.Status.LastPriceSuccess);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task DisposeCancelsRequestsAndPreventsSubsequentRefreshes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            Reply = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpFetchResponse(503);
            },
        };
        var cost = new LinuxCostService(new AppStorage(_directory), true, transport: transport, now: () => Noon);
        var refresh = cost.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(refresh, cost.RefreshAsync());
        cost.Dispose();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await cost.RefreshAsync();
        Assert.Single(transport.Requests);
        Assert.False(cost.Status.IsBusy);
    }

    [Fact]
    public async Task ChangingCurrencyWhileBusyQueuesMissingCurrencyAndTheOriginalTaskWaitsForIt()
    {
        var cnyEntered = Signal(); var releaseCny = Signal();
        var jpyEntered = Signal(); var releaseJpy = Signal();
        var erCalls = 0;
        var transport = new FakeTransport
        {
            Reply = async (request, token) =>
            {
                if (request.Url == PriceTableSync.Urls[0]) return new HttpFetchResponse(200, NewTable(PriceBook.Bundled.Version), finalUrl: request.Url);
                if (request.Url == ExchangeRateSync.Providers[0].Url)
                {
                    if (Interlocked.Increment(ref erCalls) == 1)
                    {
                        cnyEntered.TrySetResult(); await releaseCny.Task.WaitAsync(token);
                        return new HttpFetchResponse(200, RateBody("CNY"), finalUrl: request.Url);
                    }
                    jpyEntered.TrySetResult(); await releaseJpy.Task.WaitAsync(token);
                    return new HttpFetchResponse(200, RateBody("JPY"), finalUrl: request.Url);
                }
                return new HttpFetchResponse(200, FawazBody("JPY"), finalUrl: request.Url);
            },
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, "CNY", transport, () => Noon);
        var refresh = cost.RefreshAsync();
        await cnyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cost.SetOptions(true, "EUR");
        cost.SetOptions(true, "JPY");
        Assert.Same(refresh, cost.RefreshAsync());
        releaseCny.TrySetResult();
        await jpyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(refresh.IsCompleted);
        Assert.True(cost.Status.IsBusy);
        releaseJpy.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("JPY", cost.CurrencyCode);
        Assert.Equal(150m, cost.Rates.Rates["JPY"]);
        Assert.False(cost.Status.CurrencyRateFallback);
        Assert.False(cost.Status.IsBusy);
        Assert.Null(cost.Status.RatesError);
        Assert.Equal(2, erCalls);
        Assert.Equal(4, transport.Requests.Count); // price, CNY, JPY, independent provider confirmation
    }

    [Fact]
    public async Task AConcurrentCurrencyChangeDoesNotRepeatAListThatAlreadyContainsTheSelectedCurrency()
    {
        var entered = Signal(); var release = Signal();
        var transport = new FakeTransport
        {
            Reply = async (request, token) =>
            {
                if (request.Url == PriceTableSync.Urls[0]) return new HttpFetchResponse(200, NewTable(PriceBook.Bundled.Version), finalUrl: request.Url);
                entered.TrySetResult(); await release.Task.WaitAsync(token);
                return new HttpFetchResponse(200, RateBody("CNY", "JPY"), finalUrl: request.Url);
            },
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, "CNY", transport, () => Noon);
        var refresh = cost.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cost.SetOptions(true, "JPY");
        release.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(150m, cost.Rates.Rates["JPY"]);
        Assert.Equal(2, transport.Requests.Count);
        Assert.False(cost.Status.IsBusy);
    }

    [Fact]
    public async Task ManualRefreshDuringAnFxOnlyRoundCoalescesExactlyOnePriceCheck()
    {
        var entered = Signal(); var release = Signal();
        var transport = new FakeTransport
        {
            Reply = async (request, token) =>
            {
                if (request.Url == PriceTableSync.Urls[0]) return new HttpFetchResponse(200, NewTable(PriceBook.Bundled.Version), finalUrl: request.Url);
                entered.TrySetResult(); await release.Task.WaitAsync(token);
                return new HttpFetchResponse(200, RateBody("CNY"), finalUrl: request.Url);
            },
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, "USD", transport, () => Noon);
        cost.Start();
        cost.SetOptions(true, "CNY");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = cost.RefreshAsync();
        Assert.Same(manual, cost.RefreshAsync());
        release.TrySetResult();
        await manual.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, transport.Requests.Count);
        Assert.Single(transport.Requests, request => request.Url == PriceTableSync.Urls[0]);
        Assert.Equal(Noon, cost.Status.LastPriceSuccess);
        Assert.False(cost.Status.IsBusy);
    }

    [Fact]
    public async Task RapidNetworkTogglesDiscardQueuedCurrencyWorkWhenFinallyOffline()
    {
        var entered = Signal(); var release = Signal();
        var transport = new FakeTransport
        {
            Reply = async (request, token) =>
            {
                if (request.Url == PriceTableSync.Urls[0]) return new HttpFetchResponse(200, NewTable(PriceBook.Bundled.Version), finalUrl: request.Url);
                entered.TrySetResult(); await release.Task.WaitAsync(token);
                return new HttpFetchResponse(200, RateBody("CNY"), finalUrl: request.Url);
            },
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, "CNY", transport, () => Noon);
        var refresh = cost.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cost.SetOptions(true, "JPY");
        cost.SetOptions(false, "JPY");
        cost.SetOptions(true, "JPY");
        cost.SetOptions(false, "JPY");
        release.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await cost.RefreshAsync();
        await cost.CheckScheduleAsync();
        Assert.Equal(2, transport.Requests.Count);
        Assert.False(cost.Status.NetworkEnabled);
        Assert.False(cost.Status.IsBusy);
        Assert.False(cost.Rates.Rates.ContainsKey("JPY") && !cost.Status.RatesFallback);
    }

    [Fact]
    public async Task ARecentlyFetchedListMissingTheSelectedCurrencyCannotPostponeItsStartupRefreshForADay()
    {
        var store = new ExchangeRateStore(_directory);
        store.Save(StoredRates.From(new ExchangeRateSet(ExchangeRates.ErApiHost, ExchangeRateSync.Providers[0].Url.AbsoluteUri,
            Noon.ToUnixTimeMilliseconds(), Noon.ToUnixTimeMilliseconds(), new Dictionary<string, decimal> { ["USD"] = 1, ["CNY"] = 7.25m }),
            new Dictionary<string, string>()));
        var now = Noon;
        var transport = new FakeTransport
        {
            Reply = (request, _) => Task.FromResult(new HttpFetchResponse(200,
                request.Url == ExchangeRateSync.Providers[0].Url ? RateBody("JPY") : FawazBody("JPY"), finalUrl: request.Url)),
        };
        using var cost = new LinuxCostService(new AppStorage(_directory), true, "JPY", transport, () => now);
        now = now.AddSeconds(29);
        await cost.CheckScheduleAsync();
        Assert.Empty(transport.Requests);
        now = now.AddSeconds(1);
        await cost.CheckScheduleAsync();
        Assert.Equal(150m, cost.Rates.Rates["JPY"]);
        Assert.False(cost.Status.CurrencyRateFallback);
        Assert.Equal(2, transport.Requests.Count);
        Assert.DoesNotContain(transport.Requests, request => PriceTableSync.AllowedHosts.Contains(request.Url.Host)
            && !request.Url.AbsoluteUri.Contains("currency-api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoresDailyPriceBindingsBeforeExposingTheBookToLiveMonitoring()
    {
        var sessions = Path.Combine(_directory, "sessions");
        Directory.CreateDirectory(sessions);
        string Record(string type, object payload, string timestamp) => JsonSerializer.Serialize(new { timestamp, type, payload });
        File.WriteAllLines(Path.Combine(sessions, "rollout-price-binding.jsonl"), [
            Record("session_meta", new { id = "binding-fixture", cwd = "/synthetic/cost-test" }, "2026-10-20T12:00:00Z"),
            Record("turn_context", new { model = "gpt-6.1-sol" }, "2026-10-20T12:00:01Z"),
            Record("event_msg", new { type = "task_started" }, "2026-10-20T12:00:02Z"),
            Record("token_usage_record", new { response_id = "binding-response", usage = new { input_tokens = 1000, output_tokens = 100 } }, "2026-10-20T12:00:04Z"),
            Record("event_msg", new { type = "task_complete" }, "2026-10-20T12:00:05Z"),
        ]);
        var storage = new AppStorage(Path.Combine(_directory, "data"));
        var originalBook = new PriceBook();
        var hub = new HistoryHub(new SynchronizationContext());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Changed += (_, _) =>
        {
            if (hub.State.Kind == HistoryStateKind.Ready) ready.TrySetResult();
            if (hub.State.Kind == HistoryStateKind.Failed) ready.TrySetException(new Exception(hub.State.Error));
        };
        using (var history = new HistoryService(hub, new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Codex }, CodexRoot: sessions), storage.HistoryPath, originalBook))
        {
            history.Start();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Single(hub.Days);
        var day = hub.Days.Single().Day;
        Assert.Equal(PriceBook.Bundled.Version, hub.Days.Single().PriceVersion);
        new PriceTableStore(storage.DataDirectory).SaveTable(NewTable(PriceBook.Bundled.Version + 1));
        using var cost = new LinuxCostService(storage, transport: new FakeTransport(), now: () => Noon);
        Assert.Equal(PriceBook.Bundled.Version + 1, cost.PriceBook.CurrentVersion);
        Assert.Equal(PriceBook.Bundled.Version, cost.PriceBook.BoundVersion(day));
        Assert.Equal(PriceBook.Bundled.Version, cost.PriceBook.VersionForDay(day));
    }

    private static byte[] NewTable(int version)
    {
        var node = JsonNode.Parse(PriceBook.Bundled.Data)!.AsObject();
        node["version"] = version;
        node["updated"] = Noon.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }

    private static byte[] RateBody(params string[] selected) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        result = "success", base_code = "USD", time_last_update_unix = Noon.ToUnixTimeSeconds(),
        rates = RatesFor(selected),
    });

    private static byte[] FawazBody(params string[] selected) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        date = Noon.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        usd = RatesFor(selected).ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value),
    });

    private static Dictionary<string, decimal> RatesFor(string[] selected)
    {
        var rates = new Dictionary<string, decimal> { ["USD"] = 1 };
        foreach (var code in selected.Length == 0 ? ["CNY"] : selected)
            rates[code] = code switch { "CNY" => 7.25m, "JPY" => 150m, "EUR" => .9m, _ => 1 };
        return rates;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeTransport : IHttpTransport
    {
        public ConcurrentQueue<HttpFetchRequest> Requests { get; } = new();
        public Func<HttpFetchRequest, CancellationToken, Task<HttpFetchResponse>> Reply { get; set; } =
            (request, _) => Task.FromResult(new HttpFetchResponse(503, finalUrl: request.Url));
        public Task<HttpFetchResponse> GetAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            return Reply(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
