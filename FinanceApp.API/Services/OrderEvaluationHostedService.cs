using System.Diagnostics;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public sealed class OrderEvaluationOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 30;
    public int InitialDelaySeconds { get; init; } = 15;
    public int MaxOrdersPerRun { get; init; } = 200;
    public int MaxQuoteAgeMinutes { get; init; } = 15;
    public bool RequireTradingSession { get; init; } = true;
}

internal sealed record OrderEvaluationCycleResult
{
    public bool Disabled { get; init; }
    public bool OverlapSkipped { get; init; }
    public int Selected { get; init; }
    public int Executed { get; init; }
    public int Activated { get; init; }
    public int Rejected { get; init; }
    public int Expired { get; init; }
    public int TrailingReferenceUpdated { get; init; }
    public int SkippedStaleQuote { get; init; }
    public int SkippedDelayedQuote { get; init; }
    public int SkippedOutsideSession { get; init; }
    public int SkippedNoQuote { get; init; }
    public int Failed { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Periodically evaluates open orders against the latest quotes and either activates
/// or executes them through <see cref="IOrderExecutionService"/>.
/// </summary>
public sealed class OrderEvaluationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrderEvaluationHostedService> _logger;
    private readonly ISystemProcessJournalService? _processJournalService;
    private readonly OrderEvaluationOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public OrderEvaluationHostedService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<OrderEvaluationOptions> options,
        ILogger<OrderEvaluationHostedService> logger,
        ISystemProcessJournalService? processJournalService = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
        _processJournalService = processJournalService;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Order evaluation is disabled by configuration.");
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds));
        if (initialDelay > TimeSpan.Zero)
        {
            try
            {
                await _delayAsync(initialDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order evaluation cycle failed unexpectedly.");
            }

            try
            {
                await _delayAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<OrderEvaluationCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new OrderEvaluationCycleResult { Disabled = true };
        }

        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            return new OrderEvaluationCycleResult { OverlapSkipped = true };
        }

        var watch = Stopwatch.StartNew();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var processRunId = await (_processJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
        {
            ProcessType = SystemProcessTypes.OrderEvaluationCycle,
            Trigger = SystemProcessTrigger.Automatic,
            InitialStatus = SystemProcessRunStatus.Pending,
        }, cancellationToken) ?? Task.FromResult(0L));

        if (processRunId > 0)
        {
            await _processJournalService!.MarkRunningAsync(processRunId, cancellationToken);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var executionService = scope.ServiceProvider.GetRequiredService<IOrderExecutionService>();

            var orders = await context.Orders
                .Include(o => o.Stock)
                .Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Triggered)
                .OrderBy(o => o.Id)
                .Take(Math.Max(1, _options.MaxOrdersPerRun))
                .ToListAsync(cancellationToken);

            var executed = 0;
            var activated = 0;
            var rejected = 0;
            var expired = 0;
            var trailingUpdated = 0;
            var skippedStale = 0;
            var skippedDelayed = 0;
            var skippedSession = 0;
            var skippedNoQuote = 0;
            var failed = 0;

            foreach (var order in orders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (order.ExpiresAt is { } expiresAt && expiresAt <= now)
                    {
                        order.Status = OrderStatus.Expired;
                        order.UpdatedAt = now;
                        expired++;
                        continue;
                    }

                    var stock = order.Stock;
                    if (stock is null || stock.CurrentPrice <= 0m)
                    {
                        skippedNoQuote++;
                        continue;
                    }

                    var quoteAt = stock.CurrentPriceAt ?? stock.UpdatedAt;
                    if (quoteAt == default || now - quoteAt > TimeSpan.FromMinutes(_options.MaxQuoteAgeMinutes))
                    {
                        skippedStale++;
                        continue;
                    }

                    if (stock.CurrentPriceIsDelayed)
                    {
                        skippedDelayed++;
                        continue;
                    }

                    if (_options.RequireTradingSession && !IsWithinTradingSession(stock.Exchange, now))
                    {
                        skippedSession++;
                        continue;
                    }

                    var evaluation = OrderTriggerEvaluator.Evaluate(order, stock.CurrentPrice);

                    if (evaluation.TrailingReferencePrice is { } reference &&
                        reference != order.TrailingReferencePrice)
                    {
                        order.TrailingReferencePrice = reference;
                        order.UpdatedAt = now;
                        trailingUpdated++;
                    }

                    switch (evaluation.Action)
                    {
                        case OrderEvaluationAction.Activate:
                            order.Status = OrderStatus.Triggered;
                            order.TriggeredAt ??= now;
                            order.UpdatedAt = now;
                            activated++;
                            break;

                        case OrderEvaluationAction.Execute:
                            var executionResult = await executionService.ExecuteAsync(
                                order,
                                stock.CurrentPrice,
                                OrderExecutionSource.Automatic,
                                evaluation.Reason,
                                cancellationToken);
                            if (executionResult.IsSuccess)
                            {
                                executed++;
                            }
                            else
                            {
                                rejected++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Order evaluation failed for order {OrderId}.", order.Id);
                }
            }

            await context.SaveChangesAsync(cancellationToken);

            watch.Stop();
            var result = new OrderEvaluationCycleResult
            {
                Selected = orders.Count,
                Executed = executed,
                Activated = activated,
                Rejected = rejected,
                Expired = expired,
                TrailingReferenceUpdated = trailingUpdated,
                SkippedStaleQuote = skippedStale,
                SkippedDelayedQuote = skippedDelayed,
                SkippedOutsideSession = skippedSession,
                SkippedNoQuote = skippedNoQuote,
                Failed = failed,
                Duration = watch.Elapsed,
            };

            _logger.LogInformation(
                "Order evaluation cycle completed: selected={Selected} executed={Executed} activated={Activated} rejected={Rejected} expired={Expired} trailingUpdated={TrailingUpdated} stale={Stale} delayed={Delayed} outsideSession={OutsideSession} failed={Failed} durationMs={DurationMs}",
                result.Selected,
                result.Executed,
                result.Activated,
                result.Rejected,
                result.Expired,
                result.TrailingReferenceUpdated,
                result.SkippedStaleQuote,
                result.SkippedDelayedQuote,
                result.SkippedOutsideSession,
                result.Failed,
                (int)result.Duration.TotalMilliseconds);

            if (processRunId > 0)
            {
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    result.Failed > 0 ? SystemProcessRunStatus.CompletedWithErrors : SystemProcessRunStatus.Succeeded,
                    new UpdateSystemProcessRunRequest
                    {
                        TotalItems = result.Selected,
                        ProcessedItems = result.Selected,
                        SucceededItems = result.Executed + result.Activated,
                        FailedItems = result.Failed,
                        SkippedItems = result.SkippedStaleQuote + result.SkippedDelayedQuote +
                                       result.SkippedOutsideSession + result.SkippedNoQuote,
                        ResultSummary = $"Проверено {result.Selected}; исполнено {result.Executed}; активировано {result.Activated}; отклонено {result.Rejected}; истекло {result.Expired}; ошибок {result.Failed}.",
                    },
                    cancellationToken);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (processRunId > 0)
            {
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Interrupted,
                    new UpdateSystemProcessRunRequest
                    {
                        ErrorSummary = "Цикл автоисполнения ордеров прерван остановкой приложения.",
                    },
                    CancellationToken.None);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (processRunId > 0)
            {
                await _processJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Failed,
                    new UpdateSystemProcessRunRequest { ErrorSummary = ex.Message },
                    CancellationToken.None);
            }
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static bool IsWithinTradingSession(string? exchange, DateTime utcNow)
    {
        if (!TradingSessionCalendar.TryGetSessionSpec(exchange, out var spec))
        {
            return true;
        }

        var timeZone = TradingSessionCalendar.TryResolveTimeZone(spec);
        if (timeZone is null)
        {
            return true;
        }

        return TradingSessionCalendar.IsWithinRegularSession(utcNow, spec, timeZone, out _);
    }
}
