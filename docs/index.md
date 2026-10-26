# Документация Beast BuildTool

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
- **IDE**: `.sln`/`.vcxproj` с IntelliSense и отладчиком Visual Studio; проект Xcode (сборка, запуск
  под LLDB, iOS Simulator); `.vscode` (задачи, F5); `compile_commands.json` для clangd и CLion.
- **Хосты**: Windows, Linux, macOS; готовые сборки Beast без установки .NET.

## Разделы

1. [Установка и быстрый старт](install.md)
2. [Модули, цели и правила сборки](rules.md)
3. [Платформы](platforms.md)
4. [Модули C++20](cpp-modules.md)
5. [PCH и Unity Build](compilation.md)
6. [Генерация кода и runtime-файлы](build-steps.md)
7. [Тесты, установка и пакеты](packaging.md)
8. [vcpkg и Conan](packages.md)
9. [IDE и редакторы](ide.md)
10. [Ускорение сборки](acceleration.md)
11. [Проверки и CI](testing.md)

## Ограничения

- Каждая настольная платформа собирается на своём хосте: Windows — с MSVC, macOS и iOS — с Xcode;
  Linux на другом хосте — только Clang с `linux.sysroot`.
- Запуск программ другой архитектуры: x86_64 на Apple silicon (Rosetta), Linux arm64 через qemu-user;
  иначе `run`/`test` недоступны. Android-тесты требуют устройства или эмулятора.
- ASan несовместим со `StaticRuntime`; на Windows из санитайзеров — только ASan.
- Модули C++20: без header units; не поддерживаются clang-cl и Apple clang (для macOS/iOS можно задать
  LLVM из Homebrew); `import std` — MSVC и Clang с libc++.
- Генераторов Ninja/Make нет и не планируется: Beast сам исполняет граф сборки, редакторам хватает
  `compile_commands.json`.

[Репозиторий на GitHub](https://github.com/Dilyob2004/Beast.BuildTool.v2)
