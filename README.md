# Beast BuildTool

Самостоятельная система сборки C/C++ с правилами на C#. Собирает программы и библиотеки для
Windows, Linux, macOS, iOS и Android, сама исполняет граф сборки (без CMake, Ninja и Make) и
генерирует решения Visual Studio и `compile_commands.json`.

[![Beast BuildTool](https://github.com/Dilyob2004/Beast.BuildTool.v2/actions/workflows/beast.yml/badge.svg)](https://github.com/Dilyob2004/Beast.BuildTool.v2/actions/workflows/beast.yml)

## Возможности

- **Правила на C#**: модули `Source/<Module>/<Module>.Build.cs` и цели `Source/<Name>.Target.cs`;
  опции компилятора на уровне цели и модуля, платформенные папки, `ExcludedSources`.
- **Цели**: приложения, статические и динамические библиотеки; публичные/приватные зависимости
  модулей, зависимости между целями (`TargetDependencies`) с макросами экспорта `MODULE_API`.
- **Windows x64 и ARM64**: `cl.exe` или `clang-cl`, `link.exe`, Windows SDK, `.rc`, иконка и
  DPI-манифест, MASM; AddressSanitizer (MSVC и clang-cl), LTO.
- **Linux x86_64 и arm64**: Clang (libstdc++ или libc++) или GCC, `.so` с `RUNPATH $ORIGIN`, `.S`,
  санитайзеры, LTO; кросс-сборка arm64 с запуском `run`/`test` через qemu-user.
- **macOS arm64, x86_64 и universal**: Apple clang, `.dylib` с `@rpath`, `.app` бандлы, `.dSYM`,
  Objective-C/C++ (`.m`, `.mm`), фреймворки, подпись, `.dmg` и нотаризация.
- **iOS и iOS Simulator**: `.app` со встроенными `.dylib`, подпись (ad-hoc в симуляторе, сертификат и
  provisioning-профиль для устройства), `.ipa`, `run` и `test` в симуляторе.
- **Android `arm64-v8a` и `x86_64`** в одном APK: NDK Clang, Gradle из `BuildConfig.json`, подпись
  Debug/Release, несколько приложений в проекте, запуск на эмуляторе или устройстве, тесты через `adb`.
- **Модули C++20**: `.ixx`/`.cppm`, разделы, `import` между целями, `import std` (MSVC, Clang, GCC).
- **Шаблоны `new`**: консольная программа (Windows, Linux), NativeActivity (Android), AppKit (macOS),
  UIKit (iOS).
- **Команды** `build`, `run`, `test`, `install`, `package`, `generate`, `migrate`, `clean`; пакеты
  vcpkg и Conan 2; `install` пишет пакеты CMake (`find_package`) и pkg-config.
- **Инкрементальная сборка**: граф действий, контентный кеш, зависимости заголовков от компилятора,
  продолжение после ошибок, общий кеш и worker.
- **IDE**: `.sln`/`.vcxproj` с IntelliSense и обычным отладчиком Visual Studio; `compile_commands.json`
  для clangd, VS Code и CLion.
- **Хосты**: Windows, Linux, macOS; готовые сборки Beast без установки .NET.

## Быстрый старт

Скачайте архив для своей системы со страницы
[Releases](https://github.com/Dilyob2004/Beast.BuildTool.v2/releases), распакуйте и добавьте папку в
`PATH` (.NET устанавливать не нужно). Нужен компилятор платформы: Visual Studio C++ (Windows),
`clang` или `g++` (Linux), Xcode или Command Line Tools (macOS), Android NDK (Android).

```bash
beast new HelloApp --path Projects
beast build Projects/HelloApp                 # платформа этого компьютера, Debug
beast run Projects/HelloApp
beast build Projects/HelloApp --platform Android --config Release
```

Правила проекта — обычный C#:

```csharp
// Source/Core/Core.Build.cs — модуль
using Beast.BuildTool;

public sealed class Core : ModuleRules
{
    public Core(ModulesInfo info)
    {
        PublicIncludePaths.Add("Public");
        if (info.Platform == PlatformType.Windows) InputLibraries.Add("user32.lib");
    }
}

// Source/Game.Target.cs — цель
public sealed class GameTarget : TargetRules
{
    public GameTarget(ModulesInfo info) { Modules.Add("Launch"); TargetDependencies.Add("Engine"); }
}
```

Сборка из исходников: .NET 8 SDK и `Build.bat` (Windows) или `bash Build.sh` (Linux, macOS).

## Документация

| Раздел | О чём |
| --- | --- |
| [Установка и быстрый старт](docs/install.md) | готовые сборки, сборка из исходников, первый проект |
| [Модули, цели и правила](docs/rules.md) | `*.Build.cs`, `*.Target.cs`, опции компилятора, конфигурации, зависимости, `MODULE_API` |
| [Платформы](docs/platforms.md) | Windows, Linux, macOS (universal, `.dmg`), iOS, Android, санитайзеры, LTO |
| [Модули C++20](docs/cpp-modules.md) | `.ixx`/`.cppm`, разделы, `import std`, IntelliSense |
| [PCH и Unity Build](docs/compilation.md) | предкомпилированные заголовки и объединение единиц |
| [Генерация кода и runtime-файлы](docs/build-steps.md) | шаги сборки, файлы рядом с программой и внутри APK |
| [Тесты, установка и пакеты](docs/packaging.md) | `test`, `install`, `package`, CMake и pkg-config |
| [vcpkg и Conan](docs/packages.md) | зависимости из пакетных менеджеров |
| [IDE и редакторы](docs/ide.md) | Visual Studio, VS Code, CLion, clangd |
| [Ускорение сборки](docs/acceleration.md) | кеш, общий кеш, worker |
| [Проверки и CI](docs/testing.md) | тесты Beast, GitHub Actions, выпуск версий |

Правила `.Build.cs`/`.Target.cs` выполняют C# код: собирайте только доверенные проекты.

## Лицензия

MIT — см. [LICENSE](LICENSE). Сторонние NuGet-пакеты и Gradle wrapper сохраняют
собственные лицензии; Apache-2.0 для Gradle находится в
`BuildTool/Templates/Android/Project/gradle/wrapper/LICENSE`. SDK/NDK, Java и
Visual Studio устанавливаются отдельно.
`Prototypes/` — локальные справочные материалы, исключённые из Git. Они не
входят в дистрибутив и не перелицензируются под MIT этого проекта.
