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
- **Токен-бюджет first-class** — каждая команда уважает `--max-tokens`: набирает результаты по рангу, честно говорит `truncated: true`, когда бюджет кончился.
- **MCP-сервер** — агент ходит по картам и сниппетам, а не по простыням: `repo_map` → `code_search` → `find_references` → `expand_span`.
- **Всё локально** — LiteDB + файлы в `.squeezr/`, Qdrant в Docker для векторов, ONNX-эмбеддинги на CPU. Ни одного байта кода не уходит наружу.

## Быстрый старт

```powershell
# собрать и установить в PATH (self-contained, .NET не нужен)
pwsh -File scripts\install.ps1

# или просто собрать для разработки
dotnet build Squeezr.slnx

# создать workspace в корне твоего проекта
squeezr init --name my-project
squeezr add /path/to/repo

# проиндексировать и искать
squeezr index          # live-прогресс по фазам; --reembed для полного переэмбеддинга
squeezr search "WorkspaceManager"            # гибрид: точный + семантический (RRF)
squeezr search "w.rkspace" --regex --max-tokens 200
squeezr refs Slugify                          # кто вызывает символ (C#)
squeezr status                                # включая состояние Qdrant
```

Эмбеддинги по умолчанию — локальная ONNX-модель bge-code-v1 (скачать: `pwsh -File scripts\fetch-model.ps1`); OpenAI-совместимый endpoint включается секцией `embeddings` в `.squeezr/config.json` (ключ — только через env `SQUEEZR_API_KEY`). Qdrant недоступен — индексация и поиск мягко деградируют на точный слой, ничего не падает.

Установка кладёт единый `squeezr.exe` в `%LOCALAPPDATA%\Programs\Squeezr` и прописывает его в user PATH (скрипт сам проверит Docker и подскажет, если его нет — он понадобится только с M4). Снести: `pwsh -File scripts\uninstall.ps1`.

## Roadmap

- [x] **M1** — каркас CLI, LiteDB-схемы, конфиг, workspace
- [x] **M2** — триграммный индекс, точный `search`, инкремент, `compact`
- [x] **M3** — Roslyn: символы, чанки, `symbol`, `map`, `expand`
- [x] **M4** — Qdrant + ONNX-эмбеддинги: гибридный `search`, `refs`
- [ ] **M5** — MCP `serve` со всеми тулами
- [ ] **M6** — tree-sitter для JS/TS, fallback-чанкер

## Стек

.NET 10 / C# 14 · Spectre.Console.Cli · LiteDB · Roslyn · Qdrant · ONNX Runtime · xUnit + Shouldly

---

## English (short)

**Squeezr is a local code indexer for AI agents.** It squeezes maximum signal from your codebase into a minimum of LLM tokens — a single .NET binary, fully offline.

Born from a simple observation: agents waste 30–80k tokens navigating code by reading whole files. Squeezr gives them what senior devs actually use — a repo **map**, **symbol** lookup, **references**, and precise **span expansion** — as fast local queries and MCP tools.

The trigram engine is built after [Zoekt](https://github.com/sourcegraph/zoekt) and [Russ Cox's code-search design](https://swtch.com/~rsc/regexp/regexp4.html): substring and regex search over millions of lines in milliseconds, incremental and crash-safe. C# symbols come from Roslyn with compiler-grade accuracy. Every response respects a token budget (`--max-tokens`, honest `truncated` flag). Storage: LiteDB + Qdrant (Docker) + ONNX embeddings — nothing leaves your machine.

```bash
dotnet build Squeezr.slnx
squeezr init --name my-project && squeezr add /path/to/repo
squeezr index && squeezr search "WorkspaceManager"
```

Windows install (self-contained exe + PATH, no .NET required): `pwsh -File scripts\install.ps1`. Uninstall: `scripts\uninstall.ps1`.

Status: M1–M4 done (CLI skeleton, trigram index, Roslyn symbols, hybrid semantic search + refs). See the roadmap above.
