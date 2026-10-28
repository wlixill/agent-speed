using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux;

public sealed record LinuxCostStatus(bool NetworkEnabled, bool IsBusy, int PriceVersion, string PriceDate,
    DateTimeOffset? LastPriceCheck, DateTimeOffset? LastPriceSuccess, string? PriceError, bool PriceNewerSchema,
    string CurrencyCode, string? RatesProvider, DateTimeOffset? RatesDate, bool RatesFallback, bool RatesStale,
    bool CurrencyRateFallback, bool NeedsAttribution, string? RatesError, DateTimeOffset? LastRatesCheck,
    string? InitializationWarning)
{
    public override string ToString()
    {
        static string When(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未联网检查";
        var network = IsBusy ? "正在更新公开价格／汇率" : NetworkEnabled ? "公开数据更新已启用" : "离线：使用内置或已缓存数据";
        var price = $"价格表 v{PriceVersion} · {PriceDate} · 最近检查 {When(LastPriceCheck)}";
        if (PriceError is not null) price += " · 检查失败：" + PriceError;
        if (PriceNewerSchema) price += " · 新价格表需要更新应用";
        var fx = CurrencyCode == Currency.UsdCode ? "USD · 美元无需汇率更新"
            : $"{CurrencyCode} 汇率 · {RatesDate?.UtcDateTime:yyyy-MM-dd} · "
                + (RatesFallback ? "内置参考汇率" : RatesProvider ?? "来源未知")
                + (CurrencyRateFallback && !RatesFallback ? " · 所选币种使用内置参考值" : "")
                + (RatesStale ? " · 汇率超过 7 天，仍按该日期换算" : "");
        if (RatesError is not null) fx += " · 更新失败：" + RatesError;
        return string.Join('\n', new[] { network, price, fx, InitializationWarning }.Where(line => !string.IsNullOrEmpty(line)));
    }
}

/// <summary>
/// Shared live/history price book plus opt-in public-data updates. Call Changed handlers through the UI dispatcher.
/// Construction restores persisted daily price bindings before either monitor is started, without any HTTP request.
/// </summary>
public sealed class LinuxCostService : IDisposable
{
    private readonly object _gate = new();
    private readonly PriceTableStore _priceStore;
    private readonly PriceTableSync _priceSync;
    private readonly ExchangeRateStore _rateStore;
    private readonly ExchangeRateSync _rateSync;
    private readonly IHttpTransport _transport;
    private readonly bool _ownsTransport;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource? _round;
    private CancellationTokenSource? _ratesRound;
    private Timer? _timer;
    private Task? _running;
    private DateTimeOffset _launchedAt;
    private PriceSyncState _priceState;
    private RatesInUse _rates;
    private string _currencyCode;
    private bool _networkEnabled;
    private bool _busy;
    private bool _started;
    private bool _disposed;
    private bool _newerSchema;
    private string? _priceError;
    private string? _ratesError;
    private DateTimeOffset? _lastRatesCheck;
    private DateTimeOffset? _lastRatesFailure;
    private string? _initializationWarning;
    private bool _activePrices;
    private bool _activeRates;
    private string? _activeRatesCurrency;
    private bool _pricesAttemptedInPump;
    private string? _ratesAttemptedCurrencyInPump;
    private bool _pendingPrices;
    private (string Currency, int Generation)? _pendingRates;
    private int _ratesGeneration;

    public LinuxCostService(AppStorage storage, bool networkEnabled = false, string currency = "USD",
        IHttpTransport? transport = null, Func<DateTimeOffset>? now = null, string version = "1.4.1")
    {
        ArgumentNullException.ThrowIfNull(storage);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _launchedAt = _now();
        _lifetimeToken = _lifetime.Token;
        CurrencyTable = AgentSpeed.Core.Money.CurrencyTable.Bundled;
        _currencyCode = CurrencyTable.CurrencyOrUsd((currency ?? "USD").Trim().ToUpperInvariant()).Code;
        _networkEnabled = networkEnabled;
        _transport = transport ?? new HttpClientTransport();
        _ownsTransport = transport is null;
        _priceStore = new(storage.DataDirectory);
        _rateStore = new(storage.DataDirectory);
        var tables = new List<PriceTable> { AgentSpeed.Core.Pricing.PriceBook.Bundled };
        if (_priceStore.LoadTable() is { } stored) tables.Add(stored);
        PriceBook = new(tables);
        try { HistoryService.RestorePricing(PriceBook, storage.HistoryPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            || exception.GetType().Name == "SqliteException")
        {
            _initializationWarning = "无法恢复历史价格绑定；历史数据未改动，请检查用量历史中的读取状态。";
        }
        _priceState = _priceStore.LoadState();
        _priceError = _priceState.LastError;
        _rates = new(_rateStore.Load()?.ToRateSet(), CurrencyTable.FallbackRates);
        var safeVersion = !string.IsNullOrEmpty(version) && version.Length <= 64
            && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+') ? version : "local";
        var userAgent = "AgentSpeed/" + safeVersion + " (Linux)";
        _priceSync = new(_priceStore, _transport, userAgent, now: _now);
        _rateSync = new(_rateStore, _transport, userAgent, CurrencyTable, now: _now);
    }

    public PriceBook PriceBook { get; }
    public CurrencyTable CurrencyTable { get; }
    public string CurrencyCode { get { lock (_gate) return _currencyCode; } }
    public Currency Currency => CurrencyTable.CurrencyOrUsd(CurrencyCode);
    public RatesInUse Rates { get { lock (_gate) return _rates; } }
    public event Action? Changed;
    public event Action? PricesChanged;

    public LinuxCostStatus Status
    {
        get
        {
            lock (_gate)
            {
                var missingSelected = _currencyCode != Core.Money.Currency.UsdCode
                    && (!_rates.Rates.TryGetValue(_currencyCode, out var selectedRate) || selectedRate <= 0);
                // A provider can omit a supported currency. Show the date of the fallback actually used for it.
                var date = Instant(missingSelected ? CurrencyTable.FallbackRates.DateMs : _rates.DateMs);
                var fallback = _rates.IsFallback || missingSelected;
                return new(_networkEnabled, _busy, PriceBook.CurrentVersion, PriceBook.Current.Updated,
                    Instant(_priceState.LastAttemptMs), Instant(_priceState.LastSuccessMs), _priceError, _newerSchema,
                    _currencyCode, fallback ? null : _rates.Provider, date, fallback,
                    date is null || _now() - date.Value > TimeSpan.FromMilliseconds(RatesInUse.StaleAfterMs),
                    missingSelected, !fallback && _rates.NeedsAttribution, _ratesError, _lastRatesCheck, _initializationWarning);
            }
        }
    }

    public string FormatHistoryMoney(HistoryCost cost, bool hasData = true) =>
        MoneyPresentation.History(cost, hasData, Currency, Rates, CurrencyTable);
    public string FormatLiveMoney(LiveCost cost, double liveOutput) =>
        MoneyPresentation.Live(cost, liveOutput, Currency, Rates, CurrencyTable);

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _launchedAt = _now();
            // Check twice a minute so sleep/resume and wall-clock changes do not postpone a due daily round.
            _timer = new(state => { _ = CheckScheduleAsync(); }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public void SetOptions(bool networkEnabled, string currency)
    {
        CancellationTokenSource? cancelAll = null, cancelRates = null;
        var selectionFetch = false;
        lock (_gate)
        {
            if (_disposed) return;
            var selected = CurrencyTable.CurrencyOrUsd((currency ?? "USD").Trim().ToUpperInvariant()).Code;
            var turnedOn = networkEnabled && !_networkEnabled;
            selectionFetch = (_started || _busy) && networkEnabled && _networkEnabled && selected != _currencyCode
                && selected != Core.Money.Currency.UsdCode
                && (!HasRate(selected) || ExchangeRateSync.FetchesOnSelection(Instant(_rates.FetchedMs), _lastRatesFailure, _now())
                    || _busy && _activeRates && _activeRatesCurrency is { } active && active != selected);
            if (turnedOn) _launchedAt = _now();
            _networkEnabled = networkEnabled;
            _currencyCode = selected;
            if (!networkEnabled)
            {
                cancelAll = _round;
                _pendingPrices = false;
                _pendingRates = null;
            }
            if (!networkEnabled || selected == Core.Money.Currency.UsdCode)
            {
                cancelRates = _ratesRound;
                _pendingRates = null;
            }
            else if (_pendingRates is { } pending && pending.Currency != selected)
                _pendingRates = null;
        }
        Cancel(cancelAll); Cancel(cancelRates);
        Changed?.Invoke();
        if (selectionFetch) _ = QueueRound(prices: false, rates: true);
    }

    /// <summary>The explicit refresh obeys the network switch; it never fetches exchange rates for USD.</summary>
    public Task RefreshAsync() => QueueRound(prices: true, rates: CurrencyCode != Core.Money.Currency.UsdCode);

    /// <summary>Also useful on resume; clock-injected tests verify the real shared schedules without sleeping.</summary>
    public Task CheckScheduleAsync()
    {
        bool prices, rates;
        lock (_gate)
        {
            if (_disposed || !_networkEnabled) return Task.CompletedTask;
            var now = _now();
            prices = now >= PriceTableSync.NextAttempt(_launchedAt, _priceState, now);
            rates = _currencyCode != Core.Money.Currency.UsdCode
                && now >= ExchangeRateSync.NextAttempt(_launchedAt,
                    HasRate(_currencyCode) ? Instant(_rates.FetchedMs) : null, _lastRatesFailure, now);
        }
        return QueueRound(prices, rates);
    }

    private Task QueueRound(bool prices, bool rates)
    {
        Task result;
        lock (_gate)
        {
            if (_disposed || !_networkEnabled || !prices && !rates) return Task.CompletedTask;
            if (_busy)
            {
                var cancelled = _round?.IsCancellationRequested == true;
                if (prices && (cancelled || !_activePrices && !_pricesAttemptedInPump)) _pendingPrices = true;
                if (rates && _currencyCode != Core.Money.Currency.UsdCode)
                {
                    var covered = !cancelled && _ratesRound?.IsCancellationRequested != true
                        && (_activeRates && (_activeRatesCurrency is null || _activeRatesCurrency == _currencyCode)
                            || _ratesAttemptedCurrencyInPump == _currencyCode);
                    if (!covered) _pendingRates = (_currencyCode, _ratesGeneration);
                    else if (_pendingRates is { } pending && pending.Currency != _currencyCode) _pendingRates = null;
                }
                return _running ?? Task.CompletedTask;
            }
            _busy = true;
            _activePrices = prices;
            _activeRates = rates;
            _activeRatesCurrency = null;
            _pricesAttemptedInPump = false;
            _ratesAttemptedCurrencyInPump = null;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            result = _running = completion.Task;
            _ = Task.Run(() => RunPumpAsync(prices, rates, completion));
        }
        Changed?.Invoke();
        return result;
    }

    private async Task RunPumpAsync(bool prices, bool rates, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                CancellationTokenSource round;
                lock (_gate)
                {
                    if (_disposed || !_networkEnabled) { FinishPump(completion.Task); break; }
                    _activePrices = prices;
                    _activeRates = rates;
                    _activeRatesCurrency = null;
                    round = _round = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                }
                await RunRoundAsync(prices, rates, round).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || !_networkEnabled) { FinishPump(completion.Task); break; }
                    prices = _pendingPrices;
                    _pendingPrices = false;
                    var pending = _pendingRates;
                    _pendingRates = null;
                    // An incoming list may already cover the newly selected currency. Otherwise do not let its
                    // fresh global fetchedMs postpone a missing currency for another 24 hours.
                    rates = pending is { } requested && requested.Currency == _currencyCode
                        && _currencyCode != Core.Money.Currency.UsdCode
                        && (requested.Generation == _ratesGeneration || !HasRate(_currencyCode));
                    if (!prices && !rates) { FinishPump(completion.Task); break; }
                    _activePrices = prices;
                    _activeRates = rates;
                    _activeRatesCurrency = null;
                }
            }
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_gate) FinishPump(completion.Task);
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
            if (!_disposed) Changed?.Invoke();
        }
    }

    // Called with _gate held; ownership check prevents an exiting pump from clearing a newer one.
    private void FinishPump(Task owner)
    {
        if (!ReferenceEquals(_running, owner)) return;
        _busy = false;
        _running = null;
        _activePrices = _activeRates = false;
        _activeRatesCurrency = null;
        _pricesAttemptedInPump = false;
        _ratesAttemptedCurrencyInPump = null;
        _pendingPrices = false;
        _pendingRates = null;
    }

    private bool HasRate(string currency) => _rates.Rates.TryGetValue(currency, out var rate) && rate > 0;

    private async Task RunRoundAsync(bool prices, bool rates, CancellationTokenSource round)
    {
        try
        {
            var token = round.Token;
            token.ThrowIfCancellationRequested();
            if (prices)
            {
                lock (_gate) _pricesAttemptedInPump = true;
                var result = await _priceSync.RunAsync(PriceBook.CurrentVersion, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var accepted = false;
                lock (_gate)
                {
                    if (!_networkEnabled || _disposed) return;
                    _priceState = result.State;
                    _priceError = result.Outcome is PriceSyncOutcome.Failed failed ? failed.Reason : null;
                    if (result.Outcome is not PriceSyncOutcome.Failed) _newerSchema = result.Outcome is PriceSyncOutcome.NewerSchema;
                    if (result.Table is { } table) accepted = PriceBook.Add(table);
                }
                if (accepted) PricesChanged?.Invoke();
                Changed?.Invoke();
            }
            string selected;
            CancellationTokenSource rateRound;
            lock (_gate)
            {
                if (!rates || !_networkEnabled || _disposed || _currencyCode == Core.Money.Currency.UsdCode) return;
                selected = _currencyCode;
                _activeRatesCurrency = selected;
                _ratesAttemptedCurrencyInPump = selected;
                _lastRatesCheck = _now();
                rateRound = _ratesRound = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            try
            {
                var outcome = await _rateSync.RunAsync(selected, rateRound.Token).ConfigureAwait(false);
                rateRound.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!_networkEnabled || _disposed) return;
                    switch (outcome)
                    {
                        case ExchangeRateOutcome.Updated updated:
                            _rates = new(updated.Set, CurrencyTable.FallbackRates);
                            _ratesGeneration++;
                            _ratesError = null; _lastRatesFailure = null;
                            break;
                        case ExchangeRateOutcome.Unchanged unchanged:
                            _rates = new(unchanged.Set, CurrencyTable.FallbackRates);
                            _ratesGeneration++;
                            _ratesError = null; _lastRatesFailure = null;
                            break;
                        case ExchangeRateOutcome.Failed failed:
                            _ratesError = failed.Reason; _lastRatesFailure = _now();
                            break;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_ratesRound, rateRound)) _ratesRound = null;
                    _activeRates = false;
                    if (rateRound.IsCancellationRequested) _ratesAttemptedCurrencyInPump = null;
                }
                rateRound.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _priceError = "无法读取或保存公开数据缓存";
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_round, round)) _round = null;
                if (round.IsCancellationRequested)
                {
                    _activePrices = _activeRates = false;
                    _pricesAttemptedInPump = false;
                    _ratesAttemptedCurrencyInPump = null;
                }
            }
            round.Dispose();
            if (!_disposed) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? round, rates;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingPrices = false;
            _pendingRates = null;
            _timer?.Dispose();
            round = _round; rates = _ratesRound;
        }
        Cancel(round); Cancel(rates); Cancel(_lifetime);
        if (_ownsTransport && _transport is IDisposable transport) transport.Dispose();
        _lifetime.Dispose();
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static DateTimeOffset? Instant(long? milliseconds)
    {
        if (milliseconds is not { } value) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
