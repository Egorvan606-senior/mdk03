# Changelog

Все значимые изменения клиента АРМ оператора (Campus Operator) и утилиты
campus-healthcheck фиксируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
версии соответствуют [Semantic Versioning](https://semver.org/lang/ru/).

## [Unreleased]

### Changed
- Переход на канал Beta для кольца 1 (Canary) — после согласования CR.

## [2.4.0] - 2026-10-23

### Added
- Утилита `campus-healthcheck` 1.0: цепочка проверок после обновления (конфигурация,
  версия, локальная SQLite, центральная PostgreSQL, внешний API, считыватель, диск)
  с замером задержки и повторными попытками.
- Акт готовности АРМ к промышленной эксплуатации в форматах Markdown и JSON.
- Команда `update`: проверка SHA-256 пакета, резервная копия, контроль целостности
  локальных данных и автоматический откат при критичном сбое.
- Команда `manifest` для подготовки `manifest.json` пакета обновления.
- Новые ключи конфигурации `Telemetry:*`, `Ui:*`, `Api:TimeoutSeconds`,
  `LocalDb:SyncIntervalSeconds` (переносятся в `appsettings.json` без затирания значений).
- Эксплуатационная документация: OPERATOR_MANUAL.md, UPDATE_GUIDE.md, регламент обновления.

### Changed
- Обновление раскатывается кольцами (Canary 5 % → 25 % → 100 %) вместо одновременной установки.

### Fixed
- Обновление больше не перезаписывает `appsettings.json` — адреса и параметры
  считывателя, настроенные на АРМ, сохраняются.
- Установка прерывается до изменений на АРМ, если пакет повреждён при копировании.

## [2.3.1] - 2026-09-14

### Fixed
- Повторная синхронизация журнала проходов после потери связи.

[Unreleased]: https://github.com/Egorvan606-senior/mdk03/compare/v2.4.0...HEAD
[2.4.0]: https://github.com/Egorvan606-senior/mdk03/compare/v2.3.1...v2.4.0
[2.3.1]: https://github.com/Egorvan606-senior/mdk03/releases/tag/v2.3.1
