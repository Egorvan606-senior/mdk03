namespace Campus.MealDailyReport;

/// <summary>Запись публичного мок-сервера jsonplaceholder (/posts) — «сырая» строка выгрузки кассы.</summary>
public sealed record SourcePost(int UserId, int Id, string Title, string Body);

/// <summary>Категория питания (по отчётной форме «Ведомость учёта питания»: бюджет / коммерция).</summary>
public enum MealCategory
{
    Budget,     // бюджетное (льготное) питание, часть стоимости покрывает дотация
    Commercial  // коммерческое питание, оплата полностью за счёт покупателя
}

/// <summary>Статус транзакции на кассе (поле Transaction_Status отчётной формы).</summary>
public enum TransactionStatus
{
    Success,   // оплата проведена
    Refund,    // возврат (отмена покупки кассиром)
    Declined   // отказ: недостаточно средств / лимит льготы исчерпан
}

/// <summary>
/// Транзакция питания — одна строка ведомости. Поля соответствуют требованиям к отчётности CALS:
/// точная метка времени, идентификатор АРМ и оператора, категория, сумма и статус.
/// </summary>
public sealed record MealTransaction(
    string TransactionId,
    DateTime Timestamp,
    string StationId,
    string OperatorId,
    MealCategory Category,
    string MealType,
    decimal Amount,
    decimal Subsidy,
    TransactionStatus Status,
    int SourceRecordId)
{
    /// <summary>Сумма к оплате покупателем (стоимость минус дотация).</summary>
    public decimal ToPay => Amount - Subsidy;
}

/// <summary>Финансовые итоги по одной категории за сутки.</summary>
public sealed record CategoryTotals(
    MealCategory Category,
    int Success,
    int Refunds,
    int Declined,
    decimal Revenue,
    decimal RefundAmount,
    decimal Subsidy,
    decimal Paid)
{
    /// <summary>Чистая выручка = продажи минус возвраты.</summary>
    public decimal NetRevenue => Revenue - RefundAmount;

    /// <summary>Средний чек по успешным оплатам (без учёта возвращённых).</summary>
    public decimal AverageCheck => Success == 0 ? 0 : Math.Round(NetRevenue / Success, 2);
}

public static class MealCategoryNames
{
    public static string ToRussian(this MealCategory c) => c switch
    {
        MealCategory.Budget => "Бюджет",
        MealCategory.Commercial => "Коммерция",
        _ => c.ToString()
    };

    public static string ToRussian(this TransactionStatus s) => s switch
    {
        TransactionStatus.Success => "Успешно",
        TransactionStatus.Refund => "Возврат",
        TransactionStatus.Declined => "Отклонено",
        _ => s.ToString()
    };
}
