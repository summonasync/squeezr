# Squeezr 🍋

**Локальный индексатор кода для ИИ-агентов.** Выжимает максимум смысла из твоего кода в минимум токенов. Один .NET-бинарь, всё локально, никаких облаков.

---

## Откуда он взялся

Каждый, кто гонял LLM-агента по реальной репе, знает эту боль: агент начинает «изучать кодbase», читает файлы целиком, жрёт 30–80k токенов на навигацию grep'ом — и это *до того*, как написал первую строчку. Контекст кончается, качество падает, счёт за API растёт.

Мы посмотрели на это и подумали: а почему, собственно, агент должен читать файлы целиком? Люди так не делают. Люди смотрят **карту** проекта, находят **символ**, чекают **ссылки** и раскрывают **точечный спан**. Вот это агенту и надо — просто в машинном виде.

Так родился Squeezr. Имя — от слова *squeeze*: выжимать. Выжимать сок из кода в токен-бюджет. 🍋

## Референсы

Мы не изобретали велосипед — мы собрали лучшее у лучших:

- **[Zoekt](https://github.com/sourcegraph/zoekt)** и легендарная серия статей **[Расса Кокса про code search с триграммами](https://swtch.com/~rsc/regexp/regexp4.html)** — наш триграммный движок написан по этим мотивам. Поиск подстроки и regex по миллионам строк за миллисекунды, без внешних процессов.
- **ripgrep** — эталон того, каким должен быть быстрый и честный поиск. Smart-case в `search` — оттуда.
- **ctags / LSP** — идея символьной навигации, только у нас символы извлекает Roslyn с компиляторной точностью.
- **[Model Context Protocol](https://modelcontextprotocol.io)** — агенты получают индекс как нативные MCP-тулы, а не как «запусти grep и молись».

## Что умеет

- **Триграммный индекс** — собственный движок: бинарный формат, delta+varint, tombstone-сегменты, append-only инкремент, crash-safe хвост. Lookup триграммы — ~0.5 мс на 100k документов.
- **Точный `search`** — литерал, regex (через конъюнкцию обязательных триграмм, как у Кокса), smart-case, фильтры по репо/путям/языку, ранжирование.
- **Символьный слой (C#)** — Roslyn извлекает символы, сигнатуры, doc-comments и refs с точностью компилятора.
- **Символьный слой (JS/TS/TSX)** — tree-sitter извлекает функции, классы, интерфейсы, type-alias'ы и import-граф; прочий текст (Markdown, YAML, JSON…) — fallback-чанкер скользящим окном, тоже участвует в поиске.
- **Токен-бюджет first-class** — каждая команда уважает `--max-tokens`: набирает результаты по рангу, честно говорит `truncated: true`, когда бюджет кончился.
- **MCP-сервер** — агент ходит по картам и сниппетам, а не по простыням: `repo_map` → `code_search` → `find_references` → `expand_span`.
- **Всё локально** — LiteDB + файлы в `.squeezr/`, Qdrant в Docker для векторов, ONNX-эмбеддинги на CPU. Ни одного байта кода не уходит наружу.

## Быстрый старт

Установка (self-contained, .NET не нужен):

```bash
# Linux
scripts/install.sh
```

```powershell
# Windows
pwsh -File scripts\install.ps1
```

Или просто собрать для разработки (нужен .NET 10 SDK): `dotnet build Squeezr.slnx`.

Дальше одинаково на обеих платформах:

```bash
# создать workspace в корне твоего проекта
squeezr init --name my-project
squeezr add /path/to/repo

# проиндексировать и искать
squeezr index          # live-прогресс по фазам; --reembed для полного переэмбеддинга
squeezr search "WorkspaceManager"            # гибрид: точный + семантический (RRF)
squeezr search "w.rkspace" --regex --max-tokens 200
squeezr refs Slugify                          # кто вызывает символ (C#)
squeezr status                                # включая состояние Qdrant
squeezr map --depth 2                         # карта репо: сигнатуры без тел
squeezr symbol WorkspaceManager               # точный lookup символа
squeezr expand src/Foo.cs 42 87               # раскрыть спан из живого файла
squeezr compact                               # компактификация триграммного индекса
squeezr drop                                  # полное удаление индекса workspace
squeezr workspace list                        # состав workspace: репозитории из манифеста
```

Машиночитаемый вывод (`--json`): `status --json` — один JSON-объект (camelCase, включая `staleFiles`/`indexAge`); `search --json` и `workspace list --json` — NDJSON (один объект на строку, флаш на строку; у search финальная строка — `{"type":"summary"}`). Ошибки в JSON-режиме — `{"error":{"code","message"}}` на stderr + ненулевой exit code, коды стабильные (`not_initialized`, `index_not_built`, `invalid_query`, ...). Человекочитаемый вывод при этом не меняется.

Эмбеддинги по умолчанию — локальная ONNX-модель bge-code-v1 (скачать: `scripts/fetch-model.sh` на Linux, `pwsh -File scripts\fetch-model.ps1` на Windows); OpenAI-совместимый endpoint включается секцией `embeddings` в `.squeezr/config.json` (ключ — только через env `SQUEEZR_API_KEY`). Qdrant недоступен — индексация и поиск мягко деградируют на точный слой, ничего не падает.

Установка кладёт единый бинарь: на Linux — в `~/.local/share/squeezr` + symlink `~/.local/bin/squeezr` (снести: `scripts/uninstall.sh`), на Windows — `squeezr.exe` в `%LOCALAPPDATA%\Programs\Squeezr` + user PATH (снести: `pwsh -File scripts\uninstall.ps1`). Оба установщика сами проверят Docker и подскажут, если его нет — он понадобится только с M4.

Переехали со старой Windows-машины? Миграционный чек-лист — [docs/migration-windows-to-linux.md](docs/migration-windows-to-linux.md).

## TUI: человеческий интерфейс

Не любишь помнить флаги — просто запусти `squeezr tui` (или голый `squeezr` в интерактивном терминале: без аргументов он открывает TUI). Если терминал неинтерактивный (pipe, CI) — бинарь честно падает в справку вместо меню.

Меню — семь пунктов, круг полностью покрывает жизненный цикл workspace:

- **Dashboard** — сводка: счётчики файлов/символов/чанков по репо, состояние Qdrant, pending-эмбеддинги, возраст индекса.
- **Wizards** — `init` / `add` / `drop` с формами вместо флагов.
- **Index** — инкремент, полный реиндекс или `--reembed` с live-прогрессом по фазам.
- **Search** — вводишь запрос, получаешь хиты с флагами `[exact]`/`[semantic]`/`[both]` (repo, file:line, сниппет — без тел). Выбор хита раскрывает спан с контекстом из живого файла; «More context (×2)» раздувает контекст, пока хватает бюджета. Qdrant лежит — видишь warning и exact-only выдачу.
- **Map** — браузер карты: репо → директории → файл → символы с сигнатурами (тел нет). Из символа — тот же expand его спана.
- **Config** — формы поверх `.squeezr/config.json`: qdrant, embeddings (onnx/openai), chunking, indexing. Валидация на вводе (URL, int > 0, provider), diff-сводка «было → станет» перед сохранением, после смены embeddings — напоминание про `index --reembed`.

Всё то же самое через CLI-флаги остаётся доступным — TUI это надстройка, не замена.

## MCP-сервер: агент ходит по индексу, а не по файлам

`squeezr serve` поднимает MCP-сервер (stdio, JSON-RPC на stdout). Агент получает индекс как шесть тулов — карту, поиск, символы, ссылки и раскрытие спанов — и не читает файлы целиком вообще.

### Подключение: `squeezr mcp install`

Руками в конфиги лезть не нужно — одна команда зарегистрирует сервер у клиента:

```bash
squeezr mcp install                          # интерактив: детект клиентов → мультивыбор → scope → инструкции
squeezr mcp install --client claude --scope project
squeezr mcp install --client all --scope user --yes   # все найденные клиенты, без вопросов (для скриптов)
```

| Флаг | Что делает |
|---|---|
| `--client claude\|kimi\|qwen\|codex\|all` | Кого подключать; без флага squeezr сам найдёт установленных клиентов и предложит мультивыбор; `all` — все найденные |
| `--scope user\|project` | `user` — сервер доступен во всех проектах, `project` — только в этом workspace; без флага спросит |
| `--no-watch` | Не добавлять `--watch` к `serve` (по умолчанию индекс сам догоняет правки на диске) |
| `--no-instructions` | Не писать агенту блок навигационных инструкций |
| `--yes` | Согласиться на инструкции без вопроса (для неинтерактивных прогонов) |
| `--json` | Машиночитаемый ответ вместо таблички |

Что происходит под капотом: запись регистрируется как `serve --watch`, конфиг пишет родной CLI клиента (`claude mcp add`, `qwen mcp add`, `codex mcp add`), а где CLI нет (Kimi Code) — squeezr сам мержит JSON в конфиг-файл. Чужие серверы в конфиге не трогаются; повторный install обновляет запись при прямой записи (Kimi), а по CLI-пути клиент ответит «already exists» — это нормально, запись уже на месте. Отдельно спросит (дефолт — «да»), дописать ли агенту маркер-блок с флоу навигации: идемпотентно, в инструкционный файл клиента (Claude — CLAUDE.md, Kimi и Codex — AGENTS.md, Qwen — QWEN.md), `mcp remove` убирает его за собой.

`squeezr mcp doctor` — чекап подключения: клиент найден, запись есть (user/project), бинарь в PATH, workspace проиндексирован, headless-статус через CLI клиента. Exit code 0/1 — можно вешать на скрипты и CI. `squeezr mcp remove [--client ...] [--scope ...]` удаляет запись и блок инструкций; обе команды уважают `--client` и `--json`.

Корень workspace сервер разрешает от cwd процесса вверх (запускай клиент из корня проекта); Claude Code не умеет cwd в конфиге сервера — поэтому `serve` дополнительно уважает `CLAUDE_PROJECT_DIR`, который Claude выставляет своим MCP-процессам. Перед подключением нужны `squeezr init` / `add` / `index` — сервер читает готовый индекс и сам его не строит. Qdrant опционален: без него семантический слой молча отключается, `code_search` работает exact-only (warning в ответе).

### Ручная настройка (справка)

Если хочешь прописать конфиг сам — куда писать запись `squeezr`:

| Клиент | user-scope | project-scope |
|---|---|---|
| Claude Code | `~/.claude.json` | `.mcp.json` |
| Kimi Code | `~/.kimi-code/mcp.json` (или `$KIMI_CODE_HOME/mcp.json`) | `.kimi-code/mcp.json` |
| Qwen Code | `~/.qwen/settings.json` | `.qwen/settings.json` |
| Codex CLI | `~/.codex/config.toml` | `.codex/config.toml` (только trusted-проекты) |

JSON-клиентам (Claude, Kimi, Qwen) — блок `mcpServers`:

```json
{
  "mcpServers": {
    "squeezr": {
      "command": "squeezr",
      "args": ["serve", "--watch"]
    }
  }
}
```

Codex — TOML-секция `[mcp_servers.squeezr]` с теми же `command`/`args` (плюс `startup_timeout_sec = 30`, чтобы индексатор успел стартовать). Заодно учти: Kimi и Qwen любят `cwd` в записи, Claude — нет (см. CLAUDE_PROJECT_DIR выше).

### Тулы

| Тул | Параметры (дефолты) | Возвращает |
|---|---|---|
| `repo_map` | `repo?`, `depth?` (2), `maxTokens?` (4000) | дерево репо: директории → файлы → члены с сигнатурами. Тел нет, только карта для навигации |
| `code_search` | `query` (обяз.), `repo?`, `topK?`, `maxTokens?` (2000) | гибридные совпадения `{repo, file, line, score, source, snippet}`; source = `exact`/`semantic`/`both`; тела не возвращаются |
| `symbol_lookup` | `name` (обяз.), `kind?`, `repo?`, `maxTokens?` (300) | определения символа: сигнатура, doc-comment, `span`, тело — если влезло в бюджет. Символ не найден → пустой `data`, не ошибка |
| `find_references` | `symbol` (обяз.), `repo?` | все точки вызова `{file, line, kind, enclosingSymbol}` — enclosingSymbol захватывается на индексации Roslyn'ом (точно, включая partial-классы); для индексов, построенных до захвата, значение выводится по спанам символов (fallback-инференс). Символ не найден → tool error (как `squeezr refs`, exit 1) |
| `expand_span` | `file`, `startLine` (обяз.), `endLine?`, `contextLines?` (3), `maxTokens?` (1000) | строки спана + контекст из живого файла на диске (видит правки после индексации). Единственный тул, отдающий код построчно |
| `index_status` | — | свежесть: возраст индекса, per-repo счётчики файлов/символов/чанков, pending-эмбеддинги, доступность Qdrant |

Все тулы имеют дефолтный токен-бюджет (см. таблицу); при `truncated: true` сузь запрос или подними `maxTokens` явно.

### Флоу навигации

Двухуровневая выдача: тулы дают координаты, код раскрывается только через `expand_span`.

1. `repo_map` — незнакомый репо: структура и сигнатуры, ноль тел.
2. `code_search` (по тексту) или `symbol_lookup` (по имени) — точки интереса: file:line, сниппеты, сигнатуры.
3. `find_references` — кто вызывает найденный символ.
4. `expand_span` — раскрыть спан с контекстом, когда нужен сам код.

### Семантика ответа

Каждый тул отвечает единым конвертом:

```json
{ "data": ..., "tokensUsed": 42, "truncated": false, "warnings": [], "indexAge": "00:12:03", "staleFiles": [] }
```

- `tokensUsed` / `truncated` — фактическая стоимость ответа. `truncated: true` → сузь запрос (`repo`, `kind`) или подними `maxTokens`.
- `warnings` — мягкая деградация (например, «vector store unavailable, falling back to exact search only») и подсказки при обрезке.
- `indexAge` / `staleFiles` — свежесть. Изменённые файлы освежаются inline при чтении; то, что освежить не удалось, честно попадает в `staleFiles` — агент видит, что данные по этим файлам протухли, и может предложить `squeezr index`. Окончательное решение о реиндексе — за хозяином workspace.

## Watch-режим: индекс догоняет рабочее дерево сам

Индекс по требованию — это компромисс: правишь файл, а `code_search` ещё отвечает про старую версию. FreshnessGuard освежает точечно при чтении, но полный догоняющий проход запускаешь руками. Watch-режим убирает и это.

Две формы, один движок:

```bash
squeezr watch            # человек: демон в терминале, Ctrl+C — чистый выход
squeezr serve --watch    # агент: тот же движок внутри MCP-сервера
```

Как работает: `FileSystemWatcher` на корнях репо → фильтрация (те же ignore-глобы и расширения, что у индексации; записи в `.squeezr/` не считаются) → изменённые пути копятся в changed-сет → после окна тишины один инкрементальный проход по всему накопленному. «Save-all из 50 файлов» = один проход. Индекс занят (ручной `index`, TUI, второй serve) — движок ждёт с экспоненциальным backoff и никогда не отбирает лок. Живой статус: в терминале — Live-грид (workspace / status / last pass / error), в pipe — строки `pass started: N changed paths` / `pass completed: +A ~M -D` в stdout.

Удаления — полный жизненный цикл (tombstone триграмм, чистка документов/чанков, drop Qdrant-точек файла): после `git checkout`/`git clean` выдача не содержит призраков. `serve --watch` не блокирует JSON-RPC — тулы отвечают и во время фонового прохода, конверт не меняется. FreshnessGuard остаётся страховкой: пока watch не догнал, конверт честно показывает `staleFiles`.

Конфиг — секция `watch` в `.squeezr/config.json`:

```json
{
  "watch": {
    "enabled": false,
    "debounceSeconds": 2,
    "backoffMaxSeconds": 30
  }
}
```

`enabled` включает движок у `serve` без флага (флаг главнее конфига); явный `squeezr watch` — всегда намерение пользователя, конфиг не консультируется. Отсутствие секции или невалидные значения = дефолты.

## Roadmap

- [x] **M1** — каркас CLI, LiteDB-схемы, конфиг, workspace
- [x] **M2** — триграммный индекс, точный `search`, инкремент, `compact`
- [x] **M3** — Roslyn: символы, чанки, `symbol`, `map`, `expand`
- [x] **M4** — Qdrant + ONNX-эмбеддинги: гибридный `search`, `refs`
- [x] **M5** — MCP `serve` со всеми тулами + README с agent-инструкциями
- [x] **M6** — tree-sitter для JS/TS, fallback-чанкер

## Стек

.NET 10 / C# 14 · Spectre.Console.Cli · LiteDB · Roslyn · tree-sitter (JS/TS) · Qdrant · ONNX Runtime · xUnit + Shouldly

---

## English (short)

**Squeezr is a local code indexer for AI agents.** It squeezes maximum signal from your codebase into a minimum of LLM tokens — a single .NET binary, fully offline.

Born from a simple observation: agents waste 30–80k tokens navigating code by reading whole files. Squeezr gives them what senior devs actually use — a repo **map**, **symbol** lookup, **references**, and precise **span expansion** — as fast local queries and MCP tools.

The trigram engine is built after [Zoekt](https://github.com/sourcegraph/zoekt) and [Russ Cox's code-search design](https://swtch.com/~rsc/regexp/regexp4.html): substring and regex search over millions of lines in milliseconds, incremental and crash-safe. C# symbols come from Roslyn with compiler-grade accuracy. Every response respects a token budget (`--max-tokens`, honest `truncated` flag). Storage: LiteDB + Qdrant (Docker) + ONNX embeddings — nothing leaves your machine.

An MCP server (`squeezr serve`) exposes the index to agents as six tools — `repo_map`, `code_search`, `symbol_lookup`, `find_references`, `expand_span`, `index_status` — with a uniform envelope (`tokensUsed`, `truncated`, `warnings`, `indexAge`, `staleFiles`). Client onboarding is one command: `squeezr mcp install` detects supported agent CLIs (Claude Code, Kimi Code, Qwen Code, Codex), registers a `serve --watch` entry via the client's own CLI (direct config merge for Kimi), and optionally writes an idempotent instruction block into the client's instruction file; `squeezr mcp doctor` verifies the setup per client (exit 0/1), `squeezr mcp remove` undoes it. Flags: `--client claude|kimi|qwen|codex|all`, `--scope user|project`, `--no-watch`, `--no-instructions`, `--yes`, `--json`. Workspace root = server process cwd, with a `CLAUDE_PROJECT_DIR` fallback for Claude Code (no cwd key in its config); manual config paths (e.g. `~/.claude.json` / `.mcp.json`, `~/.codex/config.toml`) are listed in the RU section above. Watch mode (`squeezr watch`, `squeezr serve --watch`) keeps the index fresh automatically: debounced catch-up passes with lock backoff, full delete/rename lifecycle, no ghost hits.

```bash
dotnet build Squeezr.slnx
squeezr init --name my-project && squeezr add /path/to/repo
squeezr index && squeezr search "WorkspaceManager"
```

Linux install (self-contained binary + `~/.local/bin` symlink, no .NET required): `scripts/install.sh`. Uninstall: `scripts/uninstall.sh`.

Windows install (self-contained exe + PATH, no .NET required): `pwsh -File scripts\install.ps1`. Uninstall: `pwsh -File scripts\uninstall.ps1`.

Status: M1–M6 done (CLI skeleton, trigram index, Roslyn symbols, hybrid semantic search + refs, MCP server with all six tools, tree-sitter JS/TS + fallback chunker). See the roadmap above.
