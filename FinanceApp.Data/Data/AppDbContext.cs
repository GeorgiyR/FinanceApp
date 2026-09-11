using Microsoft.EntityFrameworkCore;
using FinanceApp.Core.Models;

namespace FinanceApp.Data.Data;

public class AppDbContext : DbContext
{
    private static readonly DateTime MarketIndicesSeedTimestampUtc = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

    private static readonly MarketIndex[] MarketIndexSeedData =
    [
            new MarketIndex
            {
                Id = 1,
                Name = @"Dow Jones Industrial Average",
                NormalizedName = @"DOW JONES INDUSTRIAL AVERAGE",
                Code = @"DJIA",
                NormalizedCode = @"DJIA",
                ProviderSymbol = "^DJI",
                Description = @"Индекс Доу-Джонса для промышленных компаний отражает динамику 30 крупнейших публичных компаний США. Взвешен по цене, а не по рыночной капитализации, что является методологическим ограничением: более дорогие акции оказывают непропорционально большое влияние. Используется как исторический барометр состояния американской экономики, однако из-за малого числа компонентов и ценового взвешивания считается менее репрезентативным, чем S&P 500.",
                CountryOrRegion = @"USA",
                SortOrder = 10,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 2,
                Name = @"S&P 500",
                NormalizedName = @"S&P 500",
                Code = @"SPX",
                NormalizedCode = @"SPX",
                ProviderSymbol = "^GSPC",
                Description = @"Взвешенный по рыночной капитализации индекс 500 крупнейших публичных компаний США. Широко признан эталоном для американского рынка акций и базой для большинства пассивных инвестиционных стратегий. Охватывает около 80% совокупной капитализации рынка США. Основное ограничение — концентрация в технологическом секторе и крупнейших компаниях. Состав и методологию определяет комитет S&P.",
                CountryOrRegion = @"USA",
                SortOrder = 20,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 3,
                Name = @"NASDAQ Composite",
                NormalizedName = @"NASDAQ COMPOSITE",
                Code = @"COMP",
                NormalizedCode = @"COMP",
                ProviderSymbol = "^IXIC",
                Description = @"Взвешенный по капитализации индекс всех акций, торгующихся на бирже NASDAQ, — преимущественно технологических и растущих компаний. Включает более 3 000 ценных бумаг. Высокая концентрация в IT-секторе делает его чувствительным к изменениям процентных ставок и настроениям в отношении акций роста.",
                CountryOrRegion = @"USA",
                SortOrder = 30,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 4,
                Name = @"NASDAQ-100",
                NormalizedName = @"NASDAQ-100",
                Code = @"NDX",
                NormalizedCode = @"NDX",
                ProviderSymbol = "^NDX",
                Description = @"Взвешенный по капитализации индекс 100 крупнейших нефинансовых компаний, котирующихся на NASDAQ. Концентрация в технологии, потребительском секторе и здравоохранении делает его популярным инструментом для ставки на инновационные компании. Отсутствие финансового сектора — ключевое отличие от широкого рынка.",
                CountryOrRegion = @"USA",
                SortOrder = 40,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 5,
                Name = @"Russell 2000",
                NormalizedName = @"RUSSELL 2000",
                Code = @"RUT",
                NormalizedCode = @"RUT",
                ProviderSymbol = "^RUT",
                Description = @"Индекс 2 000 компаний малой капитализации из Russell 3000. Традиционно используется как барометр внутреннего экономического здоровья США: малые компании менее зависимы от глобальных цепочек поставок. Отличается более высокой волатильностью и менее ликвидным составом по сравнению с крупнокапитализированными индексами.",
                CountryOrRegion = @"USA",
                SortOrder = 50,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 6,
                Name = @"FTSE 100",
                NormalizedName = @"FTSE 100",
                Code = @"UKX",
                NormalizedCode = @"UKX",
                ProviderSymbol = "^FTSE",
                Description = @"Взвешенный по капитализации индекс 100 крупнейших компаний Лондонской фондовой биржи. Значительную долю занимают горнодобывающие, энергетические и финансовые компании с глобальными операциями. Существенная часть выручки компонентов поступает из-за рубежа, поэтому индекс реагирует на курс фунта стерлингов.",
                CountryOrRegion = @"UK",
                SortOrder = 60,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 7,
                Name = @"DAX",
                NormalizedName = @"DAX",
                Code = @"DAX",
                NormalizedCode = @"DAX",
                ProviderSymbol = "^GDAXI",
                Description = @"Взвешенный по капитализации индекс 40 крупнейших немецких компаний на бирже Xetra. Является главным барометром германского и, косвенно, европейского промышленного сектора. Особенность: DAX — индекс совокупного дохода (total return), включающий реинвестированные дивиденды, что делает его напрямую несопоставимым с price-return индексами.",
                CountryOrRegion = @"Germany",
                SortOrder = 70,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 8,
                Name = @"CAC 40",
                NormalizedName = @"CAC 40",
                Code = @"PX1",
                NormalizedCode = @"PX1",
                ProviderSymbol = "^FCHI",
                Description = @"Взвешенный по капитализации (free-float) индекс 40 крупнейших компаний Парижской фондовой биржи Euronext. Репрезентирует французский рынок и отражает широкий спектр отраслей: предметы роскоши, энергетика, финансы, промышленность. Многие компоненты являются глобальными корпорациями, поэтому чувствительны к валютным курсам.",
                CountryOrRegion = @"France",
                SortOrder = 80,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 9,
                Name = @"EURO STOXX 50",
                NormalizedName = @"EURO STOXX 50",
                Code = @"SX5E",
                NormalizedCode = @"SX5E",
                ProviderSymbol = "^STOXX50E",
                Description = @"Взвешенный по free-float капитализации индекс 50 крупнейших компаний еврозоны из 8 стран. Широко используется как базовый актив для деривативов и ETF, ориентированных на европейский рынок. Концентрация в нескольких странах (Франция, Германия) и секторах (финансы, промышленность) ограничивает диверсификацию.",
                CountryOrRegion = @"Eurozone",
                SortOrder = 90,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 10,
                Name = @"STOXX Europe 600",
                NormalizedName = @"STOXX EUROPE 600",
                Code = @"SXXP",
                NormalizedCode = @"SXXP",
                ProviderSymbol = "^STOXX",
                Description = @"Взвешенный по free-float капитализации индекс 600 компаний из 17 европейских стран, включая страны вне еврозоны (Великобритания, Швейцария, Скандинавия). Обеспечивает более широкое географическое и секторальное покрытие Европы, чем EURO STOXX 50. Применяется как эталон для паневропейских стратегий.",
                CountryOrRegion = @"Europe",
                SortOrder = 100,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 11,
                Name = @"SMI",
                NormalizedName = @"SMI",
                Code = @"SMI",
                NormalizedCode = @"SMI",
                ProviderSymbol = "^SSMI",
                Description = @"Взвешенный по free-float капитализации индекс 20 крупнейших компаний Швейцарской биржи. Сильно сконцентрирован в фармацевтике, финансах и товарах повседневного спроса. Выражен в швейцарских франках, которые традиционно считаются защитным активом. Высокая отраслевая концентрация является ключевым ограничением индекса.",
                CountryOrRegion = @"Switzerland",
                SortOrder = 110,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 12,
                Name = @"IBEX 35",
                NormalizedName = @"IBEX 35",
                Code = @"IBEX",
                NormalizedCode = @"IBEX",
                ProviderSymbol = "^IBEX",
                Description = @"Взвешенный по free-float капитализации индекс 35 наиболее ликвидных акций Испанской фондовой биржи. Значительную долю занимают банки, телекоммуникационные и энергетические компании. Индекс чувствителен к динамике кредитного рынка еврозоны и политической обстановке.",
                CountryOrRegion = @"Spain",
                SortOrder = 120,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 13,
                Name = @"FTSE MIB",
                NormalizedName = @"FTSE MIB",
                Code = @"FTSEMIB",
                NormalizedCode = @"FTSEMIB",
                ProviderSymbol = "FTSEMIB.MI",
                Description = @"Взвешенный по free-float капитализации индекс 40 крупнейших и наиболее ликвидных итальянских компаний. Существенный вес имеют финансовый и коммунальный секторы. Высокая концентрация в банках делает индекс особенно чувствительным к ситуации с государственным долгом Италии.",
                CountryOrRegion = @"Italy",
                SortOrder = 130,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 14,
                Name = @"Nikkei 225",
                NormalizedName = @"NIKKEI 225",
                Code = @"NKY",
                NormalizedCode = @"NKY",
                ProviderSymbol = "^N225",
                Description = @"Ценово-взвешенный индекс 225 избранных акций Токийской фондовой биржи — аналог Доу-Джонса для японского рынка. Является наиболее известным барометром японского фондового рынка. Ценовое взвешивание создаёт те же искажения, что и в DJIA: высокостоимостные акции оказывают непропорциональное влияние.",
                CountryOrRegion = @"Japan",
                SortOrder = 140,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 15,
                Name = @"TOPIX",
                NormalizedName = @"TOPIX",
                Code = @"TPX",
                NormalizedCode = @"TPX",
                ProviderSymbol = "^TOPX",
                Description = @"Взвешенный по free-float капитализации индекс всех акций первой секции Токийской фондовой биржи — более 2 000 компаний. Считается более репрезентативным отражением японского рынка, чем Nikkei 225. Используется японскими институциональными инвесторами как основной эталон.",
                CountryOrRegion = @"Japan",
                SortOrder = 150,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 16,
                Name = @"Hang Seng Index",
                NormalizedName = @"HANG SENG INDEX",
                Code = @"HSI",
                NormalizedCode = @"HSI",
                ProviderSymbol = "^HSI",
                Description = @"Взвешенный по free-float капитализации индекс крупнейших компаний Гонконгской фондовой биржи. Включает компании из материкового Китая, котирующиеся в Гонконге (акции H). Чувствителен к регуляторной политике материкового Китая и геополитическим рискам. Методология и состав регулярно пересматриваются.",
                CountryOrRegion = @"Hong Kong",
                SortOrder = 160,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 17,
                Name = @"CSI 300",
                NormalizedName = @"CSI 300",
                Code = @"CSI300",
                NormalizedCode = @"CSI300",
                ProviderSymbol = "000300.SS",
                Description = @"Взвешенный по free-float капитализации индекс 300 крупнейших акций, торгующихся на Шанхайской и Шэньчжэньской биржах (акции А). Отражает динамику китайского внутреннего рынка, доступного через механизмы Stock Connect. Подвержен влиянию регуляторных изменений и ограничений на движение капитала.",
                CountryOrRegion = @"China",
                SortOrder = 170,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 18,
                Name = @"Shanghai Composite",
                NormalizedName = @"SHANGHAI COMPOSITE",
                Code = @"SHCOMP",
                NormalizedCode = @"SHCOMP",
                ProviderSymbol = "000001.SS",
                Description = @"Взвешенный по капитализации индекс всех акций А и Б, котирующихся на Шанхайской фондовой бирже. Используется как широкий барометр китайского рынка, однако включает большое число малоликвидных компаний. Доступ иностранных инвесторов ограничен квотами и регуляторными требованиями.",
                CountryOrRegion = @"China",
                SortOrder = 180,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 19,
                Name = @"S&P/ASX 200",
                NormalizedName = @"S&P/ASX 200",
                Code = @"AS51",
                NormalizedCode = @"AS51",
                ProviderSymbol = "^AXJO",
                Description = @"Взвешенный по free-float капитализации индекс 200 крупнейших компаний Австралийской фондовой биржи. Сильно сконцентрирован в финансовом и горнодобывающем секторах. Динамика тесно связана с ценами на сырьё и торговыми отношениями с Китаем. Стандартный эталон для австралийских портфельных стратегий.",
                CountryOrRegion = @"Australia",
                SortOrder = 190,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 20,
                Name = @"S&P/TSX Composite",
                NormalizedName = @"S&P/TSX COMPOSITE",
                Code = @"SPTSX",
                NormalizedCode = @"SPTSX",
                ProviderSymbol = "^GSPTSE",
                Description = @"Взвешенный по рыночной капитализации индекс всех компаний, удовлетворяющих критериям включения Торонтской фондовой биржи. Существенную долю занимают финансовый сектор, горнодобыча и нефтегазовая промышленность. Динамика коррелирует с ценами на сырьё. Основной эталон канадского рынка акций.",
                CountryOrRegion = @"Canada",
                SortOrder = 200,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 21,
                Name = @"BSE SENSEX",
                NormalizedName = @"BSE SENSEX",
                Code = @"SENSEX",
                NormalizedCode = @"SENSEX",
                ProviderSymbol = "^BSESN",
                Description = @"Ценово-взвешенный индекс 30 хорошо зарекомендовавших себя компаний Бомбейской фондовой биржи. Один из старейших индикаторов индийского рынка. Малое число компонентов ограничивает репрезентативность. Чувствителен к изменениям в регуляторной среде, процентным ставкам Резервного банка Индии и состоянию банковского сектора.",
                CountryOrRegion = @"India",
                SortOrder = 210,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 22,
                Name = @"NIFTY 50",
                NormalizedName = @"NIFTY 50",
                Code = @"NIFTY50",
                NormalizedCode = @"NIFTY50",
                ProviderSymbol = "^NSEI",
                Description = @"Взвешенный по free-float капитализации индекс 50 крупнейших компаний Национальной фондовой биржи Индии. Охватывает 13 секторов и считается более репрезентативным эталоном индийского рынка, чем SENSEX. Широко используется как база для деривативов и пассивных инструментов.",
                CountryOrRegion = @"India",
                SortOrder = 220,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 23,
                Name = @"KOSPI",
                NormalizedName = @"KOSPI",
                Code = @"KOSPI",
                NormalizedCode = @"KOSPI",
                ProviderSymbol = "^KS11",
                Description = @"Взвешенный по рыночной капитализации индекс всех акций обыкновенных акций Корейской фондовой биржи. Существенную долю занимают технологические и автомобильные конгломераты. Чувствителен к геополитическим рискам на Корейском полуострове и глобальному спросу на полупроводники.",
                CountryOrRegion = @"South Korea",
                SortOrder = 230,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 24,
                Name = @"Bovespa (IBOVESPA)",
                NormalizedName = @"BOVESPA (IBOVESPA)",
                Code = @"IBOV",
                NormalizedCode = @"IBOV",
                ProviderSymbol = "^BVSP",
                Description = @"Взвешенный по ликвидности торгов индекс наиболее торгуемых акций биржи B3. Высокую долю занимают финансовые, сырьевые и энергетические компании. Подвержен влиянию бразильского реала, политических рисков и мировых цен на commodities. Является основным эталоном бразильского рынка акций.",
                CountryOrRegion = @"Brazil",
                SortOrder = 240,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 25,
                Name = @"MSCI World",
                NormalizedName = @"MSCI WORLD",
                Code = @"MSCIW",
                NormalizedCode = @"MSCIW",
                ProviderSymbol = null,
                Description = @"Взвешенный по free-float капитализации индекс акций крупной и средней капитализации из более 20 развитых стран. Охватывает около 85% рынка с поправкой на free-float в каждой стране. Широко используется как глобальный эталон для развитых рынков. Высокая доля США (более 60%) является ключевым ограничением географической диверсификации.",
                CountryOrRegion = @"Developed Markets",
                SortOrder = 250,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 26,
                Name = @"MSCI Emerging Markets",
                NormalizedName = @"MSCI EMERGING MARKETS",
                Code = @"MSCIEM",
                NormalizedCode = @"MSCIEM",
                ProviderSymbol = null,
                Description = @"Взвешенный по free-float капитализации индекс акций крупной и средней капитализации из более 20 развивающихся стран. Охватывает около 85% рынка с поправкой на free-float в каждой стране. Отражает возможности роста развивающихся экономик, но несёт повышенные риски: валютные, политические и ликвидности.",
                CountryOrRegion = @"Emerging Markets",
                SortOrder = 260,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            },
            new MarketIndex
            {
                Id = 27,
                Name = @"MSCI ACWI",
                NormalizedName = @"MSCI ACWI",
                Code = @"MSCIACWI",
                NormalizedCode = @"MSCIACWI",
                ProviderSymbol = null,
                Description = @"Взвешенный по free-float капитализации индекс акций крупной и средней капитализации из 47 стран — как развитых, так и развивающихся рынков. Объединяет MSCI World и MSCI Emerging Markets. Используется как единый глобальный эталон. Высокая доля США сохраняется и в ACWI.",
                CountryOrRegion = @"Global",
                SortOrder = 270,
                IsArchived = false,
                ShowInNavigation = true,
                CreatedAt = MarketIndicesSeedTimestampUtc,
                UpdatedAt = MarketIndicesSeedTimestampUtc
            }
    ];

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Stock> Stocks { get; set; } = null!;
    public DbSet<Portfolio> Portfolios { get; set; } = null!;
    public DbSet<PortfolioItem> PortfolioItems { get; set; } = null!;
    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<Transaction> Transactions { get; set; } = null!;
    public DbSet<Dividend> Dividends { get; set; } = null!;
    public DbSet<StockHistoricalPrice> StockHistoricalPrices { get; set; } = null!;
    public DbSet<CompanyFundamentalsSnapshot> FundamentalsSnapshots { get; set; } = null!;
    public DbSet<FinancialPeriod> FinancialPeriods { get; set; } = null!;
    public DbSet<EarningsEvent> EarningsEvents { get; set; } = null!;
    public DbSet<Sector> Sectors { get; set; } = null!;
    public DbSet<Industry> Industries { get; set; } = null!;
    public DbSet<MarketIndex> MarketIndices { get; set; } = null!;
    public DbSet<StockMarketIndex> StockMarketIndices { get; set; } = null!;
    public DbSet<MarketIndexHistoricalPrice> MarketIndexHistoricalPrices { get; set; } = null!;
    public DbSet<CatalogStockRefreshRun> CatalogStockRefreshRuns { get; set; } = null!;
    public DbSet<CatalogFundamentalsRefreshRun> CatalogFundamentalsRefreshRuns { get; set; } = null!;
    public DbSet<CatalogMaintenanceLease> CatalogMaintenanceLeases { get; set; } = null!;
    public DbSet<StockMetadataEnrichmentJob> StockMetadataEnrichmentJobs { get; set; } = null!;
    public DbSet<StockMetadataEnrichmentResult> StockMetadataEnrichmentResults { get; set; } = null!;
    public DbSet<StockMetadataIndustryMapping> StockMetadataIndustryMappings { get; set; } = null!;
    public DbSet<SystemProcessRun> SystemProcessRuns { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasIndex(x => x.OrderId).IsUnique().HasFilter("`OrderId` IS NOT NULL");
            entity.HasOne(x => x.Stock)
                .WithMany()
                .HasForeignKey(x => x.StockId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(x => x.InstrumentCode)
                .HasMaxLength(32);
            entity.Property(x => x.InstrumentCodeType)
                .HasConversion<string>()
                .HasMaxLength(8);
            entity.Property(x => x.Quantity)
                .HasPrecision(18, 8);
            entity.Property(x => x.UnitPrice)
                .HasPrecision(18, 8);
        });

        modelBuilder.Entity<StockHistoricalPrice>(entity =>
        {
            entity.HasIndex(x => new { x.StockId, x.Timestamp, x.Interval }).IsUnique();
            entity.HasIndex(x => new { x.StockId, x.Timestamp });
            entity.Property(x => x.Interval).HasMaxLength(10);
            entity.Property(x => x.QuoteCurrency).HasMaxLength(8);
            entity.Property(x => x.FinancialCurrency).HasMaxLength(8);
            entity.Property(x => x.NormalizedQuoteCurrency).HasMaxLength(8);
            entity.Property(x => x.QuoteUnitMultiplier);
            entity.Property(x => x.Volume);
            entity.Property(x => x.IsQuoteDerived);
        });

        modelBuilder.Entity<Stock>(entity =>
        {
            entity.Property(x => x.CommonName).HasDefaultValue(string.Empty);
            entity.Property(x => x.Exchange).HasMaxLength(32).HasDefaultValue(StockExchanges.Nyse);
            entity.Property(x => x.Wkn).HasMaxLength(6);
            entity.Property(x => x.Isin).HasMaxLength(12);
            entity.Property(x => x.CurrentPriceDelayWarning).HasMaxLength(300);
            entity.HasIndex(x => x.Wkn).HasFilter("`Wkn` IS NOT NULL");
            entity.HasIndex(x => x.Isin).HasFilter("`Isin` IS NOT NULL");
            entity.Property(x => x.FinanzenNetSlug).HasMaxLength(120);
            entity.HasOne(x => x.Industry)
                .WithMany(x => x.Stocks)
                .HasForeignKey(x => x.IndustryId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
            entity.HasOne(x => x.Sector)
                .WithMany()
                .HasForeignKey(x => x.SectorId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
            entity.HasIndex(x => x.SectorId);
            entity.HasIndex(x => x.IndustryId);
            // TrackingStatus: stored as int, always written by the application (ValueGeneratedNever)
            // so EF never omits the column from INSERT even when the value is CatalogOnly = 0
            // (the CLR default for int). HasDefaultValue would mark the property as
            // ValueGeneratedOnAdd, causing EF to skip it in INSERT when value == 0 and letting
            // MySQL substitute its column DEFAULT (1 = Tracked) — the production bug fixed here.
            entity.Property(x => x.TrackingStatus)
                .HasConversion<int>()
                .ValueGeneratedNever();
            entity.HasIndex(x => x.TrackingStatus)
                .HasDatabaseName("IX_Stocks_TrackingStatus");
            entity.Property(x => x.PurchaseCandidatePriority)
                .HasConversion<int>()
                .ValueGeneratedNever();
            entity.HasIndex(x => x.PurchaseCandidatePriority)
                .HasDatabaseName("IX_Stocks_PurchaseCandidatePriority");
            entity.Property(x => x.HistoryRefreshCadence)
                .HasConversion<int>()
                .ValueGeneratedNever();
            entity.HasIndex(x => new { x.HistoryRefreshCadence, x.NextIncrementalHistoryRefreshAtUtc, x.Id })
                .HasDatabaseName("IX_Stocks_HistoryRefreshCadence_NextIncremental_Id");
            entity.HasIndex(x => new { x.HistoryRefreshCadence, x.NextHistoryReconciliationAtUtc, x.Id })
                .HasDatabaseName("IX_Stocks_HistoryRefreshCadence_NextReconciliation_Id");
            entity.HasIndex(x => new { x.HistoryRefreshCadence, x.NextFullHistoryBackfillAtUtc, x.Id })
                .HasDatabaseName("IX_Stocks_HistoryRefreshCadence_NextFullBackfill_Id");
            // ProviderSymbol index for deduplication lookups
            entity.Property(x => x.ProviderSymbol).HasMaxLength(50);
            entity.HasIndex(x => x.ProviderSymbol)
                .HasDatabaseName("IX_Stocks_ProviderSymbol")
                .HasFilter("`ProviderSymbol` IS NOT NULL");
        });

        modelBuilder.Entity<Sector>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.NormalizedName).HasMaxLength(200);
            entity.HasIndex(x => x.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<Industry>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.NormalizedName).HasMaxLength(200);
            entity.HasIndex(x => new { x.SectorId, x.NormalizedName }).IsUnique();
            entity.HasOne(x => x.Sector)
                .WithMany(x => x.Industries)
                .HasForeignKey(x => x.SectorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketIndex>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.NormalizedName).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(50);
            entity.Property(x => x.NormalizedCode).HasMaxLength(50);
            entity.Property(x => x.ProviderSymbol).HasMaxLength(50);
            entity.HasIndex(x => x.NormalizedCode).IsUnique();
            entity.HasData(MarketIndexSeedData);
        });

        modelBuilder.Entity<MarketIndexHistoricalPrice>(entity =>
        {
            entity.HasIndex(x => new { x.MarketIndexId, x.Timestamp, x.Interval }).IsUnique();
            entity.HasIndex(x => new { x.MarketIndexId, x.Interval, x.Timestamp });
            entity.Property(x => x.Interval).HasMaxLength(10);
            entity.Property(x => x.Provider).HasMaxLength(64);
            entity.Property(x => x.ProviderSymbol).HasMaxLength(50);
            entity.HasOne(x => x.MarketIndex)
                .WithMany()
                .HasForeignKey(x => x.MarketIndexId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockMarketIndex>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ImportedAt)
                .HasDefaultValueSql("UTC_TIMESTAMP(6)");
            entity.HasOne(x => x.Stock)
                .WithMany(x => x.MarketIndices)
                .HasForeignKey(x => x.StockId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.MarketIndex)
                .WithMany(x => x.StockMarketIndices)
                .HasForeignKey(x => x.MarketIndexId)
                .OnDelete(DeleteBehavior.Restrict);
            // A stock can be current in an index only once (EffectiveTo IS NULL marks current membership).
            // Historical rows are distinguished by EffectiveFrom / EffectiveTo.
            entity.HasIndex(x => new { x.StockId, x.MarketIndexId })
                .HasDatabaseName("IX_StockMarketIndices_StockId_MarketIndexId");
        });

        modelBuilder.Entity<CatalogStockRefreshRun>(entity =>
        {
            entity.Property(x => x.RunKey).HasMaxLength(64);
            entity.Property(x => x.TimeZoneId).HasMaxLength(64);
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.Property(x => x.FailureSummary).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            entity.HasIndex(x => x.RunKey).IsUnique();
            entity.HasIndex(x => new { x.BusinessDate, x.TimeZoneId });
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });
        });

        modelBuilder.Entity<CatalogFundamentalsRefreshRun>(entity =>
        {
            entity.Property(x => x.RunKey).HasMaxLength(64);
            entity.Property(x => x.TimeZoneId).HasMaxLength(64);
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.Property(x => x.FailureSummary).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            entity.HasIndex(x => x.RunKey).IsUnique();
            entity.HasIndex(x => new { x.BusinessWeek, x.TimeZoneId }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });
        });

        modelBuilder.Entity<CatalogMaintenanceLease>(entity =>
        {
            entity.Property(x => x.LeaseName).HasMaxLength(64);
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);

            entity.HasIndex(x => x.LeaseName).IsUnique();
            entity.HasIndex(x => x.LeaseExpiresAtUtc);
        });

        modelBuilder.Entity<StockMetadataEnrichmentJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasConversion<int>();
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.SelectedStockIdsJson).HasMaxLength(2000);
            entity.Property(x => x.InitiatedByUserId).HasMaxLength(128);
            entity.Property(x => x.DiagnosticSummary).HasMaxLength(2000);
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            entity.HasIndex(x => x.RetryAfterUtc);
        });

        modelBuilder.Entity<StockMetadataEnrichmentResult>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProviderSymbol).HasMaxLength(50);
            entity.Property(x => x.Exchange).HasMaxLength(32);
            entity.Property(x => x.OldIsin).HasMaxLength(12);
            entity.Property(x => x.CandidateIsin).HasMaxLength(12);
            entity.Property(x => x.OldWkn).HasMaxLength(6);
            entity.Property(x => x.CandidateWkn).HasMaxLength(6);
            entity.Property(x => x.RawProviderSector).HasMaxLength(200);
            entity.Property(x => x.RawProviderIndustry).HasMaxLength(200);
            entity.Property(x => x.IsinSource).HasMaxLength(100);
            entity.Property(x => x.WknSource).HasMaxLength(100);
            entity.Property(x => x.IndustrySource).HasMaxLength(100);
            entity.Property(x => x.Diagnostics).HasMaxLength(1000);
            entity.Property(x => x.IsinConfidence).HasConversion<int>();
            entity.Property(x => x.WknConfidence).HasConversion<int>();
            entity.Property(x => x.IndustryConfidence).HasConversion<int>();
            entity.Property(x => x.IsinDecision).HasConversion<int>();
            entity.Property(x => x.WknDecision).HasConversion<int>();
            entity.Property(x => x.IndustryDecision).HasConversion<int>();
            entity.HasOne(x => x.Job)
                .WithMany(x => x.Results)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.JobId, x.StockId }).IsUnique();
            entity.HasIndex(x => new { x.JobId, x.Id });
        });

        modelBuilder.Entity<StockMetadataIndustryMapping>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Provider).HasMaxLength(100);
            entity.Property(x => x.NormalizedSector).HasMaxLength(200);
            entity.Property(x => x.NormalizedIndustry).HasMaxLength(200);
            entity.HasIndex(x => new { x.Provider, x.NormalizedSector, x.NormalizedIndustry }).IsUnique();
            entity.HasOne(x => x.Industry)
                .WithMany()
                .HasForeignKey(x => x.IndustryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SystemProcessRun>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProcessType).HasMaxLength(100);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.InitiatedByUserId).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(128);
            entity.Property(x => x.ExternalRunKey).HasMaxLength(128);
            entity.Property(x => x.InstanceId).HasMaxLength(128);
            entity.Property(x => x.ResultSummary).HasMaxLength(1000);
            entity.Property(x => x.ErrorSummary).HasMaxLength(1000);
            entity.Property(x => x.LastProcessedEntity).HasMaxLength(256);
            entity.Property(x => x.DetailsJson).HasMaxLength(4000);

            entity.HasIndex(x => new { x.StartedAtUtc, x.Id })
                .HasDatabaseName("IX_SystemProcessRuns_StartedAtUtc_Id");
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.ProcessType, x.QueuedAtUtc, x.Id })
                .HasDatabaseName("IX_SystemProcessRuns_ProcessType_QueuedAtUtc_Id");
            entity.HasIndex(x => x.CorrelationId);
            entity.HasIndex(x => x.ExternalRunKey).IsUnique();
            entity.HasIndex(x => new { x.CompletedAtUtc, x.UpdatedAtUtc, x.Id })
                .HasDatabaseName("IX_SystemProcessRuns_ActiveLookup");
        });

        modelBuilder.Entity<CompanyFundamentalsSnapshot>(entity =>
        {
            entity.HasIndex(x => x.StockId).IsUnique();
            entity.Property(x => x.SourceSymbol).HasMaxLength(32);
            entity.Property(x => x.Currency).HasMaxLength(8);
            entity.Property(x => x.Source).HasMaxLength(64).HasDefaultValue("Yahoo Finance");
            entity.HasOne(x => x.Stock)
                .WithMany()
                .HasForeignKey(x => x.StockId)
                .OnDelete(DeleteBehavior.Cascade);

            foreach (var prop in new[]
            {
                nameof(CompanyFundamentalsSnapshot.MarketCap),
                nameof(CompanyFundamentalsSnapshot.EnterpriseValue),
                nameof(CompanyFundamentalsSnapshot.TotalDebt),
                nameof(CompanyFundamentalsSnapshot.CashAndEquivalents),
                nameof(CompanyFundamentalsSnapshot.RevenueTtm),
                nameof(CompanyFundamentalsSnapshot.NetIncomeTtm),
                nameof(CompanyFundamentalsSnapshot.EbitdaTtm),
                nameof(CompanyFundamentalsSnapshot.OperatingIncomeTtm),
                nameof(CompanyFundamentalsSnapshot.FreeCashFlowTtm),
                nameof(CompanyFundamentalsSnapshot.TotalAssets),
                nameof(CompanyFundamentalsSnapshot.TotalLiabilities),
            })
            {
                entity.Property(prop).HasColumnType("decimal(28,2)");
            }

            entity.Property(x => x.PeRatio).HasColumnType("decimal(18,4)");
            entity.Property(x => x.ForwardPeRatio).HasColumnType("decimal(18,4)");
            entity.Property(x => x.PbRatio).HasColumnType("decimal(18,4)");
            entity.Property(x => x.DividendYield).HasColumnType("decimal(18,6)");
        });

        modelBuilder.Entity<FinancialPeriod>(entity =>
        {
            entity.HasIndex(x => new { x.SnapshotId, x.PeriodType, x.PeriodEndDate }).IsUnique();
            entity.Property(x => x.PeriodType).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ReportedCurrency).HasMaxLength(8);
            entity.Property(x => x.Source).HasMaxLength(64).HasDefaultValue("Yahoo Finance");
            entity.HasOne(x => x.Snapshot)
                .WithMany(x => x.Periods)
                .HasForeignKey(x => x.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);

            foreach (var prop in new[]
            {
                nameof(FinancialPeriod.Revenue),
                nameof(FinancialPeriod.OperatingIncome),
                nameof(FinancialPeriod.NetIncome),
                nameof(FinancialPeriod.Ebitda),
                nameof(FinancialPeriod.TotalDebt),
                nameof(FinancialPeriod.TotalAssets),
                nameof(FinancialPeriod.TotalLiabilities),
                nameof(FinancialPeriod.FreeCashFlow),
            })
            {
                entity.Property(prop).HasColumnType("decimal(28,2)");
            }

            entity.Property(x => x.EpsReported).HasColumnType("decimal(18,4)");
            entity.Property(x => x.EpsEstimate).HasColumnType("decimal(18,4)");
        });

        modelBuilder.Entity<EarningsEvent>(entity =>
        {
            entity.HasIndex(x => new { x.SnapshotId, x.ReportDate, x.FiscalPeriod }).IsUnique();
            entity.Property(x => x.DateStatus).HasConversion<string>().HasMaxLength(16).HasDefaultValue(EarningsDateStatus.Unknown);
            entity.Property(x => x.FiscalPeriod).HasMaxLength(32);
            entity.Property(x => x.Source).HasMaxLength(64).HasDefaultValue("Yahoo Finance");
            entity.HasOne(x => x.Snapshot)
                .WithMany(x => x.EarningsEvents)
                .HasForeignKey(x => x.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.EpsEstimate).HasColumnType("decimal(18,4)");
            entity.Property(x => x.EpsReported).HasColumnType("decimal(18,4)");
            entity.Property(x => x.RevenueEstimate).HasColumnType("decimal(28,2)");
            entity.Property(x => x.RevenueReported).HasColumnType("decimal(28,2)");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(x => x.Username).HasMaxLength(32);
            entity.Property(x => x.NormalizedUsername).HasMaxLength(32);
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(256);
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.HasIndex(x => x.NormalizedUsername).IsUnique();
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
        });
    }
}
