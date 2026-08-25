using System.Text.Json;
using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/system/process-runs")]
[Authorize]
public sealed class SystemProcessRunsController : ControllerBase
{
    private const int MaxPageSize = 100;
    private static readonly HashSet<SystemProcessRunStatus> ActiveStatuses =
    [
        SystemProcessRunStatus.Pending,
        SystemProcessRunStatus.Running,
        SystemProcessRunStatus.Deferred,
    ];

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public SystemProcessRunsController(AppDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    [HttpGet]
    public async Task<ActionResult<SystemProcessRunListResponse>> GetList(
        [FromQuery] SystemProcessRunListQuery query,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateQuery(query, out var parsedStatuses);
        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var serverNowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var dbQuery = _db.SystemProcessRuns.AsNoTracking().AsQueryable();

        if (parsedStatuses.Count > 0)
        {
            dbQuery = dbQuery.Where(x => parsedStatuses.Contains(x.Status));
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessType))
        {
            var processType = query.ProcessType.Trim();
            dbQuery = dbQuery.Where(x => x.ProcessType == processType);
        }

        if (!string.IsNullOrWhiteSpace(query.Trigger)
            && Enum.TryParse<SystemProcessTrigger>(query.Trigger.Trim(), true, out var trigger))
        {
            dbQuery = dbQuery.Where(x => x.Trigger == trigger);
        }

        if (query.FromUtc.HasValue)
        {
            dbQuery = dbQuery.Where(x => x.QueuedAtUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            dbQuery = dbQuery.Where(x => x.QueuedAtUtc <= query.ToUtc.Value);
        }

        if (query.ActiveOnly)
        {
            dbQuery = dbQuery.Where(x => ActiveStatuses.Contains(x.Status) && x.CompletedAtUtc == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            dbQuery = dbQuery.Where(x =>
                x.ProcessType.Contains(search)
                || x.DisplayName.Contains(search)
                || (x.CorrelationId != null && x.CorrelationId.Contains(search))
                || (x.ExternalRunKey != null && x.ExternalRunKey.Contains(search))
                || (x.ResultSummary != null && x.ResultSummary.Contains(search)));
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var items = await dbQuery
            .OrderByDescending(x => x.CompletedAtUtc == null)
            .ThenByDescending(x => x.QueuedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => MapListItem(x, serverNowUtc))
            .ToListAsync(cancellationToken);

        return Ok(new SystemProcessRunListResponse
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            ServerNowUtc = serverNowUtc,
            Items = items,
        });
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<SystemProcessRunDetailsDto>> GetById(long id, CancellationToken cancellationToken = default)
    {
        var serverNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var run = await _db.SystemProcessRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (run is null)
        {
            return NotFound();
        }

        return Ok(MapDetails(run, serverNowUtc));
    }

    [HttpGet("summary")]
    public async Task<ActionResult<SystemProcessRunSummaryDto>> GetSummary(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var last24h = nowUtc.AddHours(-24);

        var activeCount = await _db.SystemProcessRuns
            .AsNoTracking()
            .CountAsync(x => x.CompletedAtUtc == null && ActiveStatuses.Contains(x.Status), cancellationToken);

        var failedLast24Hours = await _db.SystemProcessRuns
            .AsNoTracking()
            .CountAsync(
                x => x.CompletedAtUtc != null
                     && x.CompletedAtUtc >= last24h
                     && (x.Status == SystemProcessRunStatus.Failed
                         || x.Status == SystemProcessRunStatus.CompletedWithErrors
                         || x.Status == SystemProcessRunStatus.Interrupted),
                cancellationToken);

        var completedLast24Hours = await _db.SystemProcessRuns
            .AsNoTracking()
            .CountAsync(
                x => x.CompletedAtUtc != null
                     && x.CompletedAtUtc >= last24h
                     && x.Status == SystemProcessRunStatus.Succeeded,
                cancellationToken);

        var totalLast24Hours = await _db.SystemProcessRuns
            .AsNoTracking()
            .CountAsync(x => x.QueuedAtUtc >= last24h, cancellationToken);

        return Ok(new SystemProcessRunSummaryDto
        {
            GeneratedAtUtc = nowUtc,
            ActiveCount = activeCount,
            FailedLast24Hours = failedLast24Hours,
            CompletedLast24Hours = completedLast24Hours,
            TotalLast24Hours = totalLast24Hours,
        });
    }

    private static Dictionary<string, string[]> ValidateQuery(SystemProcessRunListQuery query, out HashSet<SystemProcessRunStatus> parsedStatuses)
    {
        var errors = new Dictionary<string, string[]>();
        parsedStatuses = [];

        if (query.Page <= 0)
        {
            errors[nameof(query.Page)] = ["page должен быть больше 0."];
        }

        if (query.PageSize <= 0 || query.PageSize > MaxPageSize)
        {
            errors[nameof(query.PageSize)] = [$"pageSize должен быть в диапазоне 1..{MaxPageSize}."];
        }

        if (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc.Value > query.ToUtc.Value)
        {
            errors[nameof(query.FromUtc)] = ["fromUtc должен быть меньше или равен toUtc."];
        }

        if (!string.IsNullOrWhiteSpace(query.Trigger)
            && !Enum.TryParse<SystemProcessTrigger>(query.Trigger.Trim(), true, out _))
        {
            errors[nameof(query.Trigger)] = ["Недопустимое значение trigger."];
        }

        if (!string.IsNullOrWhiteSpace(query.Statuses))
        {
            var invalid = new List<string>();
            foreach (var part in query.Statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<SystemProcessRunStatus>(part, true, out var status))
                {
                    parsedStatuses.Add(status);
                }
                else
                {
                    invalid.Add(part);
                }
            }

            if (invalid.Count > 0)
            {
                errors[nameof(query.Statuses)] = [$"Недопустимые статусы: {string.Join(", ", invalid)}."];
            }
        }

        return errors;
    }

    private static SystemProcessRunListItemDto MapListItem(SystemProcessRun run, DateTime serverNowUtc)
    {
        var duration = CalculateDuration(run, serverNowUtc);
        double? progressPercent = run.TotalItems.HasValue && run.TotalItems > 0
            ? Math.Round((double)run.ProcessedItems / run.TotalItems.Value * 100, 2)
            : null;

        return new SystemProcessRunListItemDto
        {
            Id = run.Id,
            ProcessType = run.ProcessType,
            DisplayName = run.DisplayName,
            Status = run.Status,
            Trigger = run.Trigger,
            QueuedAtUtc = run.QueuedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            CorrelationId = run.CorrelationId,
            TotalItems = run.TotalItems,
            ProcessedItems = run.ProcessedItems,
            SucceededItems = run.SucceededItems,
            FailedItems = run.FailedItems,
            SkippedItems = run.SkippedItems,
            ResultSummary = run.ResultSummary,
            ErrorSummary = run.ErrorSummary,
            DurationSeconds = duration,
            ProgressPercent = progressPercent,
        };
    }

    private static SystemProcessRunDetailsDto MapDetails(SystemProcessRun run, DateTime serverNowUtc)
    {
        var detailsJson = run.DetailsJson;
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            try
            {
                using var _ = JsonDocument.Parse(detailsJson);
            }
            catch
            {
                detailsJson = null;
            }
        }

        var baseDto = MapListItem(run, serverNowUtc);
        return new SystemProcessRunDetailsDto
        {
            Id = baseDto.Id,
            ProcessType = baseDto.ProcessType,
            DisplayName = baseDto.DisplayName,
            Status = baseDto.Status,
            Trigger = baseDto.Trigger,
            QueuedAtUtc = baseDto.QueuedAtUtc,
            StartedAtUtc = baseDto.StartedAtUtc,
            CompletedAtUtc = baseDto.CompletedAtUtc,
            UpdatedAtUtc = baseDto.UpdatedAtUtc,
            CorrelationId = baseDto.CorrelationId,
            TotalItems = baseDto.TotalItems,
            ProcessedItems = baseDto.ProcessedItems,
            SucceededItems = baseDto.SucceededItems,
            FailedItems = baseDto.FailedItems,
            SkippedItems = baseDto.SkippedItems,
            ResultSummary = baseDto.ResultSummary,
            ErrorSummary = baseDto.ErrorSummary,
            DurationSeconds = baseDto.DurationSeconds,
            ProgressPercent = baseDto.ProgressPercent,
            ExternalRunKey = run.ExternalRunKey,
            InitiatedByUserId = run.InitiatedByUserId,
            LastProcessedEntity = run.LastProcessedEntity,
            DetailsJson = detailsJson,
        };
    }

    private static double? CalculateDuration(SystemProcessRun run, DateTime serverNowUtc)
    {
        var from = run.StartedAtUtc ?? run.QueuedAtUtc;
        var to = run.CompletedAtUtc ?? serverNowUtc;
        if (to < from)
        {
            return null;
        }

        return Math.Round((to - from).TotalSeconds, 3);
    }
}
