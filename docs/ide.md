# IDE и редакторы

Visual Studio, Xcode, VS Code, CLion и clangd.

`beast generate [проект]` создаёт файлы IDE для текущей системы: решение Visual Studio (Windows),
проект Xcode (macOS) и папку `.vscode` (везде). `beast new` и `beast module` делают это сами.

## Visual Studio

`generate` создаёт для каждой цели конфигурации `Debug|x64`, `Development|x64`, `Release|x64`;
`Debug|ARM64` и т. д. — если установлены ARM64-инструменты; `Android Debug|x64` и т. д. — если найден
NDK. Android-конфигурации собирают все ABI, их IntelliSense видит определения и заголовки NDK; F5 у
Android-приложения выполняет `run --no-build --wait` (установка и запуск на устройстве/эмуляторе).
Файлы чужой платформы (`Private/Android/...` в Windows-конфигурации) помечены `ExcludedFromBuild`.

IntelliSense модулей C++20: интерфейсы помечены `CompileAsCppModule`, внутренние разделы —
`CompileAsCppModuleInternalPartition` (см. [Модули C++20](cpp-modules.md)).

## Xcode (macOS)

`generate` создаёт `<Проект>.xcodeproj` рядом с `Source`. Откройте его в Xcode:

- для каждой цели Beast есть цель и схема Xcode с тем же именем; **Product → Build (⌘B)** вызывает
  `beast build --target <цель> --config <конфигурация>`, ошибки компилятора кликабельны;
- **Product → Run (⌘R)** у приложения запускает `Binaries/Mac/Debug/...` под отладчиком LLDB
  (точки останова, просмотр переменных). Схема запускает Debug-сборку;
- для каждого приложения есть ещё цель **«<Имя> iOS Simulator»**: Build собирает его для симулятора,
  Run устанавливает и запускает в симуляторе (вывод программы — в консоли Xcode). Появляется, если
  установлен iOS SDK;
- **Product → Clean** вызывает `beast clean --target <цель>`;
- Debug, Development и Release — конфигурации Xcode (Product → Scheme → Edit Scheme).

Файлы `Source/` видны в навигаторе и редактируются как обычно; компилирует их Beast, а не Xcode, поэтому
настройки сборки (Build Settings) Xcode на сборку не влияют — всё задаётся в `*.Build.cs`/`*.Target.cs`.
После добавления модулей или файлов снова выполните `generate` (Xcode подхватит изменения сам).

## VS Code, CLion, clangd

После каждой сборки в корне проекта обновляется `compile_commands.json` — точные команды компилятора
для каждого файла (включая `-fmodule-file=` модулей C++20 и команды файлов, вошедших в Unity-блоки).
clangd (расширение VS Code `clangd`), CLion и clang-tidy читают его сами; для расширения Microsoft C/C++
укажите `"C_Cpp.default.compileCommands": "${workspaceFolder}/compile_commands.json"`. Отключение:
`"generateCompileCommands": false` в `Config/BuildConfig.json`.

`generate` создаёт в `.vscode` (только отсутствующие файлы — ваши правки не перезаписываются):

- `tasks.json` — задачи `beast: build Debug` (сборка по умолчанию, **Ctrl/⌘+Shift+B**), `build Release`,
  `run`, `test`, `clean`, `generate`; ошибки компилятора попадают в панель Problems;
- `launch.json` — отладка каждого приложения (**F5**) с предварительной сборкой Debug: Windows —
  отладчик Visual Studio (расширение C/C++), Linux — gdb (C/C++), macOS — расширение CodeLLDB;
- `settings.json` — путь к `compile_commands.json` для C/C++ и clangd.

Чтобы пересоздать файл, удалите его и выполните `generate`.

---
[Оглавление](index.md)
