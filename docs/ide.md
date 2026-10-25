# IDE и редакторы

Visual Studio, VS Code, CLion и clangd.

## Visual Studio

`generate` создаёт для каждой цели конфигурации `Debug|x64`, `Development|x64`, `Release|x64`;
`Debug|ARM64` и т. д. — если установлены ARM64-инструменты; `Android Debug|x64` и т. д. — если найден
NDK. Android-конфигурации собирают все ABI, их IntelliSense видит определения и заголовки NDK; F5 у
Android-приложения выполняет `run --no-build --wait` (установка и запуск на устройстве/эмуляторе).
Файлы чужой платформы (`Private/Android/...` в Windows-конфигурации) помечены `ExcludedFromBuild`.

IntelliSense модулей C++20: интерфейсы помечены `CompileAsCppModule`, внутренние разделы —
`CompileAsCppModuleInternalPartition` (см. [Модули C++20](cpp-modules.md)).

## VS Code, CLion, clangd

После каждой сборки в корне проекта обновляется `compile_commands.json` — точные команды компилятора
для каждого файла (включая `-fmodule-file=` модулей C++20 и команды файлов, вошедших в Unity-блоки).
clangd (расширение VS Code `clangd`), CLion и clang-tidy читают его сами; для расширения Microsoft C/C++
укажите `"C_Cpp.default.compileCommands": "${workspaceFolder}/compile_commands.json"`. Отключение:
`"generateCompileCommands": false` в `Config/BuildConfig.json`.

Сборку из VS Code удобно вызывать задачей (`.vscode/tasks.json`):

```json
{ "version": "2.0.0", "tasks": [ { "label": "beast build", "type": "shell",
  "command": "beast build ${workspaceFolder} --config Debug", "group": "build", "problemMatcher": "$gcc" } ] }
```

---
[Оглавление](index.md)
