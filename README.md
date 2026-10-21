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
# Windows: из исходников (нужен .NET 10 SDK)
powershell -File scripts\install.ps1
```

На Windows есть и второй путь — готовый установщик `SqueezrSetup-<version>-win-x64.exe`: двойной клик, без админа, с записью в «Программы и компоненты». Его собирают локально одной командой, см. [Windows: установщик `.exe`](#windows-установщик-exe).

Скрипты объявляют `#Requires -Version 5.1`, так что работают и в штатном Windows PowerShell (`powershell -File ...`), и в pwsh 7 — отдельно ставить pwsh не нужно.

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
squeezr vectors reconcile                     # точки Qdrant против meta.db: dry-run, --apply чистит сирот
squeezr drop                                  # полное удаление индекса workspace
squeezr workspace list                        # реестр воркспейсов из центрального стора
```

Машиночитаемый вывод (`--json`): `status --json` — один JSON-объект (camelCase, включая `staleFiles`/`indexAge`); `search --json` и `workspace list --json` — NDJSON (один объект на строку, флаш на строку; у search финальная строка — `{"type":"summary"}`). Ошибки в JSON-режиме — `{"error":{"code","message"}}` на stderr + ненулевой exit code, коды стабильные (`workspace_not_initialized`, `index_not_built`, `invalid_query`, ...). Человекочитаемый вывод при этом не меняется.

Прогресс-ивенты (`--events`): `squeezr index --events` и `squeezr serve --watch --events` стримят NDJSON-ивенты индексации — по одному на строку, флаш на строку: `started` (workspace; `provider?` — разрешённый провайдер эмбеддингов, только на `index`-прогонах с семантикой; у watch-проходов ещё `changedPaths`) → `fileIndexed` (`{repo, path, files}` — кумулятивный счётчик за прогон) → `progress` (`{repo, phase, total}` для фаз semantic/embeddings/trigram) → `completed` (`durationMs`, итоги `files/added/modified/deleted`, `symbols?`, `provider?`, счётчики отложенных чисток точек `deferredReaps`/`pendingReaps`, разбивка по репо) или терминальный `error` (`code`, `message`). Транспорт: у `index` — stdout, у `serve --watch` — stderr (stdout занят MCP JSON-RPC). Тихий инкрементный прогон даёт ровно `started → fileIndexed×N → completed`. Композиция с `--json`: сначала завершённая последовательность ивентов, затем финальный `--json`-объект; ошибки дублируются энвелопом 035 на stderr + exit 1. Занятый реестр воркспейсов (конкурентная запись в `workspaces.json`) валит прогон до старта: стрим открывается терминальным `error` без `started`, код `workspace_registry_busy` — сигнал «повторить через момент» для скриптов; тот же код приходит в `--json`/stderr-энвелопах всех команд, трогающих реестр.

Эмбеддинги — провайдер из `auto`/`onnx`/`ollama`/`openai`, выбирается секцией `embeddings` конфига воркспейса (лежит в сторе: `~/.squeezr/workspaces/<storeKey>/config.json`, правится удобнее через `squeezr tui` → Config):

| Провайдер | Когда выбирать | Конфиг |
|---|---|---|
| `auto` (дефолт) | GPU без настройки: на каждом прогоне индексации ollama отвечает на пробе — считаем на GPU через него, не отвечает — локальный onnx на CPU | `provider: "auto"` (пишется `init`'ом по умолчанию) |
| `onnx` | Локально на CPU, ничего не поднимать | Локальная ONNX-модель **nomic-embed-text-v1.5** (официальный int8-экспорт, ~131 МБ, 768d; WordPiece, mean pooling + L2, контекст модели 8192, squeezr режет до 512 — `maxSequenceLength`; префиксы `search_document:`/`search_query:` добавляет сам squeezr) |
| `ollama` | Заметно быстрее на больших репо; на AMD-GPU считает через ROCm (см. раздел про services) | `embeddings.provider: "ollama"` + секция `embeddings.ollama`: endpoint `http://localhost:11434`, модель `nomic-embed-text` (768d, профиль `nomic-embed-text-v1.5` — префикс-симметрия с onnx по семантике (одинаковые `search_document: `/`search_query: ` на стороне squeezr; сами вектора onnx≠ollama, поэтому смена провайдера переэмбеддит чанки). Нативный `/api/embed`, API-ключа нет. Модель подтянется сама при первой индексации (auto-pull); ollama не запущен — явная ошибка с подсказкой `squeezr services up` |
| `openai` | OpenAI-совместимый endpoint (OpenAI, Azure, vLLM) | Секция `embeddings.openAiCompatible` + ключ только через env `SQUEEZR_API_KEY` |

Скачать ONNX-модель: `scripts/fetch-model.sh` на Linux, `powershell -File scripts\fetch-model.ps1` на Windows. Порядок резолва модели: явный путь в конфиге → `~/.squeezr/models/` → bundled-копия рядом с бинарём. Обратно на bge-code-v1 (1536d, кодовая специализация): `scripts/fetch-model.sh --model bge-code-v1` (в pwsh: `-Model bge-code-v1`) + `"model": "bge-code-v1"` в секции `embeddings.onnx`; на существующем workspace смена модели честно ругнётся «collection vector size 768 ≠ 1536» — лечится `squeezr index --reembed`, молча перетереть коллекцию не выйдет. Смена провайдера/модели меняет ключ чанков — затронутые чанки переэмбеддятся автоматически. Qdrant недоступен — индексация и поиск мягко деградируют на точный слой, ничего не падает.

## For AI agents

An AI agent told to "use squeezr here" gets productive in one conversation. Run this only on an explicit human request — never install squeezr proactively. The full ritual with guardrails, tool cheat-sheet and troubleshooting lives in [`skills/squeezr/SKILL.md`](skills/squeezr/SKILL.md); short version:

```bash
curl -fsSL http://REDACTED-LAN-HOST:3000/REDACTED-FORGE-NS/Squeezr/raw/main/scripts/install-release.sh | bash
squeezr services up
squeezr init && squeezr add . && squeezr index --events
squeezr mcp install --all --scope user --yes
```

1. The installer resolves the latest published Gitea release, unpacks it into `~/apps/squeezr/<tag>/` and repoints the `~/.local/bin/squeezr` symlink (if that dir is not on PATH, the script says so — fix the shell profile and open a new terminal). Pin or roll back with `--version <tag>`. **Linux only** — releases are published for `linux-x64` alone. On Windows, build the setup executable locally (`powershell -File scripts\build-installer.ps1`) or install from source with `powershell -File scripts\install.ps1`.
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
├── workspaces.lock                  # lock реестра — одна операция записи workspaces.json за раз
├── workspaces/<storeKey>/          # index store одного workspace
│   ├── workspace.json               # манифест (репозитории)
│   ├── config.json                  # конфиг (embeddings, watch, ...)
│   ├── meta.db                      # LiteDB
│   ├── trigram.idx                  # триграммный индекс
│   ├── index.lock
│   ├── watch.owner                  # claim watch-цикла: pid+host+kind, эксклюзивное создание; чужой host переживается только временем (×4 stale-окна), не pid
│   ├── watch.heartbeat              # пока жив демон `watch` (флот, см. раздел про fleet)
│   ├── serve-watch.heartbeat        # ...или `serve --watch` от MCP-клиента
│   ├── queries.jsonl                # телеметрия запросов для `stats` (локально, drop сносит)
│   └── queries.lock                 # сериализует писателей queries.jsonl: аппенды, scrub при compact и stats --reset (пишущие ждут бюджет; reset при занятом локе отказывается, ничего не снеся)
├── models/                         # ONNX-модели эмбеддингов
└── infra/docker-compose.yml        # qdrant + ollama (генерируется `squeezr services up`)
```

- **Discovery как у git:** любая команда, запущенная из любого сабдиректория воркспейса, находит его сама (поиск вверх по реестру). «Зашёл не в ту папку» исчезло как класс.
- **Реестр:** `squeezr workspace list` показывает все воркспейсы из `workspaces.json`; записи, чей rootPath исчез с диска, помечаются `stale` — удаляются поодиночке через `squeezr drop --store-key <key>` или пакетно через `squeezr workspace repair --dry-run` / `--yes` (с размерами store-директорий). Записи с rootPath = `$HOME` или родитель стора (наследие инцидентов) помечаются `husk` с тем же хинтом на drop.
- **Форма storeKey:** `<slug имени>-<первые 8 hex SHA1 от канонического rootPath>` (slug: lower, буквы/цифры, разделители в `-`; пустое имя → `workspace`). Ключ — всегда ровно один сегмент внутри `workspaces/`; `drop --store-key` и prune-свип в repair отказывают ключам другой формы (`.`, `..`, сепараторы, NUL, двоеточие — на обеих платформах) до первого обращения к файловой системе: отказ не читает и не удаляет ничего.
- **Сироты и repair:** если store-директория пережила потерю реестра (крах, ручная чистка `workspaces.json`), `workspace list` показывает её как `orphan — adoptable` (манифест с rootPath, ключ сходится, корень жив), `orphan — needs root` (старый манифест без rootPath) или `orphan — root gone` (rootPath записан, но каталога нет на диске). `squeezr workspace repair` возвращает в реестр только живых сирот: адопт требует живого корня — и в батч-маршруте (живость снимается одним сканом), и в явном `--store-key <key> --root <path>` (путь проверяется на диске в момент записи ряда, хэш name+root обязан совпасть с ключом, чужой стор подсунуть нельзя). Мёртвый корень — тупик, а не работа: такой стор остаётся `needs root`-строкой с причиной `root-gone`, никогда не адоптится и никогда не свипуется (ряд без строки реестра primitive `drop` не трогает); единственный маршрут наружу — восстановить каталог по записанному пути и перезапустить repair. Пакетная чистка stale-записей требует явного подтверждения: `--dry-run` только показывает список, `--yes` или ответ в TTY подтверждают; без терминала и без `--yes` repair отказывается (отсутствие промпта — не согласие). Удаление идёт тем же единственным путём, что и `drop` (ряд + store-директория), adoptable-сироты и живые записи не удаляются; перед реальной чисткой — датированная копия `workspaces.json`. Запасной путь: `squeezr init` в корне проекта — поверх существующего store-дира он работает как lossless-adoption: живые `config.json`, манифест (со списком репо) и индекс (`meta.db`) сохраняются, сброса в дефолты нет.
- **Side-car-файлы:** `index.lock`, `watch.owner` и `queries.lock` лежат в store-каталоге рядом с данными, но в `workspace.json` не регистрируются — это примитивы координации писателей, а не метаданные воркспейса.

```
$ squeezr workspace list
Squeezr (orphan)   unknown — needs --root   squeezr-d23cb388   orphan — needs root
warning: workspace store 'squeezr-d23cb388' has no recorded root path; run 'squeezr workspace repair --store-key squeezr-d23cb388 --root <path>'.

$ squeezr workspace repair --store-key squeezr-d23cb388 --root REDACTED-PATH/Projects/AI-Tools/Squeezr
Adopted workspace Squeezr (squeezr-d23cb388) with root REDACTED-PATH/Projects/AI-Tools/Squeezr
```
- **Переменные окружения:** `SQUEEZR_HOME` переносит весь стор, `SQUEEZR_CLIENT_HOME` переносит каталог, в котором `squeezr mcp install/remove/doctor` ищет конфиги агентских клиентов (по умолчанию — профиль пользователя). Вторая нужна в первую очередь тестам и песочницам: под Windows `GetFolderPath(UserProfile)` ходит в shell-API известных папок и `USERPROFILE` игнорирует, так что иначе профиль не перенаправить. Не задана — поведение ровно как раньше.
- **Легаси-миграция автоматическая:** проекты со старым layout (`.squeezr/` + `squeezr.workspace.json` в корне) переезжают в стор при первом же обращении — переносится содержимое `.squeezr/` и манифест, squeezr-сгенерированный `docker-compose.yml` и мёртвый `state.json` удаляются. Детали — [docs/migration-central-store.md](docs/migration-central-store.md). Qdrant-коллекции (имя зависит от пути воркспейса, а не стора) переживают миграцию, вектора не теряются.

## Cross-repo workspaces

A workspace is not limited to one repository — register sibling repos and search them as a single index:

```bash
squeezr add ../other-repo --name other-repo
```

The path is absolute or relative (resolved against the workspace root); `--name` sets the repo name the tools filter on — without it, the directory name is taken.

- One workspace, many repos: `squeezr index` covers them all, and the watch daemon keeps each one fresh.
- Per-repo filtering: the `repo` parameter of `repo_map` / `code_search` / `symbol_lookup` / `find_references` (CLI twins: `--repo` on `search` and `map`).
- Subtree scoping: the `prefix` parameter of `repo_map` / `code_search` (CLI twins: `--prefix` on `map` and `search`) answers only inside a directory subtree, matched against stored relative paths. An empty subtree is an error naming the nearest directories, not a silent empty answer.
- Ranking curation: `indexing.rankExclude` in the workspace config (`["audits/**", "BACKLOG/**"]`) keeps doc trees out of `code_search` ranking — both arms — without unindexing them: `expand_span` / `find_references` / `symbol_lookup` still reach every excluded file, and no reindex is needed (query-time only). `prefix` narrows in, `rankExclude` pushes out, the two intersect. `index_status` lists the active patterns in `warnings`, `squeezr status` prints them next to the embeddings provider; a malformed glob produces a warning and is skipped instead of breaking the query. A value with a broken shape (a bare string instead of the array, non-string entries) no longer takes the whole config down: the string is salvaged as a one-pattern list, junk entries are dropped, and `squeezr status` still prints the surviving patterns. Drive-letter patterns (`C:/repo/**`, `C:\repo\**`) are rejected with a warning — patterns are repository-relative, so a drive prefix can never match anything.
- Query globs (`indexing.rankExclude`, MCP/CLI `pathGlob`) read the same classes as the ignore dialect — `[abc]`, `[a-z]`, `[!…]`/`[^…]`, `?`, `**` — since 063/T14 they match there too. They differ in two places: there `\` stays a directory separator (`src\**\*.cs`), not an escape, and nothing guards residual shapes — an unterminated `[` or a POSIX `[:alpha:]` reads as literal characters and a plain member set instead of being skipped with a warning the way the ignore layer does. The separator asymmetry also means a query class cannot contain `/` — the splitter has no bracket awareness. So `**/[Bb]in/**` really excludes `src/Bin/…`, while a glob written to name literal brackets (`audits/[v1]/**`) no longer names that directory — it reads as a one-character set over `v`/`1`.
- Coverage is visible before you query: `index_status` and `squeezr status` show per-repo file/symbol/chunk counts; `embeddings.coverage` in `index_status` adds the semantic share (embedded / total chunks).

Discovery stays per-directory: a project resolved from inside its tree keeps its own workspace — sibling repos join only when deliberately added with `add`.

## Исключения при индексации

Squeezr не индексирует всё подряд: встроенные дефолты режут `node_modules`, `bin`, `obj`, `dist`, `.next`, `.vs`, `coverage` и прочий шум. поверх дефолтов — три слоя, каждый следующий перебивает предыдущий (last-match-wins):

1. **`indexing.ignore`** в конфиге воркспейса (`~/.squeezr/workspaces/<storeKey>/config.json`) — глобы, которые ты хочешь видеть всегда.
2. **`.squeezrignore`** в корне репо — синтаксис gitignore, НЕ каскадируется (только корневой файл).
3. **`.gitignore`-каскад** — любой `.gitignore` по пути к файлу, паттерны относятся к директории этого файла. Включён по умолчанию.

Флаг `indexing.respectGitignore` (bool, дефолт `true` в `IndexingConfig`) — выключает весь слой `.gitignore`. Старые конфиги без ключа грузятся в `true`, миграция не нужна.

Поддерживаемое подмножество gitignore: пустые строки, `#`-комментарии, `!`-негация, ведущий/внутренний `/` = якорь к директории, завершающий `/` = только директории, `*`, `?`, `**`, символьные классы — наборы `[abc]`, диапазоны `[a-z]`, инверсия `[!…]` и `[^…]` — и `\`-эскейпы, снимающие магию с `* ? [ ]` и делающие литералом пробел, `#`, `!` (`Generated\ Files/` читается как `Generated Files/`). С 063/T14 эти формы матчатся, а не отбрасываются: <removed 2026-10-05 — 063/T14>~~НЕ поддерживаются `[character-classes]` и `\`-эскейпы~~</removed>. Отбрасываются с warning только остаточные формы — незакрытый `[`, POSIX-классы вида `[[:alpha:]]` (и вложенные в набор: `[a[:digit:]b]` — скипается весь паттерн, git-resolving не эмулируем), экранированный разделитель `\/`, `\ `-эскейп завершающего пробела (строка тримится до валидации, поэтому `Generated\ Files` работает, а `foo\ ` — нет) и висячий `\` в конце: `unsupported ignore pattern '<raw>' in <file>; pattern skipped.` В `<raw>` — строка файла как она в нём стоит, с исходными пробелами. Классы и эскейпы в `indexing.secretGuardExtra` ширят маску, но не сужают её: негатум `!` по-прежнему не проходит в компиляцию extras.

Наблюдаемость: `squeezr index` печатает `ignored N file(s)` рядом со skipped/added/modified. `squeezr status --json` и MCP `index_status` отдают блок `ignored: { files, bySource: { defaults|config|squeezrignore|gitignore } }` per repo (ключи с нулевым счётчиком отсутствуют — экономия токенов); `index --json`/`--events` отдают те же числа плоскими полями `ignored` (int) + `ignoredBySource` (ключи — тот же словарь, что в `ignored.bySource`). Юнит — файлы: директория, усечённая правилом (`out/`), засчитывается не как 1, а как все файлы внутри неё (посчитаны по именам, без чтения содержимого). Числа — снимок последнего индексационного прохода (не текущее состояние диска): удаление/перемещение файла на диске не меняет `ignored` до следующего `squeezr index`. Если исключения есть, warning указывает доминирующий источник и как его выключить/поправить. Молчаливый filter-out запрещён: `ignored > 0` всегда виден. Воркспейс, проиндексированный до появления этого поля, показывает `ignored` absent/0 без ошибок — честное отсутствие данных, не фейковые нули.

Снятие с индекса происходит на следующем проходе: файл, попавший в `.gitignore` после индексации, исчезнет при следующем `squeezr index` (или watch-проходе).

Read-path (`code_search`/`symbol_lookup`/`repo_map`/`find_references`, inline-освежение протухшего файла) не обходит дерево: каскад `.gitignore` собирается только по предкам пути этого файла, решения при этом те же, что у полного каскада.

**Breaking change (035 --json contract):** `RepoIndexResult` получил 7-ю позицию `Ignored` (int, total count) и 8-ю `IgnoredBySource` (`Dictionary<string,int>?`, ключи `defaults|config|squeezrignore|gitignore`, `null`/отсутствие — ничего не исключено). В `--json`-выводе `index`/`events` каждый `repos[]`-элемент теперь несёт поле `ignored` (int) и аддитивное `ignoredBySource` (объект со строковыми ключами — тот же словарь значений, что `ignored.bySource` в status). В `status --json` и `index_status` добавлен блок `ignored: { files, bySource }` per repo. Существующие поля не переименованы и не удалены — только аддитивные.

## Секрет-гард: что отказывает и как почистить старый стор

Гард решает **по форме пути**: чтобы классифицировать файл, он его не открывает — в классификаторе нет ни одного обращения к ФС (`SecretGuardTests.Classifier_SourceHasNoFileSystemProbe`). Значения секретов гард не видит и не пишет никуда: ни в отчёты, ни в `--json`, ни в телеметрию. Имена защищённых путей в отчётах прохода и чистки тоже не появляются — только счётчики. Имя звучит разве что на чтении, и там по-разному: **отказ** возвращает путь, который спрашивающий назвал сам, а **withheld-строка** называет первый снятый гардом результат — его спрашивающий мог вообще не называть (см. ниже).

**Встроенный набор — 19 масок.** `SecretGuard.BuiltInPatterns`, список заморожен тестом `BuiltInPatterns_AreTheFrozenSpecList`:

`**/.env`, `**/.env.*`, `**/*.key`, `**/*.pem`, `**/*.pfx`, `**/*.p12`, `**/*.jks`, `**/*.kdbx`, `**/id_rsa*`, `**/id_dsa*`, `**/id_ecdsa*`, `**/id_ed25519*`, `**/.npmrc`, `**/.netrc`, `**/.git-credentials`, `**/.pgpass`, `**/secrets.json`, `**/secrets.*.json`, `**/*.secret`

Единственное исключение — конвенциональные заглушки `.env.example` / `.env.sample` / `.env.template`, и только для `.env`-семейства: имя, которое формально заглушка, но по форме попадает в остальной набор (`.env.key`), защищено всё равно.

**Встроенное нельзя сузить.** Единственная конфигурационная ручка — `indexing.secretGuardExtra` (массив глобов в `config.json` воркспейса), и она **union-only**: пользовательские маски проверяются первыми, встроенные применяются всегда, вычитать из них нечем. Форма, которая ничего не называет, в `secretGuardExtra` не компилируется вообще — негатив `!`, комментарий, dir-only (`secrets/`) и остаточные неподдерживаемые формы **инертны по построению**, а не «отфильтрованы потом» (`SecretGuardTests.SecretGuardExtra_ClassesAndEscapesWidenOnly_NegationStaysInert`, `SecretGuardExtra_CannotNameAFileShape_NeitherWidensNorNarrows`). Символьные классы и `\`-эскейпы умеют только расширять маску. Пустые `indexing.ignore` и `secretGuardExtra` не сужают ничего (`EmptyIgnoreAndEmptyExtra_NarrowNothing`), а встроенные маски не входят в редактируемые дефолты (`BuiltInPatterns_AreNotPartOfTheEditableIgnoreDefaults`) — их нельзя убрать и через ignore-конфиг.

**Диалект `secretGuardExtra` — тот же, что у `indexing.ignore`** (подмножество gitignore, см. «Исключения при индексации»): неанкорированная форма читается **на любой глубине** — `*.tokens` закрывает и `root.tokens`, и `src/deep.tokens`; внутренний `/` анкорит от корня репо — `deploy/*.crt` закрывает `deploy/server.crt`, но не `deploy/sub/deep.crt`; `**/tokens.txt` — любая глубина, но не `tokens.txt.bak`. Смысл совпадает с ignore-слойем дословно, ряд проверок закреплён тестом `SecretGuardExtra_MeansWhatIndexingIgnoreMeans`.

**Две известные границы — это документированные limitations, а не молчание:**

1. **Путь, у которого `.env` — директория, не защищён.** Классификатор решает по **последнему сегменту**: `.env/dev`, `.env/local.json`, `vault/token.key/readme.md` protected не считаются (`DirectoryShapedSecret_LastSegmentDecides_NotProtected`). Файл с секретным именем *внутри* такой директории защищён своей маской (`.env/prod.key` — да). Хвостовой разделитель при этом тримится: запрос `.env/` — это отказ, а не «not found».
2. **`id_rsa*` матчит и публичные ключи.** `.ssh/id_rsa.pub` попадает под маску вместе с приватным ключом — публичная часть тоже не индексируется и не отдаётся. Осознанная цена: перестраховаться маской проще, чем пропустить приватный ключ.

**Как выглядит отказ для агента.** Явный запрос защищённого пути — ошибка, а не пустой ответ:

```
'.env' is protected by squeezr's built-in secret guard: never indexed, never rendered
```

Имя в **отказе** — запрошенный путь (плюс у `find_references`: если все объявления символа protected, в отказе окажется файл **первого** из них — не «единственного», а просто первого по порядку; молчать про «не найдено», когда файл есть, было бы враньем).

Молча пропущенные результаты считаются в `warnings` одной фразой во всех поверхностях (CLI stderr, MCP-конверт, TUI — wording не разъезжается):

```
warning: secret guard: 3 result(s) withheld ('.env' matches built-in patterns)
```

Внимание к референту: имя в этой строке — **не то, что ты набрал**, а первый результат, который гард снял. `repo_map` с префиксом `deploy/` назовёт файл под этим префиксом, запрос без префикса — защищённый путь, который вообще ни в одном аргументе не звучал. Так что сама warning-строка «безопасна для скриншота» ровно в той же мере, что и содержимое файла: не безопасна. Источник решения — `built-in patterns` либо `indexing.secretGuardExtra`; отказ же всегда говорит *built-in*, даже если решила твоя маска из extras — различает источник только tally. Тот же словарь у ingestion: `secret guard: 3 file(s) withheld from indexing (2 built-in patterns, 1 indexing.secretGuardExtra)`. Если `config.json` не прочитался, встроенные маски остаются, пользовательские выключаются, и это сказано отдельно: `could not read indexing config (…); user secret-guard masks are inactive`.

**Счётчики — где их искать.** `index --json` несёт `guardWithheld` / `guardWithheldBuiltIns` / `guardDropped` в корне и те же три в каждом `repos[]`; в `--events` корневой `completed` — `guardWithheld` и `guardDropped` (третий лежит только в `repos[]`); `squeezr status` даёт колонки `Withheld`/`Dropped` плюс строку-итог; `status --json` и MCP `index_status` — объект `guard { withheld, withheldByBuiltIns, withheldByConfig, dropped }` per repo. Числа — снимок последнего прохода, не живое состояние диска. Отказ гарда считается как skipped, проход от этого не падает.

**Что делает апгрейд — и чего он не делает.** Установка нового бинаря **не убирает уже проиндексированное**: центральный стор (`~/.squeezr/workspaces/<storeKey>/`) переустановка не трогает, и в старом `meta.db`, `trigram.idx` и `queries.jsonl` строки и имена protected-файлов лежат как лежали. С момента установки меняется только поведение read-пути — такие строки он больше не отдаёт. Данные уходят в один из двух моментов:

- **следующий проход индексации** (`squeezr index`, включая фоновый watch-проход): защищённые пути не попадают в обход, поэтому их строки в сторе считаются удалёнными и уходят через тот же каскад (символы, чанки, ссылки, очередь чистки векторов). Имена уходят из строк файлов и из **новых** записей reap-журнала (новые ставятся без имени); ряд `pending_reaps`, оставленный прежным билдом, своего имени в этом проходе не теряет — только в `squeezr compact`;
- **`squeezr compact`** — явный маршрут и единственная операторская команда, которая чистит стор целиком: гард вычищает оставшиеся защищённые строки, **redact-ит имена в reap-журнале**, **scrub-ит значения параметров в `queries.jsonl`** (включая repo-qualified и абсолютные формы, которые сам продукт и выводит), переписывает `trigram.idx`, перестраивает `meta.db` и возвращает освободившиеся байты.

Флага `--purge` у `compact` нет: чистка по гарду выполняется всегда как часть компактификации, и без запроса оператора не чистится ничего. Отчёт — только числами:

```
secret guard: 7 path(s) purged, 186 row(s) removed · 1 reap journal name(s) redacted · 28 recorded parameter value(s) redacted · 7 point cleanup(s) queued
```

Точки, которые проход не смог снять сам, доигрывает `squeezr vectors reconcile --apply`. Сырые байты очищенного стора проверены свипом по `meta.db`/`trigram.idx`/телеметрии — evidence в `audits/store-purge-2026-10-05.md`.

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

Установка кладёт единый бинарь: на Linux — в `~/.local/share/squeezr` + symlink `~/.local/bin/squeezr` (снести: `scripts/uninstall.sh`), на Windows — `squeezr.exe` в `%LOCALAPPDATA%\Programs\Squeezr` + user PATH (снести: `powershell -File scripts\uninstall.ps1`). Оба установщика сами проверят Docker и подскажут, если его нет — он понадобится для `squeezr services up` (Qdrant и, опционально, Ollama). Важно: ни установка, ни удаление не трогают стор `~/.squeezr` — воркспейсы и модели переживают переустановку бинаря.

### Windows: установщик `.exe`

Кроме скриптовой установки Windows умеет нормальный установщик — `SqueezrSetup-<version>-win-x64.exe`. Он ставится **без прав администратора** в `%LOCALAPPDATA%\Programs\Squeezr` (тот же каталог, куда кладёт `install.ps1`, так что одно аккуратно перекрывает другое), сам добавляет каталог в user PATH и регистрируется в «Программах и компонентах» — удаление идёт оттуда, а не поиском скрипта в забытом чекауте. Если запустить с повышением прав, предложит поставить на всю машину в `%ProgramFiles%\Squeezr`.

Готовых сборок под Windows нигде не лежит и не будет: Gitea-релизы собираются только под `linux-x64`, Windows-раннера нет. Пакет собирается локально, на любой виндовой машине:

```powershell
# пререквизит: Inno Setup 6 (плюс .NET 10 SDK)
winget install -e --id JRSoftware.InnoSetup

powershell -File scripts\build-installer.ps1
# → artifacts\installer\SqueezrSetup-1.0.2-win-x64.exe
```

Флаги: `-Rid win-x64|win-arm64` (дефолт `win-x64`), `-Version <строка>` (по умолчанию берётся `<Version>` из `src\Squeezr.Cli\Squeezr.Cli.csproj`), `-SkipBuild` (упаковать уже опубликованное дерево `artifacts\publish`), `-IsccPath <путь>` (явный компилятор), `-OutputDir <путь>`, `-InstallInno` (доставить Inno Setup через winget, если его нет). Inno ищется по PATH, в ключах удаления HKCU и HKLM и в стандартных каталогах — per-user установка в `%LOCALAPPDATA%\Programs\Inno Setup 6` находится так же, как машинная.

Что стоит знать:

- **Пакет не подписан** — сертификата нет. При первом запуске SmartScreen покажет «Windows protected your PC»: *More info* → *Run anyway*.
- **PATH подхватится только в новом терминале** — установщик рассылает `WM_SETTINGCHANGE`, но уже открытые консоли его не перечитывают.
- **Перед переустановкой поверх живых демонов** останови их: `squeezr fleet stop <storeKey>`. Иначе бинарь залочен, и закрывать его будет Restart Manager.
- **Удаление не трогает стор `~/.squeezr`** — воркспейсы, модели и compose-файл переживают деинсталл; снести их можно только руками, после `squeezr drop` и `squeezr services down`.
- **Модель эмбеддингов в пакет не входит** (+131 МБ ради опциональной семантики) — тянется отдельно через `scripts\fetch-model.ps1`.

Переехали со старой Windows-машины? Миграционный чек-лист — [docs/migration-windows-to-linux.md](docs/migration-windows-to-linux.md). Проект ещё на старом layout с `.squeezr/` в корне? Миграция в центральный стор происходит сама — [docs/migration-central-store.md](docs/migration-central-store.md).

## TUI: человеческий интерфейс

Не любишь помнить флаги — просто запусти `squeezr tui` (или голый `squeezr` в интерактивном терминале: без аргументов он открывает TUI). Если терминал неинтерактивный (pipe, CI) — бинарь честно падает в справку вместо меню.

Меню контекстное — зависит от состояния workspace:

- **Папка без workspace** (воркспейс не найден — squeezr ищет его вверх по дереву от текущей папки) — сразу предложение «No workspace found here. Initialize now?» (дефолт — да) → форма `init`. Отказ — не тупик: остаётся меню из двух пунктов — Initialize workspace / Exit, можно передумать без рестарта.
- **Workspace инициализирован** — полный круг: Dashboard / Search / Map / Index / Add repository / Config / Drop workspace / Token stats / Usage analytics / Exit.

Пункты верхнего уровня:

- **Dashboard** — сводка: счётчики файлов/символов/чанков по репо, состояние обоих сервисов (qdrant — points/pending, ollama — running + модель провайдера установлена/нет), pending-эмбеддинги, возраст индекса. Сервис лежит — под статусом явный хинт `qdrant is down — start it with: squeezr services up` (для ollama — аналогично). Репозиториев ещё нет — в конце предложение «No repositories yet. Add one now?» (дефолт — да) → форма Add repository.
- **Search** — вводишь запрос, получаешь хиты с флагами `[exact]`/`[semantic]`/`[both]` (repo, file:line, сниппет — без тел). Выбор хита раскрывает спан с контекстом из живого файла; «More context (×2)» раздувает контекст, пока хватает бюджета. Qdrant лежит — видишь warning и exact-only выдачу.
- **Map** — браузер карты: репо → директории → файл → символы с сигнатурами (тел нет). Из символа — тот же expand его спана.
- **Index** — инкремент, полный реиндекс или `--reembed` с live-прогрессом по фазам.
- **Add repository** — форма `add` вместо флагов.
- **Config** — формы поверх конфига воркспейса (в сторе: `~/.squeezr/workspaces/<storeKey>/config.json`): qdrant, embeddings (onnx/ollama/openai), chunking, indexing. Валидация на вводе (URL, int > 0, provider), diff-сводка «было → станет» перед сохранением, после смены embeddings — напоминание про `index --reembed`.
- **Drop workspace** — подтверждение и полное удаление индекса workspace (`drop`).
- **Token stats** — per-workspace аналитика телеметрии: таблица по тулам, savings, cache hit-rate, разрезы By agent / By session — тот же контент, что у `squeezr stats` (общий рендерер). Снапшот на входе в экран; свежие данные = повторный вход.
- **Usage analytics** — глобальный rollup по всем воркспейсам реестра: per-workspace блоки со stale-пометками + итоговая таблица By agent — то же, что `squeezr usage` без флагов. Текущий воркспейс не нужен по смыслу (данные fleet-wide), пункт живёт в общем меню.

Всё то же самое через CLI-флаги остаётся доступным — TUI это надстройка, не замена.

## MCP-сервер: агент ходит по индексу, а не по файлам

`squeezr serve` поднимает MCP-сервер (stdio, JSON-RPC на stdout). Агент получает индекс как шесть тулов — карту, поиск, символы, ссылки и раскрытие спанов — и не читает файлы целиком вообще.

### Подключение: `squeezr mcp install`

Руками в конфиги лезть не нужно — одна команда зарегистрирует сервер у клиента:

```bash
squeezr mcp install                          # интерактив: детект клиентов → мультивыбор → scope → инструкции
squeezr mcp install --client claude --scope project
squeezr mcp install --client all --scope user --yes   # все найденные клиенты, без вопросов (для скриптов)
squeezr mcp install --client all --yes       # «почини всё» после апгрейда: перетирает регистрации на актуальный путь
```

| Флаг | Что делает |
|---|---|
| `--client claude\|kimi\|qwen\|codex\|all` | Кого подключать; без флага squeezr сам найдёт установленных клиентов и предложит мультивыбор; `all` — все найденные |
| `--scope user\|project` | `user` — сервер доступен во всех проектах, `project` — только в этом workspace; без флага спросит (с `--yes` дефолт — `user`) |
| `--no-watch` | Не добавлять `--watch` к `serve` (по умолчанию индекс сам догоняет правки на диске) |
| `--no-instructions` | Не писать агенту блок навигационных инструкций |
| `--yes` | Неинтерактивный режим: ни одного вопроса (для скриптов); требует явный `--client`; блок инструкций при этом НЕ пишется — только с `--instructions` |
| `--instructions` | Явное согласие на блок инструкций в паре с `--yes` (без `--yes` флаг не действует; если задан и `--no-instructions` — побеждает он) |
| `--json` | Машиночитаемый ответ вместо таблички |

Что происходит под капотом: запись регистрируется как `serve --watch`, конфиг пишет родной CLI клиента (`claude mcp add`, `qwen mcp add`, `codex mcp add`), а где CLI нет (Kimi Code) — squeezr сам мержит JSON в конфиг-файл. В `command` попадает **стабильный путь** — `~/.local/bin/squeezr`, если такая ссылка существует (dev-сборка из репо — путь бинаря как есть): после `install-release.sh` клиенты сами подхватывают новую версию при следующем переподключении, протухших регистраций больше нет. Чужие серверы в конфиге не трогаются; повторный install обновляет запись при прямой записи (Kimi), а по CLI-пути клиент ответит «already exists» — это нормально, запись уже на месте. Отдельно спросит (дефолт — «да»), дописать ли агенту маркер-блок с флоу навигации: идемпотентно, в инструкционный файл клиента (Claude — CLAUDE.md, Kimi и Codex — AGENTS.md, Qwen — QWEN.md), `mcp remove` убирает его за собой.

`squeezr mcp doctor` — чекап подключения: клиент найден, запись есть (user/project), бинарь в PATH, workspace проиндексирован, headless-статус через CLI клиента. Плюс детект **stale-регистраций**: если в конфиге клиента прописан путь, отличающийся от стабильного, doctor покажет `stale` с парой «registered → expected» и готовой командой лечения (`squeezr mcp install --client <id> --yes`); в `--json` — аддитивные поля `userCommand`/`projectCommand`/`userStale`/`projectStale`/`expectedCommand`, exit code стабильный stale не меняет. `squeezr mcp remove [--client ...] [--scope ...]` удаляет запись и блок инструкций; обе команды уважают `--client` и `--json`. Живой `serve` при старте сверяет свой бинарь с целью стабильной ссылки и на несовпадении выводит в stderr одну строку-хитрость: «регистрация могла устареть — прогони doctor/install» (молчит, если ссылки нет или всё совпало).

Корень workspace сервер разрешает от cwd процесса вверх (запускай клиент из корня проекта); Claude Code не умеет cwd в конфиге сервера — поэтому `serve` дополнительно уважает `CLAUDE_PROJECT_DIR`, который Claude выставляет своим MCP-процессам. Перед подключением нужны `squeezr init` / `add` / `index` — сервер читает готовый индекс и сам его не строит. Qdrant опционален: без него семантический слой молча отключается, `code_search` работает exact-only (warning в ответе).

### Project-level onboarding

The default instructions block is per-client and per-machine — it never lands in the repo. The project block is the other tier: a committed `squeezr:project-instructions` section in the project's own agent files, so everyone who clones the project gets the same navigation rules and command cheat-sheet.

```bash
squeezr mcp install --project   # write/refresh the block; no client registration
squeezr mcp remove --project    # strip the block from every carrier file
```

Without `--client`/`--all` the command only writes the block (block-only mode, works non-interactively); combined with clients it registers them and writes the block in one pass. Targeting: `AGENTS.md` always (created when missing); `CLAUDE.md`/`QWEN.md` only when they are real files — a one-line `@AGENTS.md` import pointer is left untouched. Plain `mcp install` never touches project files: the flag is explicit (`--yes` does not imply it), and the interactive prompt for it defaults to "no".

Re-running install refreshes the block in place between its markers — no duplicates. The block header carries the squeezr version that wrote it: after upgrading squeezr, re-run `squeezr mcp install --project` to refresh the text. If a target file has one-sided markers (start without end or vice versa), the file is left untouched and you get exactly one stderr warning naming it. `mcp remove --project` strips the block from every file carrying it and never deletes files — a file left empty is fine.

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
| `repo_map` | `repo?`, `prefix?`, `depth?` (2), `maxTokens?` (4000) | дерево репо: директории → файлы → члены с сигнатурами. Тел нет, только карта для навигации. `prefix` (например `"src/"`) сужает выдачу до поддерева — имена директорий рендерятся относительно prefix, `depth` сохраняет структурный смысл (1 = директории, 2 = файлы, 3 = члены) |
| `code_search` | `query` (обяз.), `repo?`, `prefix?`, `topK?`, `maxTokens?` (2000) | гибридные совпадения `{repo, file, line, score, source, snippet}`; source = `exact`/`semantic`/`both`; тела не возвращаются. Один хит = один файл: выигрывает лучший спан, прочие спаны того же файла схлопываются в `alsoMatched` (поле присутствует только когда их ≥1), поэтому `topK` ограничивает число разных файлов, а не спанов. `prefix` фильтрует оба рука (exact и semantic) по поддереву директорий. Ранжирование курит конфиг `indexing.rankExclude`: исключённые деревья не попадают в выдачу, но остаются в индексе. Hybrid-ветка получила identifier-точную руку: многотокенные запросы с code-shaped токенами (camelCase, `snake_case`, длина ≥ 12) или редкими токенами (df ≤ 5% docs) попадают в отдельную ветку с `source=exact` — точнее, чем phrase-literal для составных имён. Фильтры `repo`/`language`/`pathGlob`/`prefix`/rankExclude сужают и эту ветку — тем же `BuildFilter`, что и phrase-руку. Ограничение ветки: case-развёртка триграмм кэпируется (`CaseVariantCap = 64` делится на число триграмм токена, вариантов на триграмму 8), поэтому lowercase-токен длиннее ~10 символов не достанет написание, существующее только в PascalCase, и молча выпадает из дискриминантов — тихая потеря recall (phrase-рука в том же случае уходит в FullScan по диску, одиночные литералы не теряют). Лечение — case-folded триграммы в индексе: это смена формата индекса с ребилдом, своей фичи в бэклоге пока нет |
| `symbol_lookup` | `name` (обяз.), `kind?`, `repo?`, `maxTokens?` (300) | определения символа: сигнатура, doc-comment, `span`, тело — если влезло в бюджет. Тело типа не влезло → `members` (имя, kind, строка) в том же бюджете + счётчик `membersOmitted`: каждая строка — готовая координата для `expand_span`, тупика больше нет. Символ не найден → пустой `data` + `didYouMean` (топ-3 кандидата по trigram-Jaccard, порог 0.34), не ошибка |
| `find_references` | `symbol` (обяз.), `repo?` | все точки вызова `{repo, file, line, kind, enclosingSymbol}` — `repo` в каждой строке различает побайтово одинаковые пути из разных репозиториев (checkout плюс его worktree). Одноимённые определения с одним и тем же списком вызовов дают один набор строк, и конверт несёт аддитивное `definitions` — `{repo, kind, file, startLine, endLine}` по каждому объявлению за схлопнутыми строками (те же поля, что в `refs --json`). Поле появляется ровно в двух случаях: два и более одноимённых объявления делят один список вызовов, либо вызовов у символа нет вовсе (иначе пустой `data` не сказал бы про объявления ничего); в прочих ответах поля нет, и конверт остаётся шестиключевым. `definitions` — метаданные схлопнутых строк: в `tokensUsed`, как и в CLI, не считаются. enclosingSymbol захватывается на индексации Roslyn'ом (точно, включая partial-классы); для индексов, построенных до захвата, значение выводится по спанам символов (fallback-инференс). Символ не найден → tool error (как `squeezr refs`, exit 1) |
| `expand_span` | `file`, `startLine` (обяз.), `endLine?`, `contextLines?` (3), `maxTokens?` (1000) | строки спана + контекст из живого файла на диске (видит правки после индексации). Единственный тул, отдающий код построчно |
| `index_status` | — | свежесть: возраст индекса, per-repo счётчики файлов/символов/чанков, блок `ignored: { files, bySource }` per repo (исключённые файлы с разбивкой по источнику: defaults/config/squeezrignore/gitignore; нулевые источники отсутствуют), pending-эмбеддинги и `embeddings.coverage` (доля эмбедженных чанков, 0..1; `null`, когда эмбеддить нечего), доступность Qdrant, `rootPath` воркспейса (если это не корень твоего проекта — сессия смотрит в чужой воркспейс). Когда есть исключения, `warnings` указывает доминирующий источник и как его выключить. Когда ранжирование курируется, `warnings` перечисляют активные паттерны `indexing.rankExclude`. **Аддитивно (контракт 035):** если в реестре два и более воркспейса, `data` несёт `workspaces` — массив `{ name, rootPath, storeKey, current, stale }` (не больше 12 строк, строка текущей сессии идёт первой; сверх лимита считаются в `workspacesOmitted`). При одном воркспейсе обоих ключей нет вовсе, и вывод `data` совпадает с прежним байт в байт — существующие поля не переименованы и не удалены |

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
- `warnings` — мягкая деградация (например, «vector store unavailable, falling back to exact search only» при полном отказе или «semantic coverage: 76% (3017 chunks pending embedding)» в `code_search`, когда семантический слой накачан не целиком — при 100% строки нет), подсказки при обрезке и «cache: invalidated (index changed)» — метка смерти кэша (см. ниже).
- `indexAge` / `staleFiles` — свежесть. Изменённые файлы освежаются inline при чтении; то, что освежить не удалось, честно попадает в `staleFiles` — агент видит, что данные по этим файлам протухли, и может предложить `squeezr index`. Окончательное решение о реиндексе — за хозяином workspace.

Повторные запросы почти бесплатны: дорогие тулы (`repo_map`, `code_search`, `symbol_lookup`, `find_references`) кэшируют ответы внутри serve-сессии — LRU на 100 записей, ключ по каноническому JSON параметров (порядок свойств и null-и-absent на ключ не влияют). Идентичный запрос в той же сессии = мгновенный повтор сохранённого конверта, телеметрия помечает его `cacheHit: true` (и он виден в hit-rate у `squeezr stats`). Любая переиндексация — watch-проход или внешний `squeezr index` — меняет поколение кэша (mtime meta.db): мёртвые записи выбрасываются сразу, не дожидаясь LRU-эвикции, а первый ответ после смены поколения несёт в `warnings` строку `cache: invalidated (index changed)` — один раз на поколение за сессию, в сам кэшируемый конверт она не попадает и при повторах не размножается. Такой запрос помечается в телеметрии `cacheInvalidated: true`, так что «кэш умер» отличимо от «кэша просто не было». `expand_span` не кэшируется (читает живой файл с диска), `index_status` дешёвый сам по себе.

### Error-плоскость: structuredContent

Успех и ошибка едут в разных плоскостях — клиент должен уметь читать обе. Успех — `content[0].text` с JSON-конвертом выше. Ошибка — `isError: true`, человекочитаемый текст в `content[0].text` и машиночитаемый контракт в `structuredContent`:

```json
{
  "isError": true,
  "structuredContent": {
    "code": "workspace_not_initialized",
    "message": "Workspace is not initialized. Run 'squeezr init' first.",
    "rootPath": "/path/to/workspace",
    "nextSteps": ["cd '/path/to/workspace' && squeezr init", "cd '/path/to/workspace' && squeezr index"],
    "cause": "root_without_manifest"
  }
}
```

- `code` — машина-читаемая причина (`workspace_not_initialized`, `index_not_built`, …), стабильна между релизами в рамках аддитивного контракта 035.
- `nextSteps` — точные команды для следующего шага, в одном словаре с `squeezr mcp doctor` и `hook pre-read`.
- `cause` — уточнение внутри кода, там где причин несколько: у `workspace_not_initialized` это `root_without_manifest` (сессия внутри рута — инициализируй и проиндексируй его) или `session_outside_root` (сессия вне рута — перерегистрируй клиента, **не сей второй стор**).

В CLI-плоскости (`--json` у команд, не в MCP-тулах) конверт ошибки дополнительно несёт `error.removedPaths` на drop-операциях: что уже удалилось, когда реестр обновить не удалось.

`mcp doctor` (поле `indexState` в `--json`) и `hook pre-read` печатают тот же словарь кодов и команд; doctor видит сессионную директорию и в состоянии `session_outside_root` (сессия вне анкоренного рута) выдаёт rebind-шаги вместо запрещённой инициализации, а `workspaceIndexed` выводится из той же классификации, что и `indexState` — флаг и блок состояния не противоречат друг другу.

## Watch-режим: индекс догоняет рабочее дерево сам

Индекс по требованию — это компромисс: правишь файл, а `code_search` ещё отвечает про старую версию. FreshnessGuard освежает точечно при чтении, но полный догоняющий проход запускаешь руками. Watch-режим убирает и это.

Две формы, один движок:

```bash
squeezr watch            # человек: демон в терминале, Ctrl+C — чистый выход
squeezr serve --watch    # агент: тот же движок внутри MCP-сервера
```

Как работает: `FileSystemWatcher` на корнях репо → фильтрация (те же ignore-глобы и расширения, что у индексации; служебные файлы стора не считаются — они живут вне проекта) → изменённые пути копятся в changed-сет → после окна тишины один инкрементальный проход по всему накопленному. «Save-all из 50 файлов» = один проход. Индекс занят (ручной `index`, TUI, второй serve) — движок ждёт с экспоненциальным backoff и никогда не отбирает лок. Живой статус: в терминале — Live-грид (workspace / status / last pass / error), в pipe — строки `pass started: N changed paths` / `pass completed: +A ~M -D` в stdout. Для программного потребления — `serve --watch --events`: NDJSON-ивенты проходов (`started` с `changedPaths` → `fileIndexed` → `completed`, у `completed` есть счётчик `embedded`) на stderr, JSON-RPC на stdout не тронут (схема — в разделе про `--json` выше). Без `--events` serve не молчит в двух местах: журнал отложенных чисток точек — строка `{"type":"watchPass","deferredReaps":N,"pendingReaps":M}`, дренаж эмбеддингов — строка `{"type":"watchDrain","at":"…","embedded":N}` на каждый проход, что что-то вдребезжал (или `{"type":"watchDrain","at":"…","embedded":0,"idleReason":"…"}` один раз на причину простоя — недоступный провайдер или векторный стор).

Дренаж эмбеддингов: `serve --watch` носит провайдер и векторный стор и сам доэмбеддит `embeddings.pending` до нуля — ручной `squeezr index` для догоняния не нужен. Провайдер + стор конструируются один раз на процесс serve, в фоне, до старта JSON-RPC это не доходит (тулы отвечают сразу, даже пока onnx грузит модель или resolver пробивает эндпоинт). Темп дренажа ограничен бюджетом `watch.embedBudgetPerPass` чанков на проход; как только проход выбрал бюджет целиком, движок сам перевзводит следующий пустой проход — догоняющая очередь размазывается по проходам с шагом debounce-окна, а не долбит в провайдер без паузы. На старте дренажа и по каждому проходу в stderr уходят строки провайдера (`watch embed drain ready (provider=ollama)`), отключения (`watch embed drain off (…)` с причиной) и счётчика `watchDrain` — по ним считается скорость (чанков/мин) и видна причина простоя. Эмбед-фейлы деградируют в warnings: чанки остаются в `embeddings.pending` (видны в `index_status`/`fleet`), serve живёт; упавший посреди прохода стор просто съедает остаток бюджета — недоэмбедденные чанки подбирает следующий проход. Смена модели (auto→onnx→ollama, другая модель/профиль) — это смена modelKey у чанков: вся очередь переэмбеддится один раз под новый ключ, проход честно предупредит `model key of repo … changed: N chunk(s) carry vectors from another embedding model`; старые ключи молча не переписываются, точки перезаписываются по тем же hash-id. Интерактивный `squeezr watch` дренаж не носит — это человеческий режим наблюдения; FreshnessGuard в query-пути тоже остаётся embed-free, чтобы не платить загрузкой модели за чтение.

Удаления — полный жизненный цикл (tombstone триграмм, чистка документов/чанков, drop Qdrant-точек файла): после `git checkout`/`git clean` выдача не содержит призраков. `serve --watch` с дренажем доигрывает журнал `pending_reaps` сам в ближайшем проходе; у watch без дренажа (интерактивный `squeezr watch`, `--no-embed-watch`) удаление уходит в журнал и доигрывается следующим `squeezr index` с эмбеддингами — и не молча: `deferredReaps`/`pendingReaps` в отчёте прохода. `serve --watch` не блокирует JSON-RPC — тулы отвечают и во время фонового прохода, конверт не меняется. FreshnessGuard остаётся страховкой: пока watch не догнал, конверт честно показывает `staleFiles`.

Конфиг — секция `watch` в конфиге воркспейса (`~/.squeezr/workspaces/<storeKey>/config.json`):

```json
{
  "watch": {
    "enabled": false,
    "debounceSeconds": 2,
    "backoffMaxSeconds": 30,
    "embedPending": true,
    "embedBudgetPerPass": 256
  }
}
```

`enabled` включает движок у `serve` без флага (флаг главнее конфига); явный `squeezr watch` — всегда намерение пользователя, конфиг не консультируется. `embedPending: false` выключает дренаж эмбеддингов у `serve --watch` (проходы остаются, эмбедов нет); `embedBudgetPerPass` — потолок чанков на проход дренажа, ноль и отрицательные значения невалидны и нарочно откатываются к дефолту (не «выключить по-другому»: единственный способ глушить дренаж — `embedPending: false` или `--no-embed-watch`). Флаг `--no-embed-watch` у `serve` глушит дренаж независимо от конфига. Отсутствие секции или невалидные значения = дефолты (движок выключен, дренаж включён, бюджет 256).

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

## Гигиена векторов: `squeezr vectors reconcile`

Штатную чистку делает индексация: у файла изменился набор чанков — его старые точки удаляются до вставки новых. Удаление файла точками занимается тот проход, который носит векторный стор; watch-проход его не носит, поэтому такое удаление записывается в журнал `pending_reaps` и доигрывается следующим `squeezr index` с эмбеддингами. Журнал ключуется не только id файла: `MetaStore` выдаёт id как `max(_id)+1`, то есть освобождённый id достаётся следующему добавленному файлу, и replay по одному id снёс бы точки уже живого файла — записью владеет пара (репо, путь), и если id отвечает другому файлу, запись сбрасывается, а осиротевшие точки закрывает `reconcile` (он сверяет хеши, а не id). А вот коллекция, собранная до этой механики, несёт точки-сироты: чанков давно нет в `meta.db`, а вектора лежат. `reconcile` разбирает этот завал одним проходом.

```bash
squeezr vectors reconcile            # dry-run: посчитать и показать, ничего не трогая (это дефолт)
squeezr vectors reconcile --apply    # удалить сироты + снять в pending чанки, у которых точка исчезла
squeezr vectors reconcile --json     # тот же отчёт машиночитаемо (один объект, camelCase)
```

Механика: scroll всех id коллекции → сравнение с полным множеством живых хешей чанков из `meta.db` → точка живёт, только если её id равен `ChunkPointId.FromHash(h)` для какого-то текущего хеша. Сверка по id, а не по payload `fileId`, поэтому одинаковый текст в двух файлах (точки схлопываются в одну, в payload остаётся один из файлов) вектор не теряет: хеш жив — точка жива. Второй фронт — обратная сторона той же схлопнутости: реп одного файла сносит общую точку, а ряд второго остаётся помечен `ok`, хотя вектора нет; такой ряд не видит ни один следующий проход, поэтому `--apply` сбрасывает его в `pending` и `squeezr index` переэмбеддит (в `--json` — `staleMarkers` в хешах и `requeued` в строках; единицы разные, потому и имена разные).

```
Collection squeezr-d23cb38850eb: 7492 → 6172 point(s) · 6707 chunk(s) · 6301 distinct chunk hash(es)
Deleted 1320 orphan point(s).
129 chunk(s) have no point yet — never embedded.
Re-queued 2 chunk(s) whose vectors are gone — run 'squeezr index' with embeddings available to re-embed them (watch passes do not embed).
```

Предохранители: без `--apply` не пишется ничего — ни точек не удаляется, ни маркеры в `meta.db` не сбрасываются (`--dry-run` и `--apply` вместе — ошибка `invalid_usage`); нет `meta.db` — отказ с кодом `index_not_built`; Qdrant не отвечает — ошибка до всяких чтений; в индексе ноль чанков, а в коллекции точки — отказ считать сиротами всё подряд (сначала `squeezr index`); на проход берётся `index.lock`, чтобы параллельная индексация не переписала чанки под сравнением. Прогон идемпотентен: второй запуск находит 0 сирот. В саму индексацию reconcile не встроен намеренно — скролл всей коллекции на каждый проход был бы O(корпус) ради легаси-проблемы.

Отдельно тула `index_status` (MCP) подсказывает, когда точек заметно больше, чем чанков: warning `the collection carries N more point(s) than the index has chunks …` — повод запустить `reconcile`. Сироты остаются не только из легаси: реп сносит общую точку второго файла (см. выше), а запись журнала, чей id уже достался другому файлу, сбрасывается — обе заначки подметает именно `reconcile --apply`; все остальные пути удаления точек либо делают, либо записывают в журнал и отчитываются числом.

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

Savings: query cost 8544 · whole-file baseline 10617 · saved 2073 (×1.2)
Cache hit-rate: no hits yet across 4 recorded session(s)
Period: 2026-09-17 14:57 — 2026-09-17 14:58 (4 records)
```

- **Таблица по тулам** — вызовы, средний `tokensUsed` и доля обрезанных бюджетом ответов.
- **Savings** — counterfactual-сравнение с whole-file базовой линией. Формула (без изменений со спеки 021/050):
  - `actual` (query cost) = сумма `tokensUsed` всех ответов;
  - `counterfactual` (whole-file baseline) = сумма оценок токенов ПОЛНЫХ файлов, раскрытых через `expand`/`expand_span` — файл считается один раз, оценка по размеру та же формула, что у чанкера; файлы, которых уже нет в индексе, честно исключаются со строкой `unknownFiles`;
  - `saved = max(0, counterfactual − actual)`, `ratio = counterfactual / actual`.

  Базовая линия осмысленна, только когда она перевешивает фактические затраты (`counterfactual > actual`) — тогда строка читается `Savings: query cost X · whole-file baseline Y · saved Z (×N)`. В противном случае (search-центрированный микс, где `counterfactual ≤ actual`, либо expand'ов не было вовсе) выводится `Savings: no whole-file baseline for this usage mix (query cost X; the baseline counts whole files read via expand only)` — без бессмысленного `saved 0 (×0.3)` рядом с двумя ненулевыми числами: `saved` и `×N` печатаются только в первой ветке. В `--json` аддитивно добавлены `savings.baseline` (метка базовой линии) и `savings.baselineApplies` (bool — перевешивает ли она query cost); существующие поля байт-совместимы (контракт 035).
- **Cache hit-rate** — доля повторных ответов из кэша serve-сессий (см. семантику ответа ниже) и число сессий, где кэш срабатывал; CLI-запуски кэш не используют — каждый запуск и есть своя сессия. Когда попаданий нет вовсе, строка не врёт: вместо противоречивого `0.0 % across 0 session(s)` рядом с живой таблицей By session печатается `Cache hit-rate: no hits yet across N recorded session(s)` — N сходится с числом сессий ниже, — а если кэш выбивала пересборка индекса, добавляется `— the index changed K time(s), last at <ts>`. В `--json` аддитивно добавлены `cache.invalidations` и `cache.lastInvalidation` (контракт 035: `lastInvalidation` отсутствует, пока инвалидаций не было).
- **Period** — окно записей и их количество; больше 50 000 записей — серый хинт на `--reset` (авточистки нет, осознанно).
- **By agent** — кто дёргал тулы: MCP-клиенты по имени и версии из initialize-handshake (`kimi-code 1.2.0`, `claude-code 2.0.5`), CLI-запуски как `cli`, записи до появления поля — `unknown`.
- **By session** — раскладка по сессиям (одна serve-сессия = один клиентский коннект): короткий id, клиент, число запросов, last-seen и топ-3 тула — видно «эта сессия жила три часа и сделала два code_search».

```
By agent
Client       Version  Count  Last seen
claude-code  2.0.5    3      2026-09-25 10:05
kimi-code    1.2.0    3      2026-09-25 09:15
cli          -        1      2026-09-25 11:31
unknown      -        1      2026-09-18 08:00

By session
Session   Client       Count  Last seen         Top tools
c0ffee00  cli          1      2026-09-25 11:31  search
77aa22bb  claude-code  3      2026-09-25 10:05  expand_span, find_references, repo_map
3f8a1c2e  kimi-code    3      2026-09-25 09:15  symbol_lookup, code_search
deadbeef  unknown      1      2026-09-18 08:00  search
```

Приватность: телеметрия — это локальный `queries.jsonl` в store-каталоге воркспейса (`~/.squeezr/workspaces/<storeKey>/`). В проекте и в git её нет, наружу не уходит, с воркспейсом и умирает — `squeezr drop` сносит вместе со всем стором.

## Usage: adoption по агентам, проектам и сессиям

`stats` отвечает за один воркспейс, а `squeezr usage` собирает rollup по всем воркспейсам реестра — какие агенты в каких проектах реально работают (команда не требует текущего воркспейса и запускается из любой директории, как `fleet`):

```bash
squeezr usage                      # rollup по всем воркспейсам + итоговая таблица по агентам
squeezr usage --agent kimi-code    # один клиент (точное совпадение, case-insensitive)
squeezr usage --workspace <key>    # один воркспейс по storeKey
squeezr usage --since 7d           # окно по времени: 90m / 24h / 7d / 30d
squeezr usage --json               # один JSON-объект (контракт 035)
squeezr usage --watch              # живой рефреш (3 с, --interval N), Ctrl+C — чистый выход
```

```
demo-proj-a-c4654229 — 8 record(s)
  /tmp/demo-proj-a
demo-proj-b-54669aef — 3 record(s)
  /tmp/demo-proj-b
                      By agent
Client       Version  Count  Last seen
kimi-code    1.2.0    5      2026-09-25 09:15
claude-code  2.0.5    3      2026-09-25 10:05
cli          -        2      2026-09-25 11:31
unknown      -        1      2026-09-18 08:00
Period: 2026-09-18 08:00 — 2026-09-25 11:31 (11 records)
```

- Фильтры комбинируются (AND): `squeezr usage --agent kimi-code --since 24h` — только kimi за сутки.
- Воркспейс с исчезнувшим rootPath помечается `(stale)` — команда не падает (best-effort, как `fleet`).
- `--watch` и `--json` несовместимы (json не бывает живым) — ошибка по контракту 035; в пайпе (не-TTY) `--watch` печатает один снапшот с предупреждением в stderr и выходит.
- Совсем без записей — честное `No usage telemetry yet.`.
- Retention тот же: суммарно больше 50 000 записей — хинт прогнать `squeezr stats --reset` по воркспейсам.

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

The trigram engine is built after [Zoekt](https://github.com/sourcegraph/zoekt) and [Russ Cox's code-search design](https://swtch.com/~rsc/regexp/regexp4.html): substring and regex search over millions of lines in milliseconds, incremental and crash-safe. C# symbols come from Roslyn with compiler-grade accuracy. Every response respects a token budget (`--max-tokens`, honest `truncated` flag). Storage: central workspace store in `~/.squeezr` (registry + per-workspace store dirs, zero files in your project, `SQUEEZR_HOME` override; legacy in-project `.squeezr/` auto-migrates) + Qdrant/Ollama in Docker managed by `squeezr services up|down|status` (ROCm device passthrough on AMD GPUs) + embeddings: `auto` by default — ollama (GPU) when the endpoint is reachable, otherwise local ONNX on CPU (nomic-embed-text-v1.5, int8, ~131 MB, 768d); explicit ollama (native `/api/embed`, auto-pull, GPU-capable) or any OpenAI-compatible endpoint — nothing leaves your machine. If a store dir ever outlives the registry (crash, manual cleanup), `squeezr workspace list` surfaces it as an orphan and `squeezr workspace repair` (or a lossless `squeezr init` in the project root — config, repo list and index are preserved) re-registers it without data loss **while the recorded root is on disk** — adoption always requires a live root, both in the batch route and in the explicit `--root` act, so a store whose root is gone is reported honestly as `orphan — root gone` and is neither adopted nor swept (restore the directory and re-run repair to adopt it); the same `repair` can bulk-prune stale rows whose roots are gone, but only behind explicit confirmation (`--dry-run` lists with store sizes, `--yes` or a TTY answer confirms, a non-TTY run without `--yes` refuses) and only through the single existing drop path. Default embedding model: local ONNX nomic-embed-text-v1.5 (legacy bge-code-v1, 1536d, stays a supported option via `scripts/fetch-model.sh --model bge-code-v1` + `embeddings.onnx.model` in the workspace config — switching models on an existing workspace requires `squeezr index --reembed`).

An MCP server (`squeezr serve`) exposes the index to agents as six tools — `repo_map`, `code_search`, `symbol_lookup`, `find_references`, `expand_span`, `index_status` — with a uniform envelope (`tokensUsed`, `truncated`, `warnings`, `indexAge`, `staleFiles`). Client onboarding is one command: `squeezr mcp install` detects supported agent CLIs (Claude Code, Kimi Code, Qwen Code, Codex), registers a `serve --watch` entry via the client's own CLI (direct config merge for Kimi), and optionally writes an idempotent instruction block into the client's instruction file; `squeezr mcp doctor` verifies the setup per client (exit 0/1), `squeezr mcp remove` undoes it. The registered `command` is a **stable path** — `~/.local/bin/squeezr` when that release symlink exists, the running binary's path otherwise — so upgrading squeezr no longer leaves clients pinned to a stale binary; `doctor` flags any registration whose `command` drifted from the expected path (a `stale` line with `registered → expected` and a ready `squeezr mcp install --client <id> --yes` repair, plus additive `userCommand`/`projectCommand`/`userStale`/`projectStale`/`expectedCommand` fields in `--json`), and a running `serve` that was launched from an off-target binary emits one stderr hint naming the repair (silent when the symlink is absent or already correct). Flags: `--client claude|kimi|qwen|codex|all`, `--scope user|project`, `--no-watch`, `--no-instructions`, `--instructions`, `--yes`, `--json`. Workspace root = server process cwd, with a `CLAUDE_PROJECT_DIR` fallback for Claude Code (no `cwd` key is ever written to client configs); manual config paths (e.g. `~/.claude.json` / `.mcp.json`, `~/.codex/config.toml`) are listed in the RU section above. Watch mode (`squeezr watch`, `squeezr serve --watch`) keeps the index fresh automatically: debounced catch-up passes with lock backoff, full delete/rename lifecycle, no ghost hits. Every running daemon writes a heartbeat file (`<storeDir>/<kind>.heartbeat`, refreshed every 5 s, removed on clean exit), so `squeezr fleet` shows all workspaces' daemons at a glance — pid, uptime, stale root paths, pending chunks — and `squeezr fleet stop|restart <storeKey>` manages them: stop covers both daemon kinds (SIGTERM → 5 s grace → SIGKILL, heartbeat pids verified as squeezr processes), restart respawns the CLI watch daemon only (`serve --watch` is owned by the MCP client); `--json` is NDJSON per the 035 contract. For programmatic progress, `index --events` / `serve --watch --events` stream NDJSON events (`started` → `fileIndexed` → `progress` → `completed`/`error`), flushed per line — stdout for `index`, stderr for `serve` (stdout carries MCP JSON-RPC); a quiet incremental run emits exactly `started` → `fileIndexed`×N → `completed`.

Token economics are measurable: every navigation command (CLI and all six MCP tools) appends a local telemetry line to `queries.jsonl` in the workspace store dir, and `squeezr stats [--json|--reset]` turns it into a per-tool table plus counterfactual savings — actual tokens spent vs reading every expanded file whole — and the serve-cache hit-rate; telemetry never leaves the machine and is removed with the workspace. Every record also carries the client identity (MCP handshake `clientInfo` for serve sessions, `cli` for CLI runs, `unknown` for pre-050 records), so `stats` breaks usage down By agent and By session, and `squeezr usage [--agent|--workspace|--since|--json|--watch]` rolls the telemetry up across every workspace in the registry — per-project blocks plus a fleet-wide by-agent table, runnable from any directory, with a live `--watch` view (3 s refresh, `--interval`) for watching adoption as agents work. The four expensive MCP tools cache responses per serve session (LRU 100, canonical-JSON key), and any reindex invalidates the cache generation — dead entries are dropped on the spot rather than lingering until LRU eviction, and the first response after the change carries `cache: invalidated (index changed)` in `warnings` (once per generation per session, never baked into the cached envelope) with the query flagged `cacheInvalidated` in telemetry — so identical requests replay instantly only while the index is unchanged, and an agent can tell a dead cache from a cold one.

```bash
dotnet build Squeezr.slnx
squeezr init --name my-project && squeezr add /path/to/repo
squeezr services up
squeezr index && squeezr search "WorkspaceManager"
```

Linux install (self-contained binary + `~/.local/bin` symlink, no .NET required): `scripts/install.sh`. Uninstall: `scripts/uninstall.sh` (the `~/.squeezr` store with workspaces and models is left intact — remove it manually if you really want it gone).

Windows install (self-contained exe + PATH, no .NET required): `powershell -File scripts\install.ps1`. Uninstall: `powershell -File scripts\uninstall.ps1`. The scripts declare `#Requires -Version 5.1`, so stock Windows PowerShell is enough — pwsh 7 is optional.

Windows setup executable: `powershell -File scripts\build-installer.ps1` packages the published tree with Inno Setup 6 (a build prerequisite — `winget install -e --id JRSoftware.InnoSetup`, or pass `-InstallInno`) into `artifacts\installer\SqueezrSetup-<version>-<rid>.exe`. It installs per-user without admin, registers in Add/Remove Programs, puts the install dir on PATH and leaves the `~/.squeezr` store alone on uninstall. Flags: `-Rid win-x64|win-arm64`, `-Version`, `-SkipBuild`, `-IsccPath`, `-OutputDir`, `-InstallInno`. There is no published Windows release artifact and there will not be one — the Gitea instance has no Windows runner, so the package is always built locally. The package is unsigned: SmartScreen warns on first run (*More info* → *Run anyway*).

Status: M1–M6 done (CLI skeleton, trigram index, Roslyn symbols, hybrid semantic search + refs, MCP server with all six tools, tree-sitter JS/TS + fallback chunker). See the roadmap above.
