# Граф сборки

Как Beast планирует и исполняет сборку и как посмотреть граф.

## Один граф на сборку

`build` планирует всё, что собирает этот запуск: цель, её цели-зависимости (`TargetDependencies`),
программы-инструменты шагов (`Executable = "target:<Имя>"`) и все платформы/ABI (несколько ABI
Android) — и исполняет это **одним графом действий**. Между целями нет барьеров:

- компиляция потребителя ждёт только то, что она читает: сгенерированные заголовки целей-зависимостей
  (выходы их `BuildSteps` с `AffectsCompilation` — входы компиляции, а не только порядок по каталогу);
- линковка потребителя ждёт продукты целей, которые она линкует: архив или библиотеку импорта и саму
  DLL/`.so` (ребро порядка);
- шаг, запускающий инструмент, ждёт линковку этого инструмента и перезапускается, когда тот меняется;
- один и тот же файл, который копируют рядом с продуктами две цели (runtime-DLL пакета), — одно действие;
  разные источники для одного назначения — ошибка.

Поэтому приложение компилируется параллельно с линковкой библиотеки, от которой зависит, а ошибка в
одной цели не мешает собрать всё независимое (`--stop-on-error` — прежнее поведение).

Трассы: `Intermediate/Build/<platform>/<config>/<target>/BuildTrace.json` — действия этой цели,
`BuildGraph.json` в каталоге запрошенной цели — весь граф (статус, причина запуска или пропуска,
длительность, входы, выходы, зависимости каждого действия).

## `beast graph`

```bash
beast graph [проект] [--target <цель>] [--platform ...] [--config ...] [--option NAME=VALUE]
            [--format json|dot] [--output <файл>]
```

Строит тот же план, что и `build`, но **ничего не запускает**. JSON (по умолчанию) — в stdout (сообщения
сборщика при этом идут в stderr) или в `--output`:

```json
{
  "targets": ["Linux/BeastEngine", "Linux/Engine"],
  "actions": [
    { "id": "a3", "target": "Linux/Engine", "type": "Compile", "description": "Compiling Main.cpp for Linux...",
      "outputs": ["..."], "inputs": ["..."], "dependencies": ["a0"] }
  ],
  "depth": 3
}
```

`depth` — длина критического пути в действиях (минимальное число последовательных шагов). `--format dot`
выводит Graphviz с кластером на цель: `beast graph --format dot --output graph.dot && dot -Tsvg graph.dot -o graph.svg`.

## Почему действие запустилось

Каждое действие сравнивается по содержимому: команда, компилятор, окружение, объявленные входы и
заголовки из прошлого запуска компилятора. Причина записывается в `reason` трассы: `content and outputs
unchanged`, `compiler, arguments, environment or declared inputs changed`, `no valid previous action state`
и т. д. Ключ шага генерации включает всё окружение процесса; имена, отличающиеся регистром (`HTTPS_PROXY`
и `https_proxy` на Linux/macOS), упорядочены детерминированно, поэтому шаг без изменений не перезапускается.

## Ненайденный заголовок

Если компилятор не нашёл `#include`, Beast ищет его в публичных include-путях всех модулей, в `Source/` и
`includeRoots` всех проектов и подсказывает, какой модуль его даёт и что добавить:

```text
'mathlib/mathlib.h' is provided by module 'mathlib' (.../Source/ThirdParty/mathlib/include/mathlib/mathlib.h).
Add PrivateDependencies.Add("mathlib") (PublicDependencies if 'Renderer' exposes it in its public headers) to .../Renderer.Build.cs.
```

Это ускоряет перенос правил, где все модули видели настройки друг друга (первый Beast BuildTool, legacy-режим).

---
[Оглавление](index.md)
