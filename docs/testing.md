# Проверки и CI

Тесты Beast, замеры, GitHub Actions и выпуск версий.

## Запуск проверок

Быстрый запуск без ввода команд (двойной щелчок или из `cmd`):

```bat
Build.bat                         :: собрать инструмент в BuildTool\bin
Tests\RunTests.bat                :: сборка + BuildGraphTests, ModelTests + Smoke, Targets, CompilationModes, BuildSteps, Features (Windows)
Tests\RunTests.bat android        :: то же с Android-частями (NDK, Gradle, подпись APK)
Tests\RunTests.bat all            :: + Acceleration, Hardening, AndroidPackaging
Tests\RunTests.bat bench          :: замеры исполнителя на синтетическом проекте
Tests\RunTests.bat all "D:\Android\Sdk"   :: SDK явно (иначе ANDROID_HOME, затем %LOCALAPPDATA%\Android\Sdk)
```

На Linux и macOS:

```bash
bash Build.sh                     # собрать инструмент в BuildTool/bin
bash Tests/Unix.sh                # настоящие сборки: цели, .so/.dylib, PCH, Unity, .S, модули C++20, LTO,
                                  # инкрементальность, run/test, install/package, CMake/pkg-config,
                                  # .app, universal и .dmg (macOS), iOS Simulator (macOS с Xcode),
                                  # санитайзеры, GCC, arm64 через qemu и import std (Linux), шаблоны new
bash Tests/Unix.sh --android      # + APK этого проекта (ANDROID_HOME, NDK, JAVA_HOME)
bash Tests/AndroidDevice.sh       # на запущенном эмуляторе/устройстве: run шаблона (logcat) и test
dotnet run --project Tests/ModelTests/ModelTests.csproj -c Release
```

`RunTests.bat` вызывает `.ps1`-скрипты через `pwsh -ExecutionPolicy Bypass` (политику выполнения
менять не нужно), продолжает после упавшего шага и в конце печатает сводку; код выхода — число
упавших шагов. Нужен PowerShell 7 (`winget install Microsoft.PowerShell`): фикстура BuildSteps
использует `pwsh` как генератор.

## Замеры

Проверка режимов и отдельное сравнение полной сборки, no-op и изменения одного
`.cpp` (кеш артефактов в сравнении отключён):

```powershell
pwsh -File Tests/CompilationModes.ps1 -Android -AndroidSdk 'C:\Users\you\AppData\Local\Android\Sdk'
pwsh -File Tests/OptimizationBenchmark.ps1 -Dataset Synthetic -Units 128 -Repeats 2
pwsh -File Tests/OptimizationBenchmark.ps1 -Dataset Box2D -Repeats 2
pwsh -File Tests/OptimizationBenchmark.ps1 -Platform Android -AndroidSdk 'C:\Users\you\AppData\Local\Android\Sdk'
```

Отчёт `Comparison.json`, логи и трассы остаются в `Tests/Generated/Optimizations-*`.
Повторения чередуют порядок режимов; «cold» означает пустой каталог результатов
Beast, не очистку файлового кеша ОС. Синтетический STL-тест отдельно помечен и
не выдаётся за производственный проект. Unity для стороннего кода может потребовать
своего списка исключений; скрипт сообщает ошибку компилятора, а не скрывает её.

Результаты замеров и ограничения сравнения: [Tests/Benchmarks.md](https://github.com/Dilyob2004/Beast.BuildTool.v2/blob/main/Tests/Benchmarks.md).

Замеры исполнителя на копии Box2D из локального прототипа и отдельной синтетической
нагрузке (исходники прототипа не изменяются):

```powershell
pwsh -File Tests/Benchmark.ps1 -Dataset Box2D -Jobs 8
pwsh -File Tests/Benchmark.ps1 -Dataset NativeLibraries -Jobs 8
pwsh -File Tests/Benchmark.ps1 -Dataset Synthetic -Units 256 -Jobs 8
```

Каждый запуск создаёт отдельный проект в `Tests/Generated`, сохраняет `Benchmark.json`,
логи и трассы: чистая сборка без кеша Beast, три no-op запуска, изменение общего
заголовка с сохранением timestamp и восстановление объектов из кеша после `clean`.
Проверяется число пересобранных потребителей заголовка. «Чистая» не означает сброс
дискового кеша Windows. Синтетический тест не заменяет замер большого реального приложения.
Исполнитель запускает инструменты напрямую, а архиваторы получают списки объектов
через response-файлы. Готовые задачи планируются очередью по зависимостям.
Общие входы кеша хешируются один раз внутри запуска и повторно проверяются перед
успешным завершением графа. Между запусками хеши не переиспользуются; изменения
содержимого с сохранением timestamp обнаруживаются. Результаты компилятора
по-прежнему проходят отдельную проверку входов перед публикацией в кеше.
Инвентарь include-каталогов тоже переиспользуется внутри запуска и повторно
проверяется в конце; новые/удалённые файлы по-прежнему учитываются.

```powershell
.\Tests\Smoke.ps1
# Дополнительно: собрать и проверить подпись Android APK (без установки):
.\Tests\Smoke.ps1 -Android
.\Tests\Targets.ps1 -Android
.\Tests\Acceleration.ps1 -AndroidSdk 'C:\Users\you\AppData\Local\Android\Sdk'
.\Tests\Hardening.ps1 -AndroidSdk 'C:\Users\you\AppData\Local\Android\Sdk'
dotnet run --project Tests/BuildGraphTests/BuildGraphTests.csproj -c Release
# Если SDK не находится по умолчанию:
.\Tests\Targets.ps1 -Android -AndroidSdk 'C:\Users\you\AppData\Local\Android\Sdk'
```

Тесты проверяют новый проект, модули, include paths, Debug/Release, инкрементальную
пересборку заголовка, ошибки компиляции и отсутствие связей с SDK. Результаты
остаются в `Tests/Generated`.

`ModelTests` (`dotnet run --project Tests/ModelTests/ModelTests.csproj`) проверяет модель без
компилятора, поэтому работает на любом хосте: платформенные папки и `ExcludedSources`, зависимости
целей и их ошибки, `MODULE_API`, наследование опций, LTO/санитайзеры, `migrate`, генерацию Visual
Studio, граф модулей C++20, файлы CMake/pkg-config и блокировки. `Features.ps1` (Windows) собирает и
запускает: DLL-цель с `dllimport`, LTO, `run`, `test`, `install`/`package`, потребителя CMake, ASan,
MASM, модули C++20 и — если установлены — ARM64, clang-cl, vcpkg, Conan (`-SkipPackages` пропускает
пакеты, им нужна сеть). `.github/workflows/beast.yml` запускает это в GitHub Actions на Windows,
Linux (с APK, собранным на Linux) и macOS.

`Targets.ps1` также проверяет EXE/LIB/DLL, отсутствие обязательного Launch,
транзитивные public/private-настройки, циклы/неизвестные зависимости, одинаковые
имена исходников, отсутствие лишней линковки и устаревший timestamp C# правил.

## GitHub Actions

`.github/workflows/beast.yml` запускается при каждом push и pull request (и вручную):

| Задача | Что проверяет |
| --- | --- |
| `windows` | сборка инструмента, BuildGraphTests, ModelTests, `Smoke`, `Targets`, `CompilationModes`, `Features` (MSVC, clang-cl, ASan, модули, `import std`, CMake) |
| `linux` | `Unix.sh --android`: Clang, GCC, arm64 через qemu, `import std`, CMake/pkg-config, APK на NDK |
| `android` | эмулятор x86_64 (KVM): `AndroidDevice.sh` — установка и запуск шаблона, тесты на устройстве |
| `macos` | `Unix.sh`: Apple clang, `.app`, universal, `.dmg`, iOS Simulator |

При падении скрипты пишут причину и конец лога в аннотации GitHub (видны на странице прогона), в
конце — сводку (`::notice::`: сколько групп прошло и что пропущено). Папка `Tests/Generated` с логами
прикладывается к упавшей задаче как артефакт.

Запуск вручную: вкладка **Actions** → workflow → **Run workflow**, или из терминала (GitHub CLI):

```bash
gh workflow run beast.yml                    # проверки
gh workflow run release.yml                  # пробная сборка архивов (без публикации)
gh run watch                                 # следить за прогоном
gh run view --log-failed                     # лог упавших шагов
```

## Выпуск версии

`.github/workflows/release.yml` на тег `v*` собирает самодостаточные архивы (`win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`), проверяет на них `new`/`build`/`run` и публикует
релиз с `SHA256SUMS.txt`:

```bash
git tag v2.1.0
git push origin v2.1.0
```

---
[Оглавление](index.md)
