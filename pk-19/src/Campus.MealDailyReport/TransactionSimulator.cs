namespace Campus.MealDailyReport;

/// <summary>
/// Симуляция транзакций кассы столовой по записям мок-сервера.
/// Преобразование детерминированное: одна и та же выгрузка за одни и те же сутки
/// всегда даёт одинаковый отчёт — это важно для сверки и контрольных сумм.
/// </summary>
public static class TransactionSimulator
{
    // Прейскурант комплексов (руб.)
    private static readonly (string Name, decimal Price)[] Menu =
    [
        ("Завтрак", 95m),
        ("Обед", 185m),
        ("Полдник", 70m)
    ];

    /// <summary>Лимит дотации на одну покупку для бюджетной категории (руб.).</summary>
    public const decimal BudgetSubsidyLimit = 150m;

    private static readonly TimeSpan Opening = new(8, 0, 0);   // столовая открыта 08:00–19:00
    private const int WorkingMinutes = 11 * 60;

    public static MealTransaction Create(SourcePost post, DateOnly date, string stationId, string operatorId)
    {
        // Категория: пользователи 1–4 мок-сервера — студенты-бюджетники, остальные — коммерция
        var category = post.UserId <= 4 ? MealCategory.Budget : MealCategory.Commercial;

        // Время покупки распределяется по рабочему дню столовой
        var time = Opening + TimeSpan.FromMinutes(post.Id * 37 % WorkingMinutes);
        var timestamp = date.ToDateTime(TimeOnly.FromTimeSpan(time));

        // Тип комплекса зависит от времени: до 11:00 — завтрак, 11:00–15:00 — обед, позже — полдник
        var meal = time.Hours < 11 ? Menu[0] : time.Hours < 15 ? Menu[1] : Menu[2];

        // Доп. позиции (напиток, выпечка): 0–4 позиции по 15 руб. — от длины текста записи
        decimal amount = meal.Price + post.Body.Length % 5 * 15m;

        // Статус: каждая 17-я запись — отказ, каждая 23-я — возврат
        var status = post.Id % 17 == 0 ? TransactionStatus.Declined
                   : post.Id % 23 == 0 ? TransactionStatus.Refund
                   : TransactionStatus.Success;

        // Дотация только для бюджета и только по проведённым (не отклонённым) операциям
        decimal subsidy = category == MealCategory.Budget && status != TransactionStatus.Declined
            ? Math.Min(amount, BudgetSubsidyLimit)
            : 0m;

        return new MealTransaction(
            TransactionId: $"TX-{date:yyyyMMdd}-{post.Id:0000}",
            Timestamp: timestamp,
            StationId: stationId,
            OperatorId: operatorId,
            Category: category,
            MealType: meal.Name,
            Amount: amount,
            Subsidy: subsidy,
            Status: status,
            SourceRecordId: post.Id);
    }

    /// <summary>Суммирование финансовых показателей по категориям.</summary>
    public static IReadOnlyList<CategoryTotals> Summarize(IEnumerable<MealTransaction> transactions) =>
        transactions
            .GroupBy(t => t.Category)
            .OrderBy(g => g.Key)
            .Select(g => new CategoryTotals(
                Category: g.Key,
                Success: g.Count(t => t.Status == TransactionStatus.Success),
                Refunds: g.Count(t => t.Status == TransactionStatus.Refund),
                Declined: g.Count(t => t.Status == TransactionStatus.Declined),
                // продажи: проведённые оплаты + оплаты, по которым потом был возврат
                Revenue: g.Where(t => t.Status != TransactionStatus.Declined).Sum(t => t.Amount),
                RefundAmount: g.Where(t => t.Status == TransactionStatus.Refund).Sum(t => t.Amount),
                Subsidy: g.Where(t => t.Status == TransactionStatus.Success).Sum(t => t.Subsidy),
                Paid: g.Where(t => t.Status == TransactionStatus.Success).Sum(t => t.ToPay)))
            .ToList();
}
