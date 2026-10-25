# Тесты, установка и пакеты

Команды `test`, `install`, `package`, экспорт для CMake и pkg-config.

```csharp
public sealed class CoreTestsTarget : TargetRules
{
    public CoreTestsTarget(ModulesInfo info) { Modules.Add("CoreTests"); IsTest = true; TestArguments.Add("--quiet"); }
}
```

```powershell
beast test .                                   # собирает и запускает все IsTest-цели (Windows, Linux, macOS)
beast install . --prefix D:/Dist/Game          # Release по умолчанию; --config, --platform, --arch
beast package . --platform Android --symbols   # Binaries/Packages/Game-Android-Release.zip
```

- `test` выполняет EXE в `Binaries/<platform>/<config>` (рабочий каталог — он же) с `TestArguments`;
  код выхода 0 — успех, `testTimeoutSeconds` (600) — предел. Итоговый код команды — 1 при любой
  ошибке. ARM64-тесты запускаются только на ARM64-хосте.
- `test --platform Android`: тест собирается как исполняемый файл (`-pie`, без APK) для ABI
  подключённого устройства (`--device <serial>` или `--avd <name>`), копируется через `adb` в
  `/data/local/tmp/beast/<Target>` вместе с `libc++_shared.so` и `.so` целей-зависимостей и
  запускается с `LD_LIBRARY_PATH`; код выхода передаётся обратно.
- `install` собирает цели (все, кроме тестов, или `--target A,B`) и раскладывает: `bin/` — программы,
  DLL, `.app`-бандлы, runtime-файлы (`RuntimeDependencies`, DLL пакетов, ASan); `lib/` — LIB, import
  library, `.a`, `.so`, `.dylib` (`lib/<abi>/` для Android); `include/` — публичные заголовки модулей
  библиотечных целей, с сохранением путей внутри `PublicIncludePaths`; APK Android-приложения — в
  корень. `--symbols` добавляет PDB (Windows) и `.dSYM` (macOS). Что копировать, берётся из `Intermediate/Build/.../<target>/Install.json`,
  который пишет каждая успешная сборка.
- `package` = `install` во временный каталог `Intermediate/Package/...` + zip. `--output` задаёт файл.
- Для каждой библиотечной цели `install` пишет пакет CMake `lib/cmake/<Target>/<Target>Config.cmake`
  (импортированная цель `<Target>::<Target>`) и, кроме Windows, `lib/pkgconfig/<Target>.pc`. Оба
  файла перемещаемы (пути считаются от них самих) и содержат путь к библиотеке (у DLL — и import
  library), `include/`, стандарт C++, `PublicDefinitions` и макросы `MODULE_API` в виде для
  потребителя (`dllimport` у Windows DLL, пусто в остальных случаях). У статической библиотеки
  добавляются её внешние библиотеки и цели-зависимости (`find_dependency`/`Requires`). Версия —
  `TargetRules.Version` (`<Target>ConfigVersion.cmake`: совместима та же мажорная версия). Android:
  CMake выбирает `lib/${CMAKE_ANDROID_ARCH_ABI}`, pkg-config — переменную `abi`
  (`--define-variable=abi=x86_64`). Конфигурация одна — та, что установлена (по умолчанию Release).

```cmake
find_package(Engine 1.2 CONFIG REQUIRED)            # -DCMAKE_PREFIX_PATH=<prefix>
target_link_libraries(app PRIVATE Engine::Engine)
```

```bash
PKG_CONFIG_PATH=<prefix>/lib/pkgconfig c++ main.cpp $(pkg-config --cflags --libs Engine)
```

---
[Оглавление](index.md)
