using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CampusArmMonitor
{
    /// <summary>Запись мок-сервера jsonplaceholder (/posts) — строка выгрузки кассы столовой.</summary>
    public sealed record SourcePost(int UserId, int Id, string Title, string Body);

    /// <summary>Категория льготы на питание и доля стоимости, которую покрывает дотация.</summary>
    public sealed record BenefitCategory(string Code, string Name, decimal SubsidyRate);

    /// <summary>Строка ведомости — итоги по одной категории льгот.</summary>
    public sealed record StatementLine(BenefitCategory Category, int Transactions, decimal Amount, decimal Subsidy)
    {
        public decimal ToPay => Amount - Subsidy;
        public decimal AverageCheck => Transactions == 0 ? 0 : Math.Round(Amount / Transactions, 2);
    }

    /// <summary>
    /// Экспортер ведомости питания: симулирует транзакции по выгрузке,
    /// группирует их по категориям льгот и сохраняет ведомость в CSV.
    /// </summary>
    public static class MealStatement
    {
        // Справочник льгот столовой кампуса (учебные данные)
        public static readonly BenefitCategory Full = new("L100", "Полная льгота", 1.00m);
        public static readonly BenefitCategory LowIncome = new("L050", "Малоимущие семьи", 0.50m);
        public static readonly BenefitCategory Athletes = new("L030", "Спортсмены сборной", 0.30m);
        public static readonly BenefitCategory None = new("L000", "Без льготы", 0.00m);

        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>Разбор JSON-ответа API в список записей.</summary>
        public static List<SourcePost> Parse(string json) =>
            JsonSerializer.Deserialize<List<SourcePost>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("API вернул пустой ответ");

        /// <summary>Категория льготы покупателя: определяется по userId записи (имитация справочника студентов).</summary>
        public static BenefitCategory CategoryOf(SourcePost post) => post.UserId switch
        {
            <= 2 => Full,
            <= 4 => LowIncome,
            5 => Athletes,
            _ => None
        };

        /// <summary>Стоимость покупки: комплекс (обед 185 ₽) + 0–4 доп. позиции по 15 ₽.</summary>
        public static decimal AmountOf(SourcePost post) => 185m + (post.Body?.Length ?? 0) % 5 * 15m;

        /// <summary>Группировка транзакций по категориям льгот (порядок — по убыванию льготы).</summary>
        public static List<StatementLine> Build(IEnumerable<SourcePost> posts) =>
            posts
                .Where(p => p.Id > 0)
                .GroupBy(CategoryOf)
                .Select(g =>
                {
                    decimal amount = g.Sum(AmountOf);
                    decimal subsidy = Math.Round(g.Sum(p => AmountOf(p) * g.Key.SubsidyRate), 2);
                    return new StatementLine(g.Key, g.Count(), amount, subsidy);
                })
                .OrderByDescending(l => l.Category.SubsidyRate)
                .ToList();

        /// <summary>
        /// Сохранение ведомости в CSV (разделитель «;», UTF-8 с BOM — открывается в Excel).
        /// Первая строка — реквизиты отчёта: АРМ, дата, TraceId операции (цифровой след).
        /// </summary>
        public static string SaveCsv(string directory, List<StatementLine> lines, string stationId, string traceId)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"meal_statement_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine($"Ведомость учёта питания по категориям льгот;АРМ {stationId};{DateTime.Now:dd.MM.yyyy HH:mm};TraceId {traceId}");
            sb.AppendLine("Код;Категория льготы;Доля дотации;Транзакций;Сумма;Дотация;К оплате;Средний чек");
            foreach (var l in lines)
            {
                sb.AppendLine(string.Join(';', l.Category.Code, l.Category.Name, l.Category.SubsidyRate.ToString("P0", Ru),
                    l.Transactions, Money(l.Amount), Money(l.Subsidy), Money(l.ToPay), Money(l.AverageCheck)));
            }

            sb.AppendLine(string.Join(';', "", "ИТОГО", "", lines.Sum(l => l.Transactions),
                Money(lines.Sum(l => l.Amount)), Money(lines.Sum(l => l.Subsidy)), Money(lines.Sum(l => l.ToPay)), ""));

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string Money(decimal v) => v.ToString("0.00", Ru);
    }
}
