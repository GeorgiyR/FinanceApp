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

    [Fact]
    public async Task Delete_PortfolioWithPositions_ReturnsConflictAndKeepsPortfolio()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 12, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Stocks.Add(new Stock
        {
            Id = 21,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nasdaq,
            CurrentPrice = 300m,
            UpdatedAt = DateTime.UtcNow,
        });
        context.PortfolioItems.Add(new PortfolioItem
        {
            Id = 31,
            PortfolioId = 12,
            StockId = 21,
            Quantity = 1m,
            BuyPrice = 250m,
            BoughtAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Delete(12);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(context.Portfolios);
    }

    [Fact]
    public async Task Delete_EmptyPortfolio_RemovesPortfolio()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 13, Name = "Empty", UserId = 1, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, 1);
        var result = await controller.Delete(13);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(context.Portfolios);
    }

    [Fact]
    public async Task GetAll_IncludesOnlyActiveOrders()
    {
        await using var context = CreateContext();
        context.Portfolios.Add(new Portfolio { Id = 14, Name = "Main", UserId = 1, CreatedAt = DateTime.UtcNow });
        context.Orders.AddRange(
            new Order { Id = 41, PortfolioId = 14, StockId = 21, Side = OrderSide.Buy, Status = OrderStatus.Pending, Quantity = 1m, Price = 100m, CreatedAt = DateTime.UtcNow },
            new Order { Id = 42, PortfolioId = 14, StockId = 21, Side = OrderSide.Buy, Status = OrderStatus.Triggered, Quantity = 1m, Price = 100m, CreatedAt = DateTime.UtcNow },
            new Order { Id = 43, PortfolioId = 14, StockId = 21, Side = OrderSide.Buy, Status = OrderStatus.Executed, Quantity = 1m, Price = 100m, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var controller = CreateController(context, 1);
        var result = await controller.GetAll();

        var portfolios = Assert.IsAssignableFrom<IEnumerable<Portfolio>>(result.Value).ToList();
        var portfolio = Assert.Single(portfolios);
        Assert.Equal(2, portfolio.Orders.Count);
        Assert.All(portfolio.Orders, order => Assert.True(order.Status is OrderStatus.Pending or OrderStatus.Triggered));
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
