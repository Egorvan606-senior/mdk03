# ПР № 7 · Сценарий внедрения ПО на АРМ · Генератор ведомости питания (вариант 10)

| Документ | Файл |
|---|---|
| Сценарий развертывания, чек-лист аудита АРМ, правила передачи ключей | [docs/DEPLOYMENT_SCENARIO.md](docs/DEPLOYMENT_SCENARIO.md) |
| Руководство оператора | [OPERATOR_MANUAL.md](OPERATOR_MANUAL.md) |
| Отчётные формы и журналы | [docs/REPORT_FORMS.md](docs/REPORT_FORMS.md) |
| Утилита C# (.NET 8) | [src/Campus.MealStatement](src/Campus.MealStatement) |

## Запуск

```bat
cd src\Campus.MealStatement
dotnet run -- --month 2026-10
```

Результат — папка `statements`: финансовый лог, ведомость по категориям, сводная по студентам
(CSV для Excel) и контрольные суммы SHA-256. Источник по умолчанию —
`https://jsonplaceholder.typicode.com/posts` (`mealstatement.json`), при его недоступности —
кэш последней выгрузки.
