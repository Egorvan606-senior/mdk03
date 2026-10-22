# Правила защиты ветки `main` (Branch Protection Rules)

Цель — ни одно изменение не попадает в `main` (а значит, в релиз) без ревью,
зелёного CI и связи с задачей Jira. Это часть Change Management: код в `main`
всегда соответствует согласованному CR.

## 1. Настройки GitHub: Settings → Branches → Add branch protection rule

**Branch name pattern:** `main`

| Настройка | Значение | Зачем |
|---|---|---|
| Require a pull request before merging | ✅ | прямые push в `main` запрещены |
| → Required approvals | **2** | одобряют разработчик модуля и ведущий / DBA (для изменений в `db/`) |
| → Dismiss stale pull request approvals when new commits are pushed | ✅ | одобрение относится к конкретной версии кода |
| → Require review from Code Owners | ✅ | изменения `db/migrations/**` обязательно смотрит DBA (файл `CODEOWNERS`) |
| → Require approval of the most recent reviewable push | ✅ | автор не может «досунуть» коммит после одобрения |
| Require status checks to pass before merging | ✅ | |
| → Require branches to be up to date before merging | ✅ | CI проверяет код вместе с актуальным `main` |
| → Обязательные проверки | `build-and-test`, `db-migrations` | сборка + unit-тесты; сквозной прогон миграций Expand/Migrate/Contract на PostgreSQL 16 |
| Require conversation resolution before merging | ✅ | все замечания ревью закрыты |
| Require signed commits | ✅ | подтверждённое авторство (цифровой след) |
| Require linear history | ✅ | только squash / rebase merge — история читается по задачам |
| Do not allow bypassing the above settings | ✅ | правила действуют и для администраторов |
| Allow force pushes | ❌ | история `main` неизменна |
| Allow deletions | ❌ | ветку нельзя удалить |

## 2. Дополнительно: Ruleset на формат сообщений коммитов

Settings → Rules → Rulesets → New branch ruleset → **Restrict commit metadata**:

* Applies to: `main`
* Commit message must match regex: `^\[CAMPUS-\d+\] .+`

Пример корректного сообщения: `[CAMPUS-110] Add tracing tags and business logic`.
Так каждый коммит однозначно связан с задачей — тот же номер, что в теге
трассы `jira.issue`, в CR и в CHANGELOG.

## 3. CODEOWNERS (`.github/CODEOWNERS`)

```text
# Владельцы кода модуля «Спортивный комплекс»
/homework-5/                 @sport-team
/homework-5/db/migrations/   @campus-dba
/.github/workflows/          @campus-devops
```

## 4. То же самое через GitHub REST API (Infrastructure as Code)

```bash
gh api -X PUT repos/<owner>/<repo>/branches/main/protection \
  -H "Accept: application/vnd.github+json" --input - <<'JSON'
{
  "required_status_checks": { "strict": true, "contexts": ["build-and-test", "db-migrations"] },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "required_approving_review_count": 2,
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
Settings → Merge requests: *Pipelines must succeed*, *All threads must be resolved*;
Approval rules: 2 approvals + Code Owner approval; Push rules: commit message regex `^\[CAMPUS-\d+\] .+`.

## 6. Как это связано с CI

Workflow [`.github/workflows/homework-5-ci.yml`](../../.github/workflows/homework-5-ci.yml)
создаёт ровно те проверки, которые указаны как обязательные:

* `build-and-test` — `dotnet build` + `dotnet test --filter Category!=Sanity`;
* `db-migrations` — поднимает PostgreSQL 16 и выполняет `db/test/run_migration_test.sh`
  (Expand дважды, откат, Migrate, отказ Contract при ошибках, Contract, запрет пересечений).
