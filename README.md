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
- **Всё локально** — LiteDB + файлы в центральном сторе `~/.squeezr/`, Qdrant и Ollama в Docker под управлением `squeezr services`, эмбеддинги: по умолчанию `auto` — ollama на GPU, если endpoint отвечает, иначе локальный ONNX на CPU; явно — ollama/OpenAI-compatible по конфигу. Ни одного байта кода не уходит наружу.

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
# создать workspace в корне твоего проекта (в проекте НОЛЬ файлов squeezr —
# манифест, конфиг и индекс живут в центральном сторе ~/.squeezr/)
squeezr init --name my-project
squeezr add /path/to/repo

# поднять инфраструктуру (Qdrant + Ollama в Docker, см. раздел про services)
squeezr services up

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
squeezr workspace list                        # реестр воркспейсов из центрального стора
```

Машиночитаемый вывод (`--json`): `status --json` — один JSON-объект (camelCase, включая `staleFiles`/`indexAge`); `search --json` и `workspace list --json` — NDJSON (один объект на строку, флаш на строку; у search финальная строка — `{"type":"summary"}`). Ошибки в JSON-режиме — `{"error":{"code","message"}}` на stderr + ненулевой exit code, коды стабильные (`workspace_not_initialized`, `index_not_built`, `invalid_query`, ...). Человекочитаемый вывод при этом не меняется.

Прогресс-ивенты (`--events`): `squeezr index --events` и `squeezr serve --watch --events` стримят NDJSON-ивенты индексации — по одному на строку, флаш на строку: `started` (workspace; `provider?` — разрешённый провайдер эмбеддингов, только на `index`-прогонах с семантикой; у watch-проходов ещё `changedPaths`) → `fileIndexed` (`{repo, path, files}` — кумулятивный счётчик за прогон) → `progress` (`{repo, phase, total}` для фаз semantic/embeddings/trigram) → `completed` (`durationMs`, итоги `files/added/modified/deleted`, `symbols?`, `provider?`, разбивка по репо) или терминальный `error` (`code`, `message`). Транспорт: у `index` — stdout, у `serve --watch` — stderr (stdout занят MCP JSON-RPC). Тихий инкрементный прогон даёт ровно `started → fileIndexed×N → completed`. Композиция с `--json`: сначала завершённая последовательность ивентов, затем финальный `--json`-объект; ошибки дублируются энвелопом 035 на stderr + exit 1. Занятый реестр воркспейсов (конкурентная запись в `workspaces.json`) валит прогон до старта: стрим открывается терминальным `error` без `started`, код `workspace_registry_busy` — сигнал «повторить через момент» для скриптов; тот же код приходит в `--json`/stderr-энвелопах всех команд, трогающих реестр.

Эмбеддинги — провайдер из `auto`/`onnx`/`ollama`/`openai`, выбирается секцией `embeddings` конфига воркспейса (лежит в сторе: `~/.squeezr/workspaces/<storeKey>/config.json`, правится удобнее через `squeezr tui` → Config):

| Провайдер | Когда выбирать | Конфиг |
|---|---|---|
| `auto` (дефолт) | GPU без настройки: на каждом прогоне индексации ollama отвечает на пробе — считаем на GPU через него, не отвечает — локальный onnx на CPU | `provider: "auto"` (пишется `init`'ом по умолчанию) |
| `onnx` | Локально на CPU, ничего не поднимать | Локальная ONNX-модель **nomic-embed-text-v1.5** (официальный int8-экспорт, ~131 МБ, 768d; WordPiece, mean pooling + L2, контекст модели 8192, squeezr режет до 512 — `maxSequenceLength`; префиксы `search_document:`/`search_query:` добавляет сам squeezr) |
| `ollama` | Заметно быстрее на больших репо; на AMD-GPU считает через ROCm (см. раздел про services) | `embeddings.provider: "ollama"` + секция `embeddings.ollama`: endpoint `http://localhost:11434`, модель `nomic-embed-text` (768d, профиль `nomic-embed-text-v1.5` — префикс-симметрия с onnx по семантике (одинаковые `search_document: `/`search_query: ` на стороне squeezr; сами вектора onnx≠ollama, поэтому смена провайдера переэмбеддит чанки). Нативный `/api/embed`, API-ключа нет. Модель подтянется сама при первой индексации (auto-pull); ollama не запущен — явная ошибка с подсказкой `squeezr services up` |
| `openai` | OpenAI-совместимый endpoint (OpenAI, Azure, vLLM) | Секция `embeddings.openAiCompatible` + ключ только через env `SQUEEZR_API_KEY` |

Скачать ONNX-модель: `scripts/fetch-model.sh` на Linux, `pwsh -File scripts\fetch-model.ps1` на Windows. Порядок резолва модели: явный путь в конфиге → `~/.squeezr/models/` → bundled-копия рядом с бинарём. Обратно на bge-code-v1 (1536d, кодовая специализация): `scripts/fetch-model.sh --model bge-code-v1` (в pwsh: `-Model bge-code-v1`) + `"model": "bge-code-v1"` в секции `embeddings.onnx`; на существующем workspace смена модели честно ругнётся «collection vector size 768 ≠ 1536» — лечится `squeezr index --reembed`, молча перетереть коллекцию не выйдет. Смена провайдера/модели меняет ключ чанков — затронутые чанки переэмбеддятся автоматически. Qdrant недоступен — индексация и поиск мягко деградируют на точный слой, ничего не падает.

## For AI agents

An AI agent told to "use squeezr here" gets productive in one conversation. Run this only on an explicit human request — never install squeezr proactively. The full ritual with guardrails, tool cheat-sheet and troubleshooting lives in [`skills/squeezr/SKILL.md`](skills/squeezr/SKILL.md); short version:

```bash
curl -fsSL http://REDACTED-LAN-HOST:3000/REDACTED-FORGE-NS/Squeezr/raw/main/scripts/install-release.sh | bash
squeezr services up
squeezr init && squeezr add . && squeezr index --events
squeezr mcp install --all --scope user --yes
```

1. The installer resolves the latest published Gitea release, unpacks it into `~/apps/squeezr/<tag>/` and repoints the `~/.local/bin/squeezr` symlink (if that dir is not on PATH, the script says so — fix the shell profile and open a new terminal). Pin or roll back with `--version <tag>`.
2. Starts the managed Qdrant + Ollama stack; the first run pulls ~3.6 GB of images. If docker is missing, stop and ask the human — do not improvise.
3. Creates a workspace in the current directory and indexes everything (zero files land in the project).
4. Registers the MCP server in every detected client at user scope, non-interactive. Restart the agent session afterwards — MCP clients read configs at startup.

From then on navigate via the six tools — `repo_map`, `code_search`, `symbol_lookup`, `find_references`, `expand_span`, `index_status` — instead of reading whole files.

The skill content lives in this repo at `skills/squeezr/` and imports into Overmind as the kit «squeezr» (phase 1: manual kit import through the Overmind UI), so agent teams can onboard without reading this README.

## Центральный стор воркспейсов

Squeezr не мусорит в проекте: `squeezr init` не создаёт в текущей папке ни одного файла — ни `.squeezr/`, ни манифеста, ни compose. Всё живёт в центральном сторе:

```
~/.squeezr/                          # стор (override: env SQUEEZR_HOME)
├── workspaces.json               # реестр: name/rootPath/storeKey/createdAt/lastUsedAt
├── workspaces/<storeKey>/          # index store одного workspace
│   ├── workspace.json               # манифест (репозитории)
│   ├── config.json                  # конфиг (embeddings, watch, ...)
│   ├── meta.db                      # LiteDB
│   ├── trigram.idx                  # триграммный индекс
│   ├── index.lock
│   ├── watch.heartbeat              # пока жив демон `watch` (флот, см. раздел про fleet)
│   ├── serve-watch.heartbeat        # ...или `serve --watch` от MCP-клиента
│   └── queries.jsonl                # телеметрия запросов для `stats` (локально, drop сносит)
├── models/                         # ONNX-модели эмбеддингов
└── infra/docker-compose.yml        # qdrant + ollama (генерируется `squeezr services up`)
```

- **Discovery как у git:** любая команда, запущенная из любого сабдиректория воркспейса, находит его сама (поиск вверх по реестру). «Зашёл не в ту папку» исчезло как класс.
- **Реестр:** `squeezr workspace list` показывает все воркспейсы из `workspaces.json`; записи, чей rootPath исчез с диска, помечаются `stale` и удаляются через `squeezr drop --store-key <key>`.
- **Легаси-миграция автоматическая:** проекты со старым layout (`.squeezr/` + `squeezr.workspace.json` в корне) переезжают в стор при первом же обращении — переносится содержимое `.squeezr/` и манифест, squeezr-сгенерированный `docker-compose.yml` и мёртвый `state.json` удаляются. Детали — [docs/migration-central-store.md](docs/migration-central-store.md). Qdrant-коллекции (имя зависит от пути воркспейса, а не стора) переживают миграцию, вектора не теряются.

## Managed services: `squeezr services`

Docker-инфраструктура — qdrant и ollama — управляется одной командой, руками compose не трогаешь:

```bash
squeezr services up       # сгенерировать compose и поднять оба сервиса
squeezr services status   # таблица контейнеров (+ --json, rocm, версия compose)
squeezr services down     # остановить (данные в volume'ах НЕ удаляются)
```

- Compose-файл — `~/.squeezr/infra/docker-compose.yml`, перегенерируется на каждый `up` (первой строкой — маркер `# managed by squeezr`); project `squeezr`.
- Сервисы: **qdrant** (`qdrant/qdrant:latest`, 6333/6334, volume `squeezr-qdrant`) и **ollama** (`ollama/ollama:latest`, 11434, volume `squeezr-ollama`), restart `unless-stopped`.
- **AMD GPU (ROCm):** если на хосте видны `/dev/kfd` и `/dev/dri` (например, Radeon RX 7900 XTX), ollama получает проброс этих устройств и считает на GPU; без них — CPU-режим.
- Команда глобальна: работает из любой директории, workspace не нужен. Без Docker — понятная ошибка со ссылкой на установку.

**Первый запуск — будь готов к загрузке.** Первый `services up` тянет образы из registry: qdrant ~70 МБ, ollama ~3.6 ГБ — на средней сети это минуты, на медленной — десятки минут. Команда показывает этапы (`Pulling images…`, `Starting containers…`; сырой docker-вывод — под `--verbose`). Таймаута у загрузки нет — если она застынет или надоест, прерви Ctrl+C: процесс compose будет остановлен, выход чистый. Скачанные слои кэшируются — докачай с видимым прогрессом и повтори:

```bash
docker compose -f ~/.squeezr/infra/docker-compose.yml -p squeezr pull   # resume, слои докачаются
squeezr services up                                                     # повторный up
```

Повторные `up` ходят в registry только за отсутствующими образами (`pull --policy missing`, compose v2.24+; на v2.21–2.23 — полный pull) — офлайн-перезапуск с уже скачанными образами работает. Требования: Docker Engine + compose plugin v2.21+ (`status` использует `ps --format json`); поддерживается и standalone `docker-compose`. `down` останавливает контейнеры, но **никогда** не трогает volume'ы — данные qdrant/ollama переживают циклы down/up.

Установка кладёт единый бинарь: на Linux — в `~/.local/share/squeezr` + symlink `~/.local/bin/squeezr` (снести: `scripts/uninstall.sh`), на Windows — `squeezr.exe` в `%LOCALAPPDATA%\Programs\Squeezr` + user PATH (снести: `pwsh -File scripts\uninstall.ps1`). Оба установщика сами проверят Docker и подскажут, если его нет — он понадобится для `squeezr services up` (Qdrant и, опционально, Ollama). Важно: ни установка, ни удаление не трогают стор `~/.squeezr` — воркспейсы и модели переживают переустановку бинаря.

Переехали со старой Windows-машины? Миграционный чек-лист — [docs/migration-windows-to-linux.md](docs/migration-windows-to-linux.md). Проект ещё на старом layout с `.squeezr/` в корне? Миграция в центральный стор происходит сама — [docs/migration-central-store.md](docs/migration-central-store.md).

## TUI: человеческий интерфейс

Не любишь помнить флаги — просто запусти `squeezr tui` (или голый `squeezr` в интерактивном терминале: без аргументов он открывает TUI). Если терминал неинтерактивный (pipe, CI) — бинарь честно падает в справку вместо меню.

Меню контекстное — зависит от состояния workspace:

- **Папка без workspace** (воркспейс не найден — squeezr ищет его вверх по дереву от текущей папки) — сразу предложение «No workspace found here. Initialize now?» (дефолт — да) → форма `init`. Отказ — не тупик: остаётся меню из двух пунктов — Initialize workspace / Exit, можно передумать без рестарта.
- **Workspace инициализирован** — полный круг: Dashboard / Search / Map / Index / Add repository / Config / Drop workspace / Exit.

Пункты верхнего уровня:

- **Dashboard** — сводка: счётчики файлов/символов/чанков по репо, состояние обоих сервисов (qdrant — points/pending, ollama — running + модель провайдера установлена/нет), pending-эмбеддинги, возраст индекса. Сервис лежит — под статусом явный хинт `qdrant is down — start it with: squeezr services up` (для ollama — аналогично). Репозиториев ещё нет — в конце предложение «No repositories yet. Add one now?» (дефолт — да) → форма Add repository.
- **Search** — вводишь запрос, получаешь хиты с флагами `[exact]`/`[semantic]`/`[both]` (repo, file:line, сниппет — без тел). Выбор хита раскрывает спан с контекстом из живого файла; «More context (×2)» раздувает контекст, пока хватает бюджета. Qdrant лежит — видишь warning и exact-only выдачу.
- **Map** — браузер карты: репо → директории → файл → символы с сигнатурами (тел нет). Из символа — тот же expand его спана.
- **Index** — инкремент, полный реиндекс или `--reembed` с live-прогрессом по фазам.
- **Add repository** — форма `add` вместо флагов.
- **Config** — формы поверх конфига воркспейса (в сторе: `~/.squeezr/workspaces/<storeKey>/config.json`): qdrant, embeddings (onnx/ollama/openai), chunking, indexing. Валидация на вводе (URL, int > 0, provider), diff-сводка «было → станет» перед сохранением, после смены embeddings — напоминание про `index --reembed`.
- **Drop workspace** — подтверждение и полное удаление индекса workspace (`drop`).

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

Codex — TOML-секция `[mcp_servers.squeezr]` с теми же `command`/`args` (плюс `startup_timeout_sec = 30`, чтобы индексатор успел стартовать). `cwd` в запись писать не нужно — корень workspace `serve` разрешает сам от cwd процесса (и `CLAUDE_PROJECT_DIR`, см. выше).

### Advisory hook: `squeezr hook pre-read`

Хук для Claude Code (PreToolUse на Read): squeezr читает payload хука из stdin и, если файл лежит в зарегистрированном workspace и выглядит как массовое чтение — на диске ≥ 256 KB, либо запрошен широкий диапазон (limit ≥ 400 строк) — добавляет в контекст агента мягкий nudge: «workspace проиндексирован — рассмотри code_search / symbol_lookup + expand_span вместо чтения целиком». Явно узкий limit подавляет nudge: агент уже читает прицельный спан.

Остальное — тишина: не-workspace путь, маленький файл, отсутствующий файл, кривой stdin → пустой stdout, exit 0. Хук advisory-only — `permissionDecision` не выставляет и никогда не блокирует Read; на внутренние ошибки реагирует fail-open (молча, код 0). Сети нет — только реестр workspaces и stat файла, поэтому дёшево бегает на каждый Read.

Сниппет применяется руками (политика crew: агентам писать в hook-конфиги запрещено). В `~/.claude/settings.json` (user scope; для одного проекта — `.claude/settings.json`):

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Read",
        "hooks": [
          {
            "type": "command",
            "command": "squeezr hook pre-read"
          }
        ]
      }
    ]
  }
}
```

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

Повторные запросы почти бесплатны: дорогие тулы (`repo_map`, `code_search`, `symbol_lookup`, `find_references`) кэшируют ответы внутри serve-сессии — LRU на 100 записей, ключ по каноническому JSON параметров (порядок свойств и null-и-absent на ключ не влияют). Идентичный запрос в той же сессии = мгновенный повтор сохранённого конверта, телеметрия помечает его `cacheHit: true` (и он виден в hit-rate у `squeezr stats`). Любая переиндексация — watch-проход или внешний `squeezr index` — меняет поколение кэша (mtime meta.db), все ключи выбиваются, агент получает свежие данные. `expand_span` не кэшируется (читает живой файл с диска), `index_status` дешёвый сам по себе.

## Watch-режим: индекс догоняет рабочее дерево сам

Индекс по требованию — это компромисс: правишь файл, а `code_search` ещё отвечает про старую версию. FreshnessGuard освежает точечно при чтении, но полный догоняющий проход запускаешь руками. Watch-режим убирает и это.

Две формы, один движок:

```bash
squeezr watch            # человек: демон в терминале, Ctrl+C — чистый выход
squeezr serve --watch    # агент: тот же движок внутри MCP-сервера
```

Как работает: `FileSystemWatcher` на корнях репо → фильтрация (те же ignore-глобы и расширения, что у индексации; служебные файлы стора не считаются — они живут вне проекта) → изменённые пути копятся в changed-сет → после окна тишины один инкрементальный проход по всему накопленному. «Save-all из 50 файлов» = один проход. Индекс занят (ручной `index`, TUI, второй serve) — движок ждёт с экспоненциальным backoff и никогда не отбирает лок. Живой статус: в терминале — Live-грид (workspace / status / last pass / error), в pipe — строки `pass started: N changed paths` / `pass completed: +A ~M -D` в stdout. Для программного потребления — `serve --watch --events`: NDJSON-ивенты проходов (`started` с `changedPaths` → `fileIndexed` → `completed`) на stderr, JSON-RPC на stdout не тронут (схема — в разделе про `--json` выше).

Удаления — полный жизненный цикл (tombstone триграмм, чистка документов/чанков, drop Qdrant-точек файла): после `git checkout`/`git clean` выдача не содержит призраков. `serve --watch` не блокирует JSON-RPC — тулы отвечают и во время фонового прохода, конверт не меняется. FreshnessGuard остаётся страховкой: пока watch не догнал, конверт честно показывает `staleFiles`.

Конфиг — секция `watch` в конфиге воркспейса (`~/.squeezr/workspaces/<storeKey>/config.json`):

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

## Fleet: все демоны разом

Watch-демоны рассыпаны по воркспейсам, и помнить, где что крутится, — работа для машины. `squeezr fleet` показывает каждый воркспейс реестра и состояние демонов на нём (работает из любой директории, сам воркспейс не нужен):

```bash
squeezr fleet         # таблица: демоны, pending, свежесть индекса, stale
squeezr fleet --json  # NDJSON, по одному объекту на воркспейс (контракт 035)
```

```
Name      Root path            Store key  WATCH                 SERVE-WATCH    Pending  Last indexed      Status
squeezr   /home/dev/squeezr    squeezr    pid 4217, up 2h 14m   —              0        2026-09-16 10:31  ok
old-proj  /home/dev/old-proj   old-proj   dead (pid 9021)       —              n/a      n/a               stale
```

Колонки `WATCH` / `SERVE-WATCH` — демоны двух kind: `squeezr watch` (терминал) и `serve --watch` (MCP-клиент); живой показывает `pid N, up …`, мёртвый — `dead (pid N)` (файл heartbeat остался, а пид/бит протух), нет файла — `—`. `Pending` — необработанные чанки, `Last indexed` — время последнего полного прохода; оба best-effort из `meta.db` воркспейса (`n/a`, если БД недоступна — fleet не падает). Записи с исчезнувшим rootPath помечаются `stale` и дают warning в stderr с хинтом на `squeezr drop --store-key`.

Как демон объявляет себя: каждый живой демон пишет heartbeat-файл в store-дире воркспейса — `~/.squeezr/workspaces/<storeKey>/<kind>.heartbeat`, kind = `watch` или `serve-watch`. Внутри JSON `{pid, kind, startedAt, lastBeatAt}`: бит раз в 5 с и после каждого успешного прохода, запись атомарная (tmp + move), при чистом выходе файл удаляется. Демон «жив» = `lastBeatAt` свежее 30 с И pid реально существует. Два kind живут независимо: на одном воркспейсе спокойно крутятся и терминальный `watch`, и `serve --watch` от MCP-клиента — два файла, друг другу не мешают. Мёртвые heartbeat-файлы никто не чистит: они видны как `dead` — это маркер «демон умер некрасиво», а не мусор.

Управление — по точному storeKey (как у `drop --store-key`, без fuzzy-резолва):

```bash
squeezr fleet stop <storeKey>     # остановить демонов обоих kind
squeezr fleet restart <storeKey>  # перезапустить watch-демон
```

- `stop` читает heartbeat обоих kind и останавливает живых: SIGTERM → 5 секунд грейса → SIGKILL (на Windows — kill всего дерева процессов). Пид из heartbeat перед сигналом верифицируется: процесс должен выглядеть как squeezr (имя/командная строка + сверка времени старта с `startedAt` heartbeat) — чужой процесс или переиспользованный пид не тронут, будет понятная ошибка. Живых нет — `not-running`, exit 0.
- `restart` работает только с `watch`: живой демон аккуратно останавливается, затем detached-процесс `squeezr watch` спавнится с cwd = rootPath воркспейса (rootPath исчез — ошибка с хинтом на `drop`). Живой `serve --watch` не трогается: им владеет MCP-клиент, и попытка его перезапуска честно ошибается с хинтом (`squeezr mcp install` или рестарт агента).
- Обе команды умеют `--json`: один NDJSON-объект результата, stdout чистый (контракт 035).

## Stats: экономия токенов в цифрах

«А он правда экономит?» — `squeezr stats` отвечает числами, а не обещаниями. Каждая навигационная команда (CLI: `search`/`symbol`/`refs`/`map`/`expand`; MCP: все шесть тулов) пишет одну строку телеметрии в `queries.jsonl` в сторе воркспейса, stats считает по ней сводку:

```bash
squeezr stats           # таблица по тулам + savings + кэш
squeezr stats --json    # один JSON-объект (контракт 035)
squeezr stats --reset   # очистить телеметрию
```

```
Tool     Count  Avg tokens  Truncation
expand   3      486.0       0.0 %
search   1      7086.0      0.0 %

Savings: 8544 actual vs 10617 counterfactual tokens — saved 2073 (×1.2)
Cache hit-rate: 0.0 % across 0 session(s)
Period: 2026-09-17 14:57 — 2026-09-17 14:58 (4 records)
```

- **Таблица по тулам** — вызовы, средний `tokensUsed` и доля обрезанных бюджетом ответов.
- **Savings** — counterfactual-сравнение: actual — сумма `tokensUsed` всех ответов; counterfactual — сколько токенов ушло бы, если бы каждый раскрытый через expand файл читался целиком (файл считается один раз, оценка по размеру — та же формула, что у чанкера; файлы, которых уже нет в индексе, честно исключаются с отдельной строкой). `saved` — разница, `×N.N` — во сколько раз меньше.
- **Cache hit-rate** — доля повторных ответов из кэша serve-сессий (см. семантику ответа ниже) и число сессий, где кэш срабатывал; CLI-запуски кэш не используют — каждый запуск и есть своя сессия.
- **Period** — окно записей и их количество; больше 50 000 записей — серый хинт на `--reset` (авточистки нет, осознанно).

Приватность: телеметрия — это локальный `queries.jsonl` в store-каталоге воркспейса (`~/.squeezr/workspaces/<storeKey>/`). В проекте и в git её нет, наружу не уходит, с воркспейсом и умирает — `squeezr drop` сносит вместе со всем стором.

## Roadmap

- [x] **M1** — каркас CLI, LiteDB-схемы, конфиг, workspace
- [x] **M2** — триграммный индекс, точный `search`, инкремент, `compact`
- [x] **M3** — Roslyn: символы, чанки, `symbol`, `map`, `expand`
- [x] **M4** — Qdrant + ONNX-эмбеддинги: гибридный `search`, `refs`
- [x] **M5** — MCP `serve` со всеми тулами + README с agent-инструкциями
- [x] **M6** — tree-sitter для JS/TS, fallback-чанкер

## Стек

.NET 10 / C# 14 · Spectre.Console.Cli · LiteDB · Roslyn · tree-sitter (JS/TS) · Qdrant · Ollama · ONNX Runtime · xUnit + Shouldly

---

## English (short)

**Squeezr is a local code indexer for AI agents.** It squeezes maximum signal from your codebase into a minimum of LLM tokens — a single .NET binary, fully offline.

Born from a simple observation: agents waste 30–80k tokens navigating code by reading whole files. Squeezr gives them what senior devs actually use — a repo **map**, **symbol** lookup, **references**, and precise **span expansion** — as fast local queries and MCP tools.

The trigram engine is built after [Zoekt](https://github.com/sourcegraph/zoekt) and [Russ Cox's code-search design](https://swtch.com/~rsc/regexp/regexp4.html): substring and regex search over millions of lines in milliseconds, incremental and crash-safe. C# symbols come from Roslyn with compiler-grade accuracy. Every response respects a token budget (`--max-tokens`, honest `truncated` flag). Storage: central workspace store in `~/.squeezr` (registry + per-workspace store dirs, zero files in your project, `SQUEEZR_HOME` override; legacy in-project `.squeezr/` auto-migrates) + Qdrant/Ollama in Docker managed by `squeezr services up|down|status` (ROCm device passthrough on AMD GPUs) + embeddings: `auto` by default — ollama (GPU) when the endpoint is reachable, otherwise local ONNX on CPU (nomic-embed-text-v1.5, int8, ~131 MB, 768d); explicit ollama (native `/api/embed`, auto-pull, GPU-capable) or any OpenAI-compatible endpoint — nothing leaves your machine. Default embedding model: local ONNX nomic-embed-text-v1.5 (legacy bge-code-v1, 1536d, stays a supported option via `scripts/fetch-model.sh --model bge-code-v1` + `embeddings.onnx.model` in the workspace config — switching models on an existing workspace requires `squeezr index --reembed`).

An MCP server (`squeezr serve`) exposes the index to agents as six tools — `repo_map`, `code_search`, `symbol_lookup`, `find_references`, `expand_span`, `index_status` — with a uniform envelope (`tokensUsed`, `truncated`, `warnings`, `indexAge`, `staleFiles`). Client onboarding is one command: `squeezr mcp install` detects supported agent CLIs (Claude Code, Kimi Code, Qwen Code, Codex), registers a `serve --watch` entry via the client's own CLI (direct config merge for Kimi), and optionally writes an idempotent instruction block into the client's instruction file; `squeezr mcp doctor` verifies the setup per client (exit 0/1), `squeezr mcp remove` undoes it. Flags: `--client claude|kimi|qwen|codex|all`, `--scope user|project`, `--no-watch`, `--no-instructions`, `--yes`, `--json`. Workspace root = server process cwd, with a `CLAUDE_PROJECT_DIR` fallback for Claude Code (no `cwd` key is ever written to client configs); manual config paths (e.g. `~/.claude.json` / `.mcp.json`, `~/.codex/config.toml`) are listed in the RU section above. Watch mode (`squeezr watch`, `squeezr serve --watch`) keeps the index fresh automatically: debounced catch-up passes with lock backoff, full delete/rename lifecycle, no ghost hits. Every running daemon writes a heartbeat file (`<storeDir>/<kind>.heartbeat`, refreshed every 5 s, removed on clean exit), so `squeezr fleet` shows all workspaces' daemons at a glance — pid, uptime, stale root paths, pending chunks — and `squeezr fleet stop|restart <storeKey>` manages them: stop covers both daemon kinds (SIGTERM → 5 s grace → SIGKILL, heartbeat pids verified as squeezr processes), restart respawns the CLI watch daemon only (`serve --watch` is owned by the MCP client); `--json` is NDJSON per the 035 contract. For programmatic progress, `index --events` / `serve --watch --events` stream NDJSON events (`started` → `fileIndexed` → `progress` → `completed`/`error`), flushed per line — stdout for `index`, stderr for `serve` (stdout carries MCP JSON-RPC); a quiet incremental run emits exactly `started` → `fileIndexed`×N → `completed`.

Token economics are measurable: every navigation command (CLI and all six MCP tools) appends a local telemetry line to `queries.jsonl` in the workspace store dir, and `squeezr stats [--json|--reset]` turns it into a per-tool table plus counterfactual savings — actual tokens spent vs reading every expanded file whole — and the serve-cache hit-rate; telemetry never leaves the machine and is removed with the workspace. The four expensive MCP tools cache responses per serve session (LRU 100, canonical-JSON key), and any reindex invalidates the cache generation, so identical requests replay instantly only while the index is unchanged.

```bash
dotnet build Squeezr.slnx
squeezr init --name my-project && squeezr add /path/to/repo
squeezr services up
squeezr index && squeezr search "WorkspaceManager"
```

Linux install (self-contained binary + `~/.local/bin` symlink, no .NET required): `scripts/install.sh`. Uninstall: `scripts/uninstall.sh` (the `~/.squeezr` store with workspaces and models is left intact — remove it manually if you really want it gone).

Windows install (self-contained exe + PATH, no .NET required): `pwsh -File scripts\install.ps1`. Uninstall: `pwsh -File scripts\uninstall.ps1`.

Status: M1–M6 done (CLI skeleton, trigram index, Roslyn symbols, hybrid semantic search + refs, MCP server with all six tools, tree-sitter JS/TS + fallback chunker). See the roadmap above.
