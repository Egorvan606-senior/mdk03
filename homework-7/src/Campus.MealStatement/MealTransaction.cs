namespace Campus.MealStatement;

/// <summary>Запись, полученная из публичного API (jsonplaceholder.typicode.com/posts).</summary>
public sealed record SourcePost(int UserId, int Id, string Title, string Body);

/// <summary>Категория питания и её базовая цена (прейскурант столовой кампуса).</summary>
public sealed record MealCategory(string Code, string Name, decimal BasePrice, TimeOnly From, TimeOnly To);

/// <summary>Одна транзакция питания — строка финансового лога.</summary>
public sealed record MealTransaction(
    string TransactionId,
    DateOnly Date,
    TimeOnly Time,
    string CardNumber,
    string StudentId,
    MealCategory Category,
    int Portions,
    decimal Price,
    decimal Amount,
    decimal Subsidy,
    decimal ToPay,
    string PaymentMethod,
    string CashDesk)
{
    public DateTime Timestamp => Date.ToDateTime(Time);
}

/// <summary>
/// Правила симуляции выгрузки транзакций. Публичный API отдаёт учебные записи
/// (userId, id, title, body); из них детерминированно (при одинаковых входных
/// данных — всегда одинаковый результат) строится транзакция столовой:
///   userId  → владелец карты (студент);
///   id      → номер транзакции, день месяца, время в окне категории;
///   (id + userId) mod 4 → категория питания;
///   число слов body → количество порций; длина title → наценка за состав блюда.
/// </summary>
public static class TransactionSimulator
{
    public static readonly MealCategory[] Categories =
    [
        new("BRK", "Завтрак", 120m, new TimeOnly(8, 0), new TimeOnly(10, 0)),
        new("LNC", "Обед (комплекс)", 185m, new TimeOnly(12, 0), new TimeOnly(14, 30)),
        new("BUF", "Буфет", 65m, new TimeOnly(10, 0), new TimeOnly(17, 0)),
        new("DIN", "Ужин", 160m, new TimeOnly(17, 0), new TimeOnly(19, 30))
    ];

    /// <summary>Студенты с льготным питанием (по приказу о соцподдержке) — дотация 50 % на обед.</summary>
    private static readonly HashSet<int> SubsidizedUsers = [3, 7];

    private const decimal SubsidyRate = 0.5m;

    public static MealTransaction Create(SourcePost post, int year, int month)
    {
        var category = Categories[(post.Id + post.UserId) % Categories.Length];

        int daysInMonth = DateTime.DaysInMonth(year, month);
        var date = new DateOnly(year, month, (post.Id - 1) % daysInMonth + 1);
        // выходные столовая не работает — переносим на ближайший понедельник
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            date = date.AddDays(date.DayOfWeek == DayOfWeek.Saturday ? 2 : 1);
            if (date.Month != month)
            {
                date = new DateOnly(year, month, 1).AddDays(post.Id % 5); // в начало месяца
            }
        }

        int windowMinutes = (int)(category.To - category.From).TotalMinutes;
        var time = category.From.AddMinutes(post.Id * 37 % windowMinutes);

        int words = post.Body.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        int portions = words % 3 == 0 ? 2 : 1; // примерно треть покупок — две порции
        decimal price = category.BasePrice + post.Title.Length % 7 * 5m;
        decimal amount = price * portions;

        decimal subsidy = SubsidizedUsers.Contains(post.UserId) && category.Code == "LNC"
            ? Math.Round(amount * SubsidyRate, 2, MidpointRounding.AwayFromZero)
            : 0m;

        string payment = post.Id % 10 == 0 ? "Наличные" : subsidy > 0 ? "Карта студента + дотация" : "Карта студента";

        return new MealTransaction(
            TransactionId: $"TX-{year}{month:00}-{post.Id:00000}",
            Date: date,
            Time: time,
            CardNumber: $"C{100000 + post.UserId * 17:000000}",
            StudentId: $"ST{200000 + post.UserId:000000}",
            Category: category,
            Portions: portions,
            Price: price,
            Amount: amount,
            Subsidy: subsidy,
            ToPay: amount - subsidy,
            PaymentMethod: payment,
            CashDesk: post.Id % 3 == 0 ? "Касса 2 (корпус Б)" : "Касса 1 (корпус А)");
    }
}
