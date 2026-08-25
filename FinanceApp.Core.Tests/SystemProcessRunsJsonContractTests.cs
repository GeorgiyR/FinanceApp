using System.Text.Json;
using FinanceApp.API.Controllers;
using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceApp.Core.Tests;

public class SystemProcessRunsJsonContractTests
{
    [Fact]
    public void ListResponse_StatusAndTrigger_SerializeAsStrings()
    {
        var payload = new SystemProcessRunListResponse
        {
            Page = 1,
            PageSize = 25,
            TotalCount = 1,
            ServerNowUtc = new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc),
            Items =
            [
                new SystemProcessRunListItemDto
                {
                    Id = 1,
                    ProcessType = "CatalogStockRefresh",
                    DisplayName = "Catalog refresh",
                    Status = SystemProcessRunStatus.Running,
                    Trigger = SystemProcessTrigger.Automatic,
                    QueuedAtUtc = new DateTime(2026, 8, 25, 8, 59, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc),
                }
            ]
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        var firstItem = document.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.String, firstItem.GetProperty("status").ValueKind);
        Assert.Equal("Running", firstItem.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, firstItem.GetProperty("trigger").ValueKind);
        Assert.Equal("Automatic", firstItem.GetProperty("trigger").GetString());
        Assert.DoesNotContain("\"status\":1", json);
        Assert.DoesNotContain("\"trigger\":2", json);
    }

    [Fact]
    public void DetailsResponse_InheritedStatusAndTrigger_SerializeAsStrings()
    {
        var payload = new SystemProcessRunDetailsDto
        {
            Id = 1,
            ProcessType = "CatalogStockRefresh",
            DisplayName = "Catalog refresh",
            Status = SystemProcessRunStatus.CompletedWithErrors,
            Trigger = SystemProcessTrigger.StartupCatchUp,
            QueuedAtUtc = new DateTime(2026, 8, 25, 8, 59, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        Assert.Equal("CompletedWithErrors", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("StartupCatchUp", document.RootElement.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task ControllerListAndDetails_KeepStringEnumContract()
    {
        await using var context = CreateContext();
        context.SystemProcessRuns.Add(new SystemProcessRun
        {
            Id = 100,
            ProcessType = "CatalogStockRefresh",
            DisplayName = "Catalog refresh",
            Status = SystemProcessRunStatus.Running,
            Trigger = SystemProcessTrigger.Automatic,
            InstanceId = "instance-1",
            QueuedAtUtc = new DateTime(2026, 8, 25, 8, 59, 0, DateTimeKind.Utc),
            StartedAtUtc = new DateTime(2026, 8, 25, 8, 59, 10, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var controller = new SystemProcessRunsController(context, TimeProvider.System);

        var listResult = await controller.GetList(new SystemProcessRunListQuery(), CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var listPayload = Assert.IsType<SystemProcessRunListResponse>(listOk.Value);
        var listJson = JsonSerializer.Serialize(listPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"Running\"", listJson);
        Assert.Contains("\"trigger\":\"Automatic\"", listJson);

        var detailsResult = await controller.GetById(100, CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResult.Result);
        var detailsPayload = Assert.IsType<SystemProcessRunDetailsDto>(detailsOk.Value);
        var detailsJson = JsonSerializer.Serialize(detailsPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"Running\"", detailsJson);
        Assert.Contains("\"trigger\":\"Automatic\"", detailsJson);
    }

    [Fact]
    public void UnrelatedEnumSerialization_RemainsNumericWithoutLocalConverter()
    {
        var json = JsonSerializer.Serialize(
            new { status = SystemProcessRunStatus.Running },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(1, document.RootElement.GetProperty("status").GetInt32());
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"system-process-json-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }
}
