# Правила защиты ветки `main` (Branch Protection Rules)

Цель — ни одно изменение не попадает в `main` (а значит, в релиз v1.6.0) без ревью,
зелёных автотестов и связи с задачей Jira. Прямой деплой «мимо» Pull Request
(Cowboy Coding) запрещён — это требование Change Management и концепции CALS.

## 1. Требования задания и как они настроены

| Требование | Настройка GitHub (Settings → Branches → Add branch protection rule, pattern `main`) |
|---|---|
| Запрет прямого пуша | **Require a pull request before merging** ✅ + **Do not allow bypassing the above settings** ✅ (правило действует и для администраторов) |
| Force Push is disabled | **Allow force pushes** ❌, **Allow deletions** ❌ — история `main` неизменна |
| Обязательное прохождение CI/CD тестов (xUnit) | **Require status checks to pass before merging** ✅ → обязательные проверки **`xunit-tests`** и **`db-migrations`**; **Require branches to be up to date** ✅ |
| Минимум 2 одобрения коллег (Peer Review) | **Required approvals: 3** (2 коллеги + Tech Lead) |
| 1 одобрение Tech Lead | **Require review from Code Owners** ✅ — владелец всех файлов в `CODEOWNERS` — группа `@campus/tech-leads`, без её одобрения кнопка Merge недоступна |
| Одобрение относится к актуальному коду | **Dismiss stale pull request approvals when new commits are pushed** ✅, **Require approval of the most recent reviewable push** ✅ |
| Все замечания ревью закрыты | **Require conversation resolution before merging** ✅ |
| История читается по задачам | **Require linear history** ✅ (squash / rebase merge) |

> Почему 3, а не 2: GitHub считает одобрение Code Owner одним из общего числа.
> При значении 3 и обязательном ревью Code Owner в PR гарантированно есть
> минимум **2 одобрения коллег + 1 одобрение Tech Lead**.

## 2. Формат сообщений коммитов (Ruleset)

Settings → Rules → Rulesets → New branch ruleset → **Restrict commit metadata**,
цель `main`, *Commit message must match regex*: `^\[CAMPUS-\d+\] .+`

Пример: `[CAMPUS-110] Add tracing tags and business logic` — тот же номер задачи,
что в теге трассы `jira.issue`, в Change Request и в CHANGELOG.

## 3. CODEOWNERS (`.github/CODEOWNERS`)

```text
# Tech Lead одобряет любое изменение в main
*                              @campus/tech-leads
# Изменения схемы БД дополнительно смотрит DBA
/pk-16-18/db/migrations/       @campus/tech-leads @campus/dba
/.github/workflows/            @campus/tech-leads @campus/devops
```

## 4. То же самое через GitHub REST API (Infrastructure as Code)

```bash
gh api -X PUT repos/<owner>/<repo>/branches/main/protection \
  -H "Accept: application/vnd.github+json" --input - <<'JSON'
{
  "required_status_checks": { "strict": true, "contexts": ["xunit-tests", "db-migrations"] },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "required_approving_review_count": 3,
    "dismiss_stale_reviews": true,
    "require_code_owner_reviews": true,
    "require_last_push_approval": true
  },
  "restrictions": null,
  "required_linear_history": true,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true
}
JSON
```

## 5. Аналог в GitLab

Settings → Repository → Protected branches: `main` — *Allowed to merge: Maintainers*,
*Allowed to push and merge: No one*, *Allowed to force push: off*.
Settings → Merge requests: *Pipelines must succeed*, *All threads must be resolved*.
Approval rules: правило «Peer review» — 2 одобрения; правило «Tech Lead» — 1 одобрение группы `tech-leads`.
Push rules: commit message regex `^\[CAMPUS-\d+\] .+`.

## 6. Как это связано с CI

Workflow `.github/workflows/pk-16-18-ci.yml` создаёт ровно те проверки, что указаны как обязательные:

* `xunit-tests` — `dotnet build` + `dotnet test --filter Category!=Sanity` (модульные и интеграционные xUnit-тесты);
* `db-migrations` — поднимает PostgreSQL 16 и выполняет `db/test/run_migration_test.sh`
  (Expand дважды, откат, Migrate, отказ Contract при ошибках, Contract, запрет пересечений).
