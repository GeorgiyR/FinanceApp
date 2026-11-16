namespace FinanceApp.Blazor.Components.Models;

public sealed class HelpCategory
{
    public string Slug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Order { get; init; }
}

public sealed class HelpArticle
{
    public string Slug { get; init; } = string.Empty;
    public string CategorySlug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public double Order { get; init; }
    public IReadOnlyList<HelpSection> Sections { get; init; } = [];
    public IReadOnlyList<HelpRelatedLink> Related { get; init; } = [];
}

public sealed class HelpSection
{
    public string Slug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<HelpBlock> Blocks { get; init; } = [];
}

public sealed class HelpBlock
{
    public string Type { get; init; } = string.Empty;
    public string? Text { get; init; }
    public IReadOnlyList<string>? Items { get; init; }
    public bool Ordered { get; init; }
    public IReadOnlyList<string>? Columns { get; init; }
    public IReadOnlyList<IReadOnlyList<string>>? Rows { get; init; }
    public string? CalloutType { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string>? Body { get; init; }
    public string? Question { get; init; }
    public string? Answer { get; init; }
}

public sealed class HelpRelatedLink
{
    public string ArticleSlug { get; init; } = string.Empty;
    public string? SectionSlug { get; init; }
    public string Label { get; init; } = string.Empty;
}

public static class HelpData
{
    public static readonly IReadOnlyList<HelpCategory> Categories =
    [
        new()
        {
            Slug = "quick-start",
            Title = "Быстрый старт",
            Description = "Первые шаги в интерфейсе, портфели, акции и безопасный старт.",
            Order = 1,
        },
        new()
        {
            Slug = "analytics",
            Title = "Аналитика и показатели",
            Description = "Аналитический сигнал, технические и фундаментальные показатели.",
            Order = 2,
        },
        new()
        {
            Slug = "data-quality",
            Title = "Качество данных",
            Description = "Свежесть, ограничения данных и безопасное обновление.",
            Order = 3,
        },
        new()
        {
            Slug = "stocks-and-indices",
            Title = "Акции и индексы",
            Description = "Каталог, отслеживание бумаг и работа с мировыми индексами.",
            Order = 4,
        },
        new()
        {
            Slug = "portfolios",
            Title = "Портфели и операции",
            Description = "Как вести портфель, позиции и транзакции в текущем UI.",
            Order = 5,
        },
        new()
        {
            Slug = "faq",
            Title = "FAQ и устранение проблем",
            Description = "Короткие ответы на частые вопросы.",
            Order = 6,
        },
    ];

    public static readonly IReadOnlyList<HelpArticle> Articles =
    [
        new()
        {
            Slug = "quick-start",
            CategorySlug = "quick-start",
            Title = "Быстрый старт в FinanceApp",
            Summary = "Как войти в приложение, создать портфель, добавить акции и начать анализ без лишнего риска.",
            Keywords = ["вход", "навигация", "портфель", "позиции", "транзакции", "отслеживаемые акции", "каталог", "EUR"],
            Order = 1,
            Related =
            [
                new() { ArticleSlug = "analytical-signal", SectionSlug = null, Label = "Подробно об аналитическом сигнале" },
                new() { ArticleSlug = "portfolios-and-operations", SectionSlug = null, Label = "Портфели и операции" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "quick-start-auth-navigation",
                    Title = "Вход и базовая навигация",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "После входа открывается защищённая часть приложения. Основная навигация находится в левом сайдбаре: «Главная», «Портфели», «Акции», «Справочники» и «Система».",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["На мобильных устройствах навигация открывается кнопкой «Открыть навигацию».", "Пункт «Справочники» содержит «Секторы и отрасли», «Финансовые показатели» и эту страницу справки.", "Пункт «Система → Журнал процессов» показывает активные и завершённые фоновые процессы приложения.", "Навигация работает внутри SPA, поэтому переходы не требуют полной перезагрузки страницы."],
                        },
                    ],
                },
                new()
                {
                    Slug = "quick-start-portfolios",
                    Title = "Создание и выбор портфеля",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "В разделе «Портфели» создайте портфель и откройте его. Для активного портфеля доступны подразделы «Позиции» и «Транзакции».",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Позиция отражает количество и среднюю цену покупки бумаги в выбранном портфеле. Транзакции используются для фиксации операций.",
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "Безопасный сценарий первого дня",
                            Body = ["Создайте тестовый портфель с понятным названием.", "Добавьте 1–2 бумаги небольшими объёмами.", "Проверьте карточку позиции и таблицу транзакций.", "Сравните данные по бумаге на горизонтах 3 месяца и 1 год в блоке аналитического сигнала."],
                        },
                    ],
                },
                new()
                {
                    Slug = "quick-start-stocks-catalog",
                    Title = "Отслеживаемые акции и каталог",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "В «Акции» есть два потока: «Отслеживаемые акции» (ваш рабочий список) и «Список акций» (общий каталог).",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Каталог нужен для поиска и добавления бумаг в отслеживание.", "Отслеживаемые акции — основной экран с быстрыми действиями, графиком и аналитикой.", "Открытие расширенной строки акции показывает график, фундаментальные данные и «Аналитический сигнал»."],
                        },
                    ],
                },
                new()
                {
                    Slug = "quick-start-fundamentals-and-currency",
                    Title = "Где смотреть фундаментальные данные, сигнал и валюту EUR",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Фундаментальные показатели открываются в отдельной панели/окне по акции. «Аналитический сигнал» находится в расширенной области графика акции.",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Отображение в EUR зависит от наличия актуальных курсов. Если курс недоступен, приложение показывает предупреждение и сохраняет исходные значения без недостоверной конвертации.",
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "important",
                            Body = ["Для новых пользователей безопасно начинать с наблюдения: добавьте бумаги в отслеживание, проверьте свежесть данных и только затем анализируйте горизонты."],
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "analytical-signal",
            CategorySlug = "analytics",
            Title = "Аналитический сигнал: как читать",
            Summary = "Подробная расшифровка Score, Confidence, горизонтов, весов компонентов, ограничений и предупреждений.",
            Keywords = ["Score", "Confidence", "3 месяца", "6 месяцев", "1 год", "2 года", "веса", "ренормализация", "disclaimer"],
            Order = 2,
            Related =
            [
                new() { ArticleSlug = "technical-indicators", SectionSlug = null, Label = "Технические показатели: детали" },
                new() { ArticleSlug = "data-quality-and-freshness", SectionSlug = null, Label = "Качество и свежесть данных" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "signal-location",
                    Title = "Где находится блок «Аналитический сигнал»",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Блок находится в расширенной области выбранной акции рядом с графиком цены. Это НЕ вкладка модального окна фундаментальных показателей.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Откройте раздел «Акции».", "Разверните нужную бумагу.", "Найдите заголовок «Аналитический сигнал» и переключатели горизонта."],
                        },
                    ],
                },
                new()
                {
                    Slug = "signal-horizons-and-thresholds",
                    Title = "Горизонты, Signal и пороги Score",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Сервис считает сигнал сразу по четырём горизонтам: 3 месяца, 6 месяцев, 1 год, 2 года. Один и тот же тикер может давать разные результаты на разных горизонтах.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Диапазон Score", "Код сигнала", "Текст в UI"],
                            Rows =
                            [
                                ["80–100", "StrongBullish", "Сильный бычий"],
                                ["65–<80", "ModeratelyBullish", "Умеренно бычий"],
                                ["45–<65", "Neutral", "Нейтральный"],
                                ["30–<45", "ModeratelyBearish", "Умеренно медвежий"],
                                ["0–<30", "StrongBearish", "Сильный медвежий"],
                            ],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Score — агрегированная оценка (0..100). Confidence — степень уверенности модели в текущем расчёте (0..1, в UI — проценты). Высокий Score не гарантирует высокий Confidence.",
                        },
                    ],
                },
                new()
                {
                    Slug = "signal-components-weights",
                    Title = "Компоненты и точные веса по горизонтам",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Компоненты: Trend, Momentum, Returns, Risk, Fundamentals. Вес зависит от горизонта.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Горизонт", "Trend", "Momentum", "Returns", "Risk", "Fundamentals"],
                            Rows =
                            [
                                ["3 месяца", "35%", "35%", "20%", "10%", "0%"],
                                ["6 месяцев", "35%", "25%", "20%", "15%", "5%"],
                                ["1 год", "30%", "15%", "20%", "15%", "20%"],
                                ["2 года", "15%", "5%", "15%", "20%", "45%"],
                            ],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Если компонент недоступен, он не приравнивается к нулю. Сервис ренормализует веса по доступным компонентам и добавляет предупреждение о неполной компонентной базе.",
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "warning",
                            Title = "Почему это важно",
                            Body = ["«Недостаточно данных» у компонента означает отсутствие входных данных, а не «очень плохо».", "Итоговый Score в таком случае сравним только с оговорками по confidence и warning-факторам."],
                        },
                    ],
                },
                new()
                {
                    Slug = "signal-factors-confidence-stale",
                    Title = "Факторы, confidence и stale-состояния",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Положительные факторы и факторы риска показывают, что именно подтолкнуло оценку вверх или вниз. Блок «Предупреждения» сообщает о качестве данных и ограничениях расчёта.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Confidence снижается при недостаточной длине истории, stale-данных, пропусках компонентов и проблемах фундаментальных данных на длинных горизонтах.", "Если история/фундаментал устарели, в UI отображаются предупреждения о потенциально устаревшем сигнале.", "Endpoint и чтение UI не запускают автоматически обновление провайдера только ради аналитики."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "important",
                            Body = ["Аналитическая информация не является персональной инвестиционной рекомендацией, сигналом Buy/Sell и не гарантирует будущий результат."],
                        },
                    ],
                },
                new()
                {
                    Slug = "signal-worked-examples",
                    Title = "Разбор примеров",
                    Blocks =
                    [
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "Пример 1: высокий Score, низкий Confidence",
                            Body = ["На 6 месяцев Score = 78, но Confidence = 42%.", "Причина: часть компонент недоступна, а история неполная/устаревшая.", "Вывод: направление кажется позитивным, но устойчивость оценки низкая — нужна дополнительная проверка данных."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "Пример 2: 3 месяца и 2 года противоречат",
                            Body = ["3 месяца: ModeratelyBullish (краткосрочный тренд и momentum сильные).", "2 года: ModeratelyBearish/StrongBearish (вес fundamentals и риск выше, длинный контекст слабее).", "Вывод: короткое восстановление не отменяет долгосрочные фундаментальные ограничения."],
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "technical-indicators",
            CategorySlug = "analytics",
            Title = "Технические показатели в FinanceApp: краткий обзор",
            Summary = "Короткая вводная статья по SMA/EMA/RSI/MACD, доходности, волатильности, просадке и ATR. Полный учебный разбор с формулами и примерами — в статье «Технические показатели: формулы, примеры и ограничения».",
            Keywords = ["SMA20", "SMA50", "SMA200", "EMA12", "EMA26", "RSI14", "MACD", "ATR14", "AdjustedClose", "Close"],
            Order = 3,
            Related =
            [
                new() { ArticleSlug = "technical-indicator-formulas", SectionSlug = null, Label = "Полный учебный разбор формул и примеров" },
                new() { ArticleSlug = "analytical-signal", SectionSlug = null, Label = "Как эти показатели влияют на сигнал" },
                new() { ArticleSlug = "data-quality-and-freshness", SectionSlug = null, Label = "Почему метрики бывают недоступны" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "indicators-sma-ema",
                    Title = "SMA20/SMA50/SMA200 и EMA12/EMA26",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Этот материал — краткий обзор. За полным пошаговым разбором формул, warm-up и примеров переходите к статье «Технические показатели: формулы, примеры и ограничения».",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "SMA и EMA — скользящие средние. В FinanceApp они используются для оценки тренда и momentum-компоненты.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["SMA20 и SMA50 — кратко/среднесрочный фон.", "SMA200 — долгий тренд; при короткой истории может быть недоступна.", "EMA12/EMA26 — более чувствительные средние для momentum."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "warning",
                            Body = ["Отсутствие SMA200 обычно означает недостаточную историю свечей, а не ошибку интерфейса."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicators-rsi-macd",
                    Title = "RSI14, MACD, сигнальная линия и гистограмма",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "RSI14 показывает баланс импульса, MACD — динамику расхождения EMA и разворотные фазы.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["RSI выше нейтральной зоны чаще поддерживает бычий momentum.", "Гистограмма MACD выше 0 обычно трактуется как усиление импульса.", "Сигнальная линия и гистограмма показываются в раскрытии «Показатели»."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicators-returns-risk",
                    Title = "Доходности, волатильность, max drawdown и ATR14",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Доходности 1м/3м/6м/1г используются в компоненте Returns. Волатильность, max drawdown и ATR14 — в компоненте Risk.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Annualized volatility показывает амплитуду колебаний.", "Max drawdown — текущая/историческая глубина просадки.", "ATR14 — средний истинный диапазон; показывает «ширину» дневного движения."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "info",
                            Body = ["Даже высокий риск не всегда обнуляет Score, но обычно понижает итоговую оценку и/или confidence."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicators-price-basis-and-history",
                    Title = "AdjustedClose vs Close и требования к данным",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Close-based показатели (SMA/EMA/RSI/MACD и доходности) в приоритете используют AdjustedClose и по каждой свече fallback на Close, если adjusted-значение отсутствует.",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "ATR14 рассчитывается по не-скорректированному OHLC, потому что adjusted OHLC в хранилище не сохраняется.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Неполное покрытие AdjustedClose снижает качество сигнала и может добавить warning.", "При недостаточной истории часть метрик/компонент недоступна.", "В раскрытии «Показатели» можно увидеть coverage и базу расчёта."],
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "fundamentals",
            CategorySlug = "analytics",
            Title = "Фундаментальные показатели и экраны",
            Summary = "Какие поля доступны в фундаментальных данных, как читать вкладки и что означает свежесть/пропуски.",
            Keywords = ["Market Cap", "Enterprise Value", "TTM", "P/E", "P/B", "Dividend Yield", "As of"],
            Order = 4,
            Related =
            [
                new() { ArticleSlug = "analytical-signal", SectionSlug = "signal-components-weights", Label = "Веса компонентов сигнала" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "fundamentals-where-and-fields",
                    Title = "Где смотреть и какие поля есть",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Фундаментальные данные открываются в панели «фундаментальные показатели» для конкретной акции.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Группа", "Поля в текущем UI"],
                            Rows =
                            [
                                ["Размер/структура", "Market Cap, Enterprise Value, Total Debt, Cash"],
                                ["TTM", "TTM Revenue, TTM Net Income, TTM EBITDA, TTM FCF"],
                                ["Мультипликаторы", "P/E, Forward P/E, P/B, Dividend Yield"],
                                ["Баланс", "Total Assets, Total Liabilities"],
                            ],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Также доступны вкладки с периодами: «Годовые», «Квартальные», «Отчёты». Не каждый провайдер заполняет все поля для каждой бумаги.",
                        },
                    ],
                },
                new()
                {
                    Slug = "fundamentals-source-freshness-refresh",
                    Title = "Источник, As-of, свежесть и обновление",
                    Blocks =
                    [
                        new()
                        {
                            Type = "list",
                            Items = ["Верхняя часть панели показывает источник и символ провайдера.", "Тег Fresh/Stale отражает состояние снимка данных.", "As-of показывает дату актуальности отчётных данных, если доступна.", "Кнопка «Обновить» выполняет ручной refresh и может вернуть stale-снимок с предупреждением."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "warning",
                            Body = ["Один snapshot не формирует тренд сам по себе — для тренда используйте историю периодов и сравнение нескольких дат."],
                        },
                    ],
                },
                new()
                {
                    Slug = "fundamentals-limitations-and-signal-impact",
                    Title = "Ограничения и влияние на длинные горизонты сигнала",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "При отсутствии/устаревании fundamentals компонент Fundamentals может быть недоступен или понижен по confidence, особенно на горизонтах 1 год и 2 года.",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Если исторических фундаментальных периодов недостаточно, система явно добавляет предупреждения и уменьшает надёжность длинного горизонта.",
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "data-quality-and-freshness",
            CategorySlug = "data-quality",
            Title = "Качество и свежесть данных",
            Summary = "Как понимать stale/fresh, почему confidence падает и что делать пользователю при проблемах данных.",
            Keywords = ["stale", "fresh", "As-of", "timestamp", "insufficient history", "SMA200 unavailable", "AdjustedClose coverage"],
            Order = 5,
            Related =
            [
                new() { ArticleSlug = "technical-indicators", SectionSlug = null, Label = "Технические показатели и требования к истории" },
                new() { ArticleSlug = "faq", SectionSlug = null, Label = "FAQ по устаревшим данным" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "data-quality-labels-and-timestamps",
                    Title = "Source/As-of, quote timestamp и даты свечей",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Подписи Source и As-of описывают происхождение и датировку данных. Для цены и графика важно отличать timestamp последней котировки и дату последней исторической свечи.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Свежесть котировки и свежесть дневной истории — разные вещи.", "As-of может относиться к фундаментальному снимку, а не к intraday-цене.", "Для аналитического сигнала stale определяется относительно доступной дневной истории."],
                        },
                    ],
                },
                new()
                {
                    Slug = "data-quality-common-warnings",
                    Title = "Типичные предупреждения и их смысл",
                    Blocks =
                    [
                        new()
                        {
                            Type = "list",
                            Items = ["Недостаточно истории: часть метрик/компонент недоступна.", "SMA200 unavailable: мало дневных свечей для окна 200.", "AdjustedClose coverage incomplete: часть свечей посчитана по Close fallback.", "Duplicate/invalid/constant series warnings: входной ряд содержит аномалии качества.", "Missing/stale fundamentals: длинные горизонты теряют уверенность."],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Чем больше таких warning-факторов, тем ниже confidence даже при визуально неплохом Score.",
                        },
                    ],
                },
                new()
                {
                    Slug = "data-quality-safe-user-actions",
                    Title = "Что безопасно делать пользователю при stale-данных",
                    Blocks =
                    [
                        new()
                        {
                            Type = "list",
                            Items = ["Запустить ручное обновление там, где UI явно предоставляет кнопку refresh.", "Сравнить несколько горизонтов вместо решения по одному числу.", "Проверить предупреждения в блоке сигнала и в фундаментальной панели.", "При критичных решениях перепроверить данные внешним источником."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "important",
                            Body = ["Обычное открытие экрана/endpoint чтения не запускает автоматическое обновление провайдера для technical-analysis."],
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "stocks-and-indices",
            CategorySlug = "stocks-and-indices",
            Title = "Акции и мировые индексы",
            Summary = "Как работать с отслеживаемыми акциями, каталогом, индексами и их составляющими.",
            Keywords = ["tracked stocks", "catalog", "market indices", "constituents", "history refresh"],
            Order = 6,
            Related =
            [
                new() { ArticleSlug = "quick-start", SectionSlug = null, Label = "Первые шаги с акциями" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "stocks-tracked-vs-catalog",
                    Title = "Отслеживаемые акции и каталог",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Отслеживаемые акции — ваш рабочий набор. Каталог — более широкий список бумаг, из которого обычно добавляют бумаги в отслеживание.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Добавление/удаление отслеживания меняет ваш рабочий список, но не удаляет саму бумагу из общего каталога.", "Для разных листингов важно ориентироваться на тикер/биржу/идентичность инструмента.", "Если истории мало, часть показателей и периодных сравнений может быть недоступна."],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "В режиме сортировки «Рост за период» каталог строится как единый глобальный список: порядок зависит только от выбранного периода и направления сортировки.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Членство в индексах, биржа и факт нахождения в портфеле не влияют на порядок при периодной сортировке.", "Значения с доступной доходностью сортируются первыми, записи без доступного значения всегда уходят в конец.", "Поиск по названию индекса остаётся обычным текстовым поиском и не является скрытым индекс-фильтром сортировки."],
                        },
                    ],
                },
                new()
                {
                    Slug = "stocks-chart-ranges-refresh",
                    Title = "Диапазоны графика и ручное обновление истории",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "На графике доступны диапазоны истории; периодные проценты в таблицах/карточках зависят от наличия данных за соответствующее окно.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Каждый листинг (ticker + биржа + provider symbol) отображается отдельно: график Frankfurt не объединяется с NYSE/NASDAQ, даже если компания одна и та же.", "«24 ч.» = полная предыдущая торговая сессия + текущая торговая сессия выбранного листинга (в локальном времени биржи с учётом выходных/праздников), а не скользящее «сейчас − 24 часа».", "«Сегодня» всегда показывает только текущую торговую сессию выбранного листинга.", "В «24 ч.» и «Сегодня» межсессионный разрыв (overnight gap) сохраняется: приложение не соединяет предыдущее закрытие с новым открытием и не дорисовывает несуществующие свечи.", "В заголовке графика показывается «Данные на …» — это timestamp последней исторической свечи выбранного диапазона.", "Если история устарела или источник временно недоступен, показывается предупреждение; при этом сохранённые свечи не затираются пустым графиком.", "После успешного обновления котировки (ручного или фонового) новая точка попадает в сохранённую внутридневную историю и затем сверяется с провайдерными свечами при следующем refresh.", "Ручное действие «Перезагрузить историю» заново запрашивает дневные и внутридневные интервалы (включая 5m→10m и 1h), поэтому после него quote-derived точки могут быть заменены точными провайдерными свечами."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indices-navigation-and-constituents",
                    Title = "Мировые индексы: навигация и constituents",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "В разделе «Акции» есть вложенное меню «Мировые индексы»: экран управления и страницы конкретных индексов.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["На страницах индексов доступны графики и список constituents.", "Состав индекса не равен списку ваших отслеживаемых бумаг.", "Для режимов «24ч» и «Сегодня» сначала используется внутридневная история, а при разреженных данных — текущий snapshot/изменение к предыдущему закрытию.", "На выходных и закрытом рынке данные могут обновляться реже: при отсутствии валидной истории и snapshot строка помечается как недоступная.", "Если у constituent недостаточно валидных данных, значение доходности помечается как недоступное и сортируется в конце."],
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "portfolios-and-operations",
            CategorySlug = "portfolios",
            Title = "Портфели и операции",
            Summary = "Практическая работа с портфелями, позициями, транзакциями и валютными оговорками в текущем продукте.",
            Keywords = ["портфель", "позиция", "транзакции", "average purchase price", "EUR conversion"],
            Order = 7,
            Related =
            [
                new() { ArticleSlug = "quick-start", SectionSlug = "quick-start-portfolios", Label = "Быстрый старт по портфелям" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "portfolio-create-select",
                    Title = "Создание и выбор портфеля",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Создайте портфель, выберите его в сайдбаре, затем переходите в «Позиции» и «Транзакции».",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Позиции показывают количество и среднюю цену покупки.", "Транзакции отражают историю операций в портфеле.", "Редактирование/удаление записей немедленно влияет на расчёты, связанные с портфелем."],
                        },
                    ],
                },
                new()
                {
                    Slug = "portfolio-operation-safety-workflow",
                    Title = "Безопасный рабочий сценарий",
                    Blocks =
                    [
                        new()
                        {
                            Type = "list",
                            Items = ["Сначала проверьте, что выбран правильный портфель.", "Добавляйте/редактируйте операции небольшими шагами.", "После каждого изменения перепроверьте количество и среднюю цену по позиции.", "При валютных расхождениях проверьте предупреждения о недостающем EUR-курсе."],
                            Ordered = true,
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "В текущем UI не следует предполагать автоматические налоговые/брокерские сверки, если они явно не отображаются.",
                        },
                    ],
                },
                new()
                {
                    Slug = "portfolio-eur-and-missing-rates",
                    Title = "EUR-конвертация и предупреждения о курсах",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Часть экранов показывает значения в EUR при доступных курсах. Если курсов не хватает, приложение предупреждает и избегает некорректной конвертации.",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "При анализе результатов учитывайте валюту источника и дату котировки/отчёта.",
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "faq",
            CategorySlug = "faq",
            Title = "FAQ и устранение проблем",
            Summary = "Короткие ответы на частые вопросы пользователей FinanceApp.",
            Keywords = ["FAQ", "где аналитический сигнал", "нет SMA200", "401", "network error", "hard refresh"],
            Order = 8,
            Related =
            [
                new() { ArticleSlug = "analytical-signal", SectionSlug = "signal-location", Label = "Где находится сигнал (подробно)" },
                new() { ArticleSlug = "data-quality-and-freshness", SectionSlug = null, Label = "Раздел про свежесть данных" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "faq-common",
                    Title = "Частые вопросы",
                    Blocks =
                    [
                        new()
                        {
                            Type = "qa",
                            Question = "Где находится аналитический сигнал?",
                            Answer = "В расширенной области акции рядом с графиком цены. Не в окне фундаментальных показателей.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему его нет в окне фундаментальных показателей?",
                            Answer = "Фундаментальные данные и технический сигнал разделены: fundamentals — отдельная панель, сигнал — часть расширенного блока графика.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему Score высокий, а Confidence низкий?",
                            Answer = "Оценка может быть высокой, но при этом уверенность снижается из-за stale-данных, недостающих компонент или неполной истории.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему результаты на 3 месяца и 2 года отличаются?",
                            Answer = "Разные горизонты используют разные веса компонент. На 2 года значительно выше вес fundamentals и риска.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему компонент показывает «Недостаточно данных»?",
                            Answer = "Для компонента не хватило входных данных. Это не равно нулевому score.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему нет SMA200?",
                            Answer = "Недостаточно длины дневной истории для окна 200 свечей.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему фундаментальные данные отсутствуют или устарели?",
                            Answer = "Провайдер мог не вернуть полный набор полей или свежий snapshot. Проверьте state и предупреждения в панели fundamentals.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему цена/график выглядит устаревшим?",
                            Answer = "Последняя свеча/котировка может отставать от текущего времени. Проверьте метки времени и при необходимости выполните доступный refresh.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Почему доходность отличается от изменения текущей цены?",
                            Answer = "Доходность считается по фиксированным историческим окнам и базе цен (AdjustedClose/Close fallback), а не только по двум последним отображаемым значениям.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Что означает AdjustedClose?",
                            Answer = "Это скорректированная цена закрытия; в FinanceApp close-based метрики предпочитают её, с fallback на Close по каждой свече.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "Как обновить данные безопасно?",
                            Answer = "Используйте только явные кнопки обновления в интерфейсе и после обновления перепроверьте warning-метки и confidence.",
                        },
                        new()
                        {
                            Type = "qa",
                            Question = "После обновления приложения видна старая версия. Что делать?",
                            Answer = "Сделайте hard refresh страницы (Ctrl/Cmd+Shift+R) или откройте приложение в приватном окне, чтобы исключить кэш браузера.",
                        },
                    ],
                },
                new()
                {
                    Slug = "faq-auth-and-network-errors",
                    Title = "401/session expiry и сетевые ошибки",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "При 401 обычно требуется повторная авторизация. Если сессия истекла, войдите заново.",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "При сетевых/серверных ошибках попробуйте повторить запрос позже. Если ошибка стабильна, проверьте подключение и обратитесь к команде поддержки с указанием шага и времени ошибки.",
                        },
                    ],
                },
            ],
        },
        new()
        {
            Slug = "technical-indicator-formulas",
            CategorySlug = "analytics",
            Title = "Технические показатели: формулы, примеры и ограничения",
            Summary = "Учебное объяснение SMA, EMA, RSI14, MACD, доходности, волатильности, просадки и ATR14 — от входных свечей до Score и Confidence.",
            Keywords = ["формулы", "технический анализ", "что такое скользящая средняя", "SMA20", "SMA50", "SMA200", "EMA12", "EMA26", "экспоненциальная средняя", "RSI14", "формула RSI", "Relative Strength Index", "Wilder", "формула Уайлдера", "MACD 12/26/9", "MACD Signal Histogram", "доходность", "логарифмическая доходность", "Volatility20", "Volatility60", "Current Drawdown", "MaxDrawdown", "ATR14", "True Range", "AdjustedClose fallback", "Close", "Score", "Confidence", "Недостаточно данных"],
            Order = 3.5,
            Related =
            [
                new() { ArticleSlug = "analytical-signal", SectionSlug = "signal-components-weights", Label = "Как показатели входят в итоговый сигнал" },
                new() { ArticleSlug = "data-quality-and-freshness", SectionSlug = null, Label = "Качество и свежесть входных данных" },
                new() { ArticleSlug = "technical-indicators", SectionSlug = null, Label = "Краткий обзор технических показателей" },
            ],
            Sections =
            [
                new()
                {
                    Slug = "indicator-methodology",
                    Title = "Сначала о данных: свечи, цены и торговые дни",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Технический показатель — это математическое описание уже произошедшего движения цены. Он помогает одинаково измерять тренд, импульс и риск, но не предсказывает будущее и не является командой Buy или Sell.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["Backend загружает до 800 сохранённых дневных свечей 1d, сортирует их по времени и при одинаковом timestamp оставляет запись с наибольшим Id.", "Для каждой свечи effective price выбирается отдельно: положительный AdjustedClose, иначе положительный Close. Это per-candle fallback, а не единый режим для всего ряда.", "AdjustedClose coverage показывает долю точек, где использован AdjustedClose. Неполное покрытие может снизить Confidence и добавить ADJUSTED_CLOSE_FALLBACK.", "Открытие аналитики только читает сохранённые данные и не запускает запрос к провайдеру или обновление истории.", "Торговое наблюдение — строка дневной истории, а не календарный день. Поэтому 21 наблюдение примерно соответствует месяцу, но праздники и пропуски меняют календарную длину.", "Если истории недостаточно, метрика возвращается как null и показывается как «Недостаточно данных». null не равен нулю.", "Свежесть оценивается по UTC: последняя свеча считается потенциально устаревшей, если её дата отстаёт от текущей UTC-даты более чем на 3 дня."],
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Метрика", "Минимум данных", "Единица"],
                            Rows =
                            [
                                ["SMA20 / SMA50 / SMA200", "20 / 50 / 200 закрытий", "Цена"],
                                ["EMA12 / EMA26", "12 / 26 закрытий", "Цена"],
                                ["RSI14", "15 закрытий (14 изменений)", "0–100"],
                                ["MACD line", "26 закрытий", "Цена"],
                                ["MACD Signal / Histogram", "34 закрытия", "Цена"],
                                ["Доходность 1м / 3м / 6м / 1г", "22 / 64 / 127 / 253 закрытия", "Процентные пункты"],
                                ["Volatility20 / Volatility60", "21 / 61 закрытие", "Доля; UI показывает % годовых"],
                                ["Current drawdown (поле MaxDrawdown)", "1 закрытие; окно до 252", "Процентные пункты, ≤ 0%"],
                                ["ATR14", "15 свечей OHLC", "Абсолютные единицы цены"],
                            ],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "warning",
                            Title = "Почему цифры могут отличаться",
                            Body = ["Брокер, Yahoo или TradingView могут использовать другую биржу, timezone, набор свечей, adjusted/raw режим, EMA seed, округление или длину истории. Сначала сравнивайте входные свечи и методику, а не только итоговое число."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-sma-ema",
                    Title = "SMA20/50/200 и EMA12/26: средняя цена и тренд",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "SMA отвечает на вопрос «какова средняя цена последних N наблюдений?». Формула: SMA(N) = (P₁ + … + Pₙ) / N. Например, для цен 100, 102, 101, 103 и 104: SMA5 = 510 / 5 = 102.",
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "EMA сильнее учитывает свежие цены. FinanceApp сначала берёт SMA первых N значений как seed, затем применяет EMAₜ = Priceₜ × k + EMAₜ₋₁ × (1 − k), где k = 2 / (N + 1). Для N=12 k≈0,1538. Если предыдущая EMA12=100, а новая цена=103, новая EMA≈103×0,1538+100×0,8462=100,46.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Проверка", "Влияние"],
                            Rows =
                            [
                                ["Цена > SMA50", "Trend +10; иначе, включая равенство, −10"],
                                ["Цена > SMA200", "Trend +10; иначе −10"],
                                ["SMA50 > SMA200", "Trend +15; иначе −15"],
                                ["SMA20 > SMA50", "Trend +8; иначе −8"],
                                ["EMA12 > EMA26", "Momentum +8; иначе −8"],
                            ],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "important",
                            Body = ["Средняя запаздывает за ценой. Пересечение описывает текущую структуру ряда, но не гарантирует продолжение движения."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-rsi14",
                    Title = "RSI14: сила недавних ростов и падений",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "RSI14 сравнивает сглаженные положительные и отрицательные изменения. Seed AvgGain и AvgLoss — средние первых 14 приростов и модулей падений. Затем применяется Wilder smoothing: Avgₜ = (Avgₜ₋₁ × 13 + Currentₜ) / 14. RS = AvgGain / AvgLoss; RSI = 100 − 100 / (1 + RS).",
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "Числовой пример",
                            Body = ["Если суммарный рост первых 14 изменений равен 7, а суммарное падение — 7, AvgGain=AvgLoss=0,5, RS=1 и RSI=50. Если AvgLoss=0, FinanceApp возвращает RSI=100; если AvgGain=0 — RSI=0."],
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["RSI14", "Momentum", "Смысл правила"],
                            Rows =
                            [
                                ["55–70 включительно", "+10", "Положительный импульс"],
                                ["> 70", "+2 и warning", "Перекупленность"],
                                ["45–<55", "0", "Нейтральная зона"],
                                ["30–<45", "−8", "Слабый импульс"],
                                ["< 30", "−2 и warning", "Перепроданность"],
                            ],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "warning",
                            Body = ["Перекупленность не означает автоматическую продажу, а перепроданность — автоматическую покупку. Сильный тренд может долго удерживать RSI у края диапазона."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-macd",
                    Title = "MACD 12/26/9, Signal и Histogram",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "MACD line = EMA12 − EMA26. Signal line — EMA9 последовательности MACD. Histogram = MACD line − Signal line. MACD показывает расхождение быстрой и медленной средних; Histogram — положение MACD относительно Signal.",
                        },
                        new()
                        {
                            Type = "list",
                            Items = ["MACD line появляется с 26 закрытий.", "Signal и Histogram требуют 9 значений MACD и появляются с 34 закрытий. Signal seed — среднее первых 9 MACD, далее EMA9 с k=2/10.", "Histogram ≥ 0 даёт Momentum +12; Histogram < 0 даёт −12. Равенство относится к положительной ветке."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "Пример",
                            Body = ["MACD=1,10 и Signal=0,80: Histogram=1,10−0,80=0,30, поэтому правило Histogram добавляет +12. Положительный результат не гарантирует рост цены."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-returns",
                    Title = "Доходность 1м, 3м, 6м и 1г",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Формула: Return(N) = (Pпоследняя / P N торговых дней назад − 1) × 100. При цене 100 двадцать один торговый день назад и 108 сейчас: (108/100−1)×100=8%. Результат хранится в процентных пунктах: 8 означает 8%, а не 800%.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Период", "Lookback", "Закрытий"],
                            Rows =
                            [
                                ["1 месяц", "21 торговый день", "22"],
                                ["3 месяца", "63", "64"],
                                ["6 месяцев", "126", "127"],
                                ["1 год", "252", "253"],
                            ],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Returns использует доступные окна с весами выбранного горизонта; взвешенный результат ограничивается clamp в −25…+25 и прибавляется к базовым 50. Недоступные окна не превращаются в ноль.",
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-volatility-drawdown",
                    Title = "Volatility20/60 и текущая просадка",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Дневная логарифмическая доходность rₜ = ln(Pₜ/Pₜ₋₁). Volatility = σpopulation(r) × √252. Population standard deviation делит сумму квадратов отклонений на N. √252 переводит дневной разброс в условный годовой: 0,24 в API показывается как 24%, а не 0,24%.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Volatility60", "Risk"],
                            Rows =
                            [
                                ["≤ 20%", "+12"],
                                [">20%–≤30%", "0"],
                                [">30%–≤40%", "−10"],
                                [">40%", "−20"],
                            ],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Поле API/UI называется MaxDrawdown, но реализация считает Current Drawdown: (Pпоследняя / MaxCloseокна − 1) × 100 по окну до 252 закрытий. Это НЕ максимальная историческая просадка peak-to-trough. При максимуме 120 и последней цене 90 результат (90/120−1)×100=−25%.",
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["Current drawdown", "Risk"],
                            Rows =
                            [
                                ["≥ −10%", "+10"],
                                ["<−10%–≥−20%", "0"],
                                ["< −20%–≥−35%", "−10"],
                                ["< −35%", "−20"],
                            ],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "important",
                            Body = ["Высокая волатильность означает большой разброс изменений, а не обязательно падение: быстро растущая цена тоже может быть волатильной."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-atr14",
                    Title = "True Range и ATR14: абсолютный дневной диапазон",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "TR = max(High − Low, |High − Previous Close|, |Low − Previous Close|). Гэпы учитываются через расстояние до предыдущего Close. Seed ATR — среднее первых 14 TR; далее ATRₜ = (ATRₜ₋₁ × 13 + TRₜ) / 14.",
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "Пример",
                            Body = ["High=105, Low=99, Previous Close=100: TR=max(6,5,1)=6. ATR имеет абсолютные единицы цены. Для сравнения инструментов Risk использует ATR14/latest price: ATR=2 и цена=100 дают 2%."],
                        },
                        new()
                        {
                            Type = "table",
                            Columns = ["ATR14 / latest price", "Risk"],
                            Rows =
                            [
                                ["≤ 2%", "+6"],
                                [">2%–≤5%", "0"],
                                [">5%", "−12"],
                            ],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "warning",
                            Body = ["ATR использует не скорректированные High, Low и Previous Close, тогда как остальные close-based показатели предпочитают AdjustedClose. Из-за отсутствия adjusted OHLC сплит может временно исказить ATR."],
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-scoring",
                    Title = "Как показатели превращаются в Score",
                    Blocks =
                    [
                        new()
                        {
                            Type = "paragraph",
                            Text = "Каждый компонент стартует с 50, получает описанные бонусы и штрафы и ограничивается диапазоном 0…100. Итог: Score = Σ(ComponentScoreᵢ × NormalizedWeightᵢ). Если компонент недоступен, он исключается, а веса доступных компонентов ренормализуются до суммы 1.",
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "example",
                            Title = "End-to-end пример для горизонта 3 месяца",
                            Body = ["Предположим: Trend=83, Momentum=80, Returns=58, Risk=50. Веса 0,35 / 0,35 / 0,20 / 0,10.", "Score = 83×0,35 + 80×0,35 + 58×0,20 + 50×0,10 = 73,65.", "Если Momentum=null, остаётся сумма весов 0,65. Новый Score = (83×0,35 + 58×0,20 + 50×0,10) / 0,65 = 70,23."],
                        },
                        new()
                        {
                            Type = "paragraph",
                            Text = "Score и Confidence отвечают на разные вопросы. Score описывает направление правил, Confidence — достаточность и качество данных. Высокий Score при низком Confidence не становится надёжным прогнозом.",
                        },
                    ],
                },
                new()
                {
                    Slug = "indicator-missing-confidence",
                    Title = "Confidence, предупреждения и границы интерпретации",
                    Blocks =
                    [
                        new()
                        {
                            Type = "list",
                            Items = ["Confidence складывается из покрытия истории (45%), свежести (20%), AdjustedClose coverage (15%) и доступности компонентов (20%).", "Среди предупреждений возможны HISTORY_STALE, HISTORY_INSUFFICIENT, ADJUSTED_CLOSE_FALLBACK и CONSTANT_PRICE_SERIES.", "Постоянный ценовой ряд математически даёт volatility=0, но предупреждение сообщает, что это может быть признаком плохих данных.", "Технические показатели используют исторические цены и не учитывают автоматически новости, ликвидность, отчётность или индивидуальный риск инвестора."],
                        },
                        new()
                        {
                            Type = "callout",
                            CalloutType = "important",
                            Title = "Не инвестиционная рекомендация",
                            Body = ["Score — детерминированная эвристика, а не справедливая стоимость, прогноз, гарантия результата или персональная инвестиционная рекомендация. Проверяйте исходные данные и принимайте решение в контексте компании и рынка."],
                        },
                    ],
                },
            ],
        },
    ];
}
