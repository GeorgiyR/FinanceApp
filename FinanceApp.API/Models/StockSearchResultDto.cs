namespace FinanceApp.API.Models;

public sealed record StockSearchResultDto(
    int Id,
    string Ticker,
    string Name,
    string Exchange,
    decimal CurrentPrice);
