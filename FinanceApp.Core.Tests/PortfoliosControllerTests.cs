using System.Security.Claims;
using FinanceApp.API.Controllers;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceApp.Core.Tests;

public class PortfoliosControllerTests
{
    [Fact]
    public async Task AddItem_MarkedStock_ResetsPurchaseCandidatePriorityToNone()
    {
        await using var context = CreateContext();
        var user = new User
        {
            Id = 1,
            Username = "user",
            Email = "user@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
        };
        var portfolio = new Portfolio
        {
            Id = 10,
            Name = "Main",
            UserId = user.Id,
            User = user,
            CreatedAt = DateTime.UtcNow,
        };
        var stock = new Stock
        {
            Id = 20,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.Tracked,
            PurchaseCandidatePriority = StockPurchaseCandidatePriority.HighPriority,
            UpdatedAt = DateTime.UtcNow,
        };

        context.Users.Add(user);
        context.Portfolios.Add(portfolio);
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context, user.Id);
        var result = await controller.AddItem(portfolio.Id, new AddItemDto
        {
            StockId = stock.Id,
            Quantity = 2m,
            BuyPrice = 95m,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<PortfolioItem>(ok.Value);

        var persistedStock = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == stock.Id);
        Assert.Equal(StockPurchaseCandidatePriority.None, persistedStock.PurchaseCandidatePriority);
        Assert.Equal(1, await context.PortfolioItems.CountAsync(x => x.StockId == stock.Id && x.PortfolioId == portfolio.Id));
    }

    [Fact]
    public async Task AddItem_UnknownStock_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        var user = new User
        {
            Id = 2,
            Username = "user2",
            Email = "user2@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
        };
        var portfolio = new Portfolio
        {
            Id = 11,
            Name = "Main",
            UserId = user.Id,
            User = user,
            CreatedAt = DateTime.UtcNow,
        };

        context.Users.Add(user);
        context.Portfolios.Add(portfolio);
        await context.SaveChangesAsync();

        var controller = CreateController(context, user.Id);
        var result = await controller.AddItem(portfolio.Id, new AddItemDto
        {
            StockId = 999,
            Quantity = 1m,
            BuyPrice = 10m,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Stock not found", badRequest.Value);
        Assert.Empty(context.PortfolioItems);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AppDbContext(options);
    }

    private static PortfoliosController CreateController(AppDbContext context, int userId)
    {
        return new PortfoliosController(context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    ], "TestAuth"))
                }
            }
        };
    }
}
