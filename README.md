# Beast BuildTool — standalone

Самостоятельный инструмент сборки C/C++ без движка и UI-фреймворка. Отдельный
репозиторий: прежняя версия Beast BuildTool (с фреймворком), Beast SDK, его исходники,
шрифты и графические библиотеки не нужны.

## Возможности

- **Правила на C#**: модули `Source/<Module>/<Module>.Build.cs` и цели `Source/<Name>.Target.cs`;
  опции компилятора на уровне цели и модуля, платформенные папки, `ExcludedSources`.
- **Цели**: приложения, статические и динамические библиотеки; публичные/приватные зависимости
  модулей, зависимости между целями (`TargetDependencies`) с макросами экспорта `MODULE_API`.
- **Windows x64 и ARM64**: `cl.exe` или `clang-cl`, `link.exe`, Windows SDK, `.rc`, иконка и
  DPI-манифест, MASM; AddressSanitizer (MSVC и clang-cl), LTO.
- **Linux x86_64 и arm64**: Clang или GCC, `.so` с `RUNPATH $ORIGIN`, `.S`, санитайзеры, LTO.
- **macOS arm64 и x86_64**: Apple clang (Xcode или Command Line Tools), `.dylib` с `@rpath`, `.app`
  бандлы, `.dSYM`, Objective-C/C++ (`.m`, `.mm`), фреймворки, подпись `codesign`.
- **iOS и iOS Simulator**: `.app` с встроенными `.dylib`, `Info.plist`, подпись (ad-hoc в симуляторе,
  сертификат и provisioning-профиль для устройства), `.ipa`, `run` и `test` в симуляторе (`simctl`).
- **Android `arm64-v8a` и `x86_64`** в одном APK: NDK Clang, Gradle из `BuildConfig.json`, подпись
  Debug/Release, несколько приложений в проекте, запуск на эмуляторе или устройстве, тесты через `adb`.
- **Модули C++20**: `.ixx`/`.cppm`, разделы, единицы реализации, `import` между целями (MSVC, Clang, GCC).
- **Хосты**: Windows, Linux, macOS — каждый собирает свои цели и Android.
- **Команды** `test`, `install`, `package`, `migrate`; пакеты vcpkg (manifest mode) и Conan 2;
  `install` пишет пакеты CMake (`find_package`) и pkg-config для библиотек.
- **Инкрементальная сборка**: граф действий, контентный кеш, зависимости заголовков от компилятора,
  продолжение после ошибок; промежуточные файлы в `Intermediate/Build`, результаты в `Binaries`.
- **IDE**: `.sln`, `.vcxproj` и `.filters` с C++20 IntelliSense (x64, ARM64, Android-конфигурации),
  обычный Local Windows Debugger без расширений; `compile_commands.json` для clangd/VS Code/CLion.

Ограничения: каждая настольная платформа собирается на своём хосте (Windows — с MSVC, macOS — с
Xcode; Linux на другом хосте — только Clang с `linux.sysroot`); запуск программ другой архитектуры
(`run`, `test`) недоступен, кроме x86_64 на Apple silicon (Rosetta); Android-тесты требуют устройства
или AVD; ASan несовместим со `StaticRuntime`; на Windows из санитайзеров — только ASan; модули C++20 —
без header units и `import std`, и не с clang-cl.

Удалены автоматическая линковка SDK, RHI-шейдеры, UI-шаблоны, Android
IME/accessibility-адаптеры, команды `sdk`/`watch` и hot reload сцен. Сборщик не
навязывает сторонний runtime приложению. Android SDK/NDK и стандартная C++
библиотека, конечно, остаются платформенными зависимостями.

Генераторов Ninja/Make нет и не планируется: Beast сам исполняет граф сборки,
а редакторам хватает `compile_commands.json`.
Исполнитель использует граф действий, зависимости заголовков от MSVC/Clang,
контентный кеш и ограничения параллелизма по памяти. Есть общий файловый кеш
и экспериментальный worker для общей файловой системы. UBA не используется.

## Установка

Готовые сборки — на странице [Releases](https://github.com/Dilyob2004/Beast.BuildTool.v2/releases):
`beast-<версия>-<платформа>.zip`/`.tar.gz` для `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`osx-arm64`, `osx-x64`. Они самодостаточные: .NET устанавливать не нужно. Распакуйте архив и добавьте
папку в `PATH`; команда — `beast` (`beast.cmd` на Windows), проверка — `beast version`. Компилятор
платформы по-прежнему нужен (MSVC, Clang/GCC, Xcode, Android NDK). На macOS архив, скачанный
браузером, помечен карантином: `xattr -dr com.apple.quarantine beast-*`. Суммы SHA-256 — в
`SHA256SUMS.txt`.

Выпуск новой версии: `git tag v2.1.0 && git push origin v2.1.0` — workflow `Release` собирает все
платформы, проверяет `new`/`build`/`run` на Windows x64, Linux x64 и macOS и публикует релиз. Ручной
запуск (Actions → Release → Run workflow) только собирает архивы как артефакты.

## Быстрый старт

Из исходников (нужен .NET 8 SDK):

Нужны .NET 8 SDK (или новее с поддержкой .NET 8) и компилятор платформы: на Windows —
Visual Studio C++ Desktop (MSVC и Windows SDK), на Linux — `clang` (или `g++`), на macOS —
Xcode или `xcode-select --install`. Команды ниже запускаются из корня репозитория. Windows:

```powershell
dotnet build BuildTool/Beast.BuildTool.csproj -c Release
.\BuildTool\bin\Beast.BuildTool.exe new HelloApp --path Projects
.\BuildTool\bin\Beast.BuildTool.exe build Projects/HelloApp --platform Windows --config Debug
.\Projects\HelloApp\Binaries\Windows\Debug\HelloApp.exe
```

Linux и macOS (`bash Build.sh` собирает инструмент; платформа по умолчанию — сам хост):

```bash
bash Build.sh
BuildTool/bin/Beast.BuildTool new HelloApp --path Projects
BuildTool/bin/Beast.BuildTool build Projects/HelloApp            # Binaries/Linux/Debug или Binaries/Mac/Debug
BuildTool/bin/Beast.BuildTool run Projects/HelloApp
```

Шаблон для Windows, Linux и macOS печатает приветствие через обычный `main()`. Android-шаблон
реализует `ANativeActivity_onCreate` напрямую, выводит приветствие в logcat и
заливает окно тёмным цветом. Это минимальная заготовка, не UI-библиотека.

Генератор создаёт конфиг путей Java/Android SDK/NDK:
`Projects/HelloApp/Config/BuildConfig.json`. При необходимости настройте
`android.javaHome`, `android.sdkRoot`, `android.ndkRoot`, `android.ndkVersion`.
По умолчанию Android API 29/target 35; эти значения Gradle берёт из конфига
(см. «Android: Gradle из конфигурации»).

```powershell
.\BuildTool\bin\Beast.BuildTool.exe build Projects/HelloApp --platform Android --config Release
.\BuildTool\bin\Beast.BuildTool.exe run Projects/HelloApp --platform Android --config Release --no-build --wait
```

`run` собирает и запускает приложение платформы `--platform` (по умолчанию — `defaultPlatform`
конфига, на хосте без MSVC — сам хост); код выхода программы становится кодом команды. Раньше
`run` без `--platform` означал Android — теперь его нужно указать. Android запускается на
эмуляторе или устройстве: при нескольких устройствах укажите `--device emulator-5554`, для
выбора AVD — `--avd <name>`. `build --run` — то же после сборки.
Release подписывается локальным debug-ключом, если собственная подпись не задана;
для публикации задайте `android.releaseSigning` с путём keystore, alias и именами
переменных окружения `storePasswordEnv` / `keyPasswordEnv`. Сами пароли и ключи
не храните в Git. Полный .NET runtime нужен для запуска сборщика; на целевом
устройстве приложение не требует .NET.

## Модули и Visual Studio

```powershell
.\BuildTool\bin\Beast.BuildTool.exe module Core Projects/HelloApp
.\BuildTool\bin\Beast.BuildTool.exe generate Projects/HelloApp
```

Откройте `Projects/HelloApp/HelloApp.sln`. Build/Rebuild/Clean вызывают именно
эту копию сборщика; F5 запускает собранный Windows EXE. SDK-проекта в solution
нет. После перемещения самого инструмента выполните `generate` заново.

Из корня приложения путь проекта можно опустить (сборщик должен быть в PATH
или вызван по абсолютному пути). `Source` автоматически добавлен в include paths:

```cpp
#include "Core/Public/Core.h"
```

Настройки модуля:

```csharp
using Beast.BuildTool;

public sealed class Core : ModuleRules
{
    public Core(ModulesInfo info)
    {
        Definitions.Add("MY_FEATURE=1");
        PublicIncludePaths.Add(System.IO.Path.Combine(info.ProjectDirectory!, "ThirdParty", "include"));
        if (info.Platform == PlatformType.Windows)
            InputLibraries.Add("winmm.lib");
    }
}
```

### Платформенный код и исключения

Исходники в папках `Windows`, `Win64`, `Win32` компилируются только для Windows,
`Android` — только для Android, `Linux` — только для Linux, `Mac`, `MacOS`, `OSX` — только для
macOS, `IOS` — только для iOS (устройства и симулятора). Файлы `.m`/`.mm` (Objective-C/C++)
компилируются только для macOS и iOS, `.asm` — только для Windows, `.S`/`.s` — везде, кроме Windows. Учитываются имена папок на любом уровне внутри модуля
(`Private/Windows/Window.cpp`), имя файла роли не играет. Отключить соглашение для
модуля: `UsePlatformDirectories = false;`.

```csharp
ExcludedSources.Add("Private/Experimental");      // папка целиком
ExcludedSources.Add("Private/Legacy.cpp");        // один файл
ExcludedSources.Add("Private/**/*Tests.cpp");     // шаблон: *, ?, **
```

Пути считаются от каталога модуля; несуществующий файл или папка — ошибка
(защита от опечаток), шаблон может ничего не находить. Исключённые и чужие
платформенные файлы можно по-прежнему упоминать в `UnityExcludedFiles` и
`PrecompiledHeaderExcludedFiles`.

### Опции компилятора

`CompileOptions` задаётся в цели (значения по умолчанию для всех её модулей) и в
модуле (переопределение). Незаданное значение наследуется.

```csharp
// В *.Target.cs
CompileOptions.Warnings = WarningLevel.High;   // Off, Default, High, Pedantic
CompileOptions.WarningsAsErrors = true;
Definitions.Add("APP_VERSION=\"1.2\"");       // для всех модулей цели; кавычки экранируются
LinkerArguments.Add("/STACK:8388608");
StaticRuntime = true;                           // MSVC /MT, Android -static-libstdc++

// В *.Build.cs
CompileOptions.CppStandard = CppVersion.Cpp17;  // Cpp14, Cpp17, Cpp20, Latest
CompileOptions.CStandard = CLanguageStandard.C17;
CompileOptions.Exceptions = false;
CompileOptions.RTTI = false;
CompileOptions.FloatingPoint = FloatingPointModel.Fast; // Precise (по умолчанию), Fast, Strict
CompileOptions.Favor = FavorSizeOrSpeed.SmallCode;      // FastCode; Neither — по умолчанию
if (info.Platform == PlatformType.Windows) CompileOptions.Arguments.Add("/wd4005");
LinkerArguments.Add("...");                     // добавляется в каждую цель, линкующую модуль
```

| Опция | MSVC | Clang/NDK |
| --- | --- | --- |
| `WarningLevel.Off` / `Default` / `High` / `Pedantic` | `/W0` / `/W3` (C: `/W0`) / `/W4` / `/W4 /permissive-` | `-w` / — / `-Wall -Wextra` / `+ -Wpedantic` |
| `WarningsAsErrors` | `/WX` | `-Werror` |
| `Exceptions = false` | без `/EHsc`, `_HAS_EXCEPTIONS=0` | `-fno-exceptions` |
| `RTTI = false` | `/GR-` | `-fno-rtti` |
| `FloatingPoint` | `/fp:precise` / `fast` / `strict` | — / `-ffast-math` / `-ffp-model=strict` |
| `Favor = SmallCode` / `FastCode` (Development/Release) | `/Os` / `/Ot` | `-Os` / `-O3` (по умолчанию `-O2`) |
| `CppVersion.Latest` | `/std:c++latest` | `-std=c++2b` |

`Arguments` — сырые аргументы компилятора, по одному элементу на аргумент, в порядке
добавления (повторы сохраняются); они идут после опций Beast. Для разных компиляторов
задавайте их по `info.Platform`. Опции модуля участвуют в ключе кеша и IntelliSense.

Изменения поведения по сравнению с прежними версиями: `/fp:fast` больше не включён
по умолчанию (теперь `/fp:precise`), предупреждение C4005 больше не подавляется
глобально, `/d2archSSE42` удалён. Версию Windows SDK можно закрепить в
`Config/BuildConfig.json`: `"windows": { "sdkVersion": "10.0.26100.0" }`; без неё
берётся новейшая установленная.

### Конфигурации

| | Debug | Development | Release |
| --- | --- | --- | --- |
| Оптимизация | нет | да | да |
| `assert` (`NDEBUG`) | работает | работает | отключён |
| Макросы | `DEBUG`, `_DEBUG`, `BEAST_BUILD_DEBUG=1` | `BEAST_BUILD_DEVELOPMENT=1` | `NDEBUG`, `BEAST_BUILD_RELEASE=1` |
| Отладочная информация | PDB / `-g` | PDB / `-g` | PDB / `-g` (в APK — stripped копия) |
| Прочее (Windows) | debug CRT, `/RTC1`, инкрементальная линковка | `/OPT:REF,ICF` | `/OPT:REF,ICF` |

### Цели и зависимости

`new HelloApp` создаёт `Source/HelloApp.Target.cs`:

```csharp
using Beast.BuildTool;
public sealed class HelloAppTarget : TargetRules
{
    public HelloAppTarget(ModulesInfo info)
    {
        Type = TargetType.Application;
        Modules.Add("Launch");
    }
}
```

Для использования Core добавьте в конструктор `Launch.Build.cs`:

```csharp
PrivateDependencies.Add("Core");
```

`module Core` создаёт файлы, но не редактирует произвольный пользовательский C#:
связь необходимо объявить явно. `PublicDependencies` дополнительно передаёт
публичные настройки зависимости потребителям модуля. `Definitions` и
`PrivateIncludePaths` действуют только внутри владельца; `PublicDefinitions` и
`PublicIncludePaths` передаются потребителям. Относительные include-пути
считаются от папки соответствующего `.Build.cs`. `Source` доступен всем как
базовый include-путь — это удобство, а не ограничение доступа к заголовкам.

Каждый исходник принадлежит ближайшей папке с `.Build.cs`; вложенные модули
не компилируются повторно родительским модулем. `HeaderOnly` и `External` не
компилируют исходники. Неиспользуемые модули не собираются. C# правила всех
модулей при этом должны быть синтаксически корректны.

Для самостоятельной библиотеки создайте `Source/CoreLibrary.Target.cs`:

```csharp
using Beast.BuildTool;
public sealed class CoreLibraryTarget : TargetRules
{
    public CoreLibraryTarget(ModulesInfo info)
    {
        Type = TargetType.StaticLibrary; // либо SharedLibrary
        Modules.Add("Core");
    }
}
```

```powershell
.\BuildTool\bin\Beast.BuildTool.exe build Projects/HelloApp --target CoreLibrary --config Release
.\BuildTool\bin\Beast.BuildTool.exe generate Projects/HelloApp
```

Каждая цель получает собственный NMake-проект в solution; сборкой управляет Beast.
Windows: `.exe/.lib/.dll`; Android: `.a/.so`, а Application дополнительно упаковывается
в подписанный APK. Статическая цель
содержит объекты всех своих исходных зависимостей (без вложенных архивов);
готовые внешние библиотеки в неё не встраиваются, потребитель должен линковать их сам.
Промежуточные файлы изолированы по цели, конфигурации и модулю.

#### Зависимости между целями

Цель может использовать другую цель проекта (`StaticLibrary` или `SharedLibrary`):

```csharp
public sealed class EngineTarget : TargetRules
{
    public EngineTarget(ModulesInfo info) { Type = TargetType.SharedLibrary; Modules.Add("Core"); }
}
public sealed class HelloAppTarget : TargetRules
{
    public HelloAppTarget(ModulesInfo info) { Modules.Add("Launch"); TargetDependencies.Add("Engine"); }
}
```

`Launch.Build.cs` по-прежнему объявляет `PrivateDependencies.Add("Core")` — ради
include-путей и определений. `build --target HelloApp` сначала собирает `Engine`
(в том же запуске), затем `HelloApp`. Модули зависимой цели компилируются один раз,
её целью; приложение их не перекомпилирует и линкует результат: `Engine.lib`
(import library) рядом с `Engine.dll` на Windows, `libEngine.so` на Android (с
`-soname`; в APK он попадает автоматически, Release — stripped). Через статическую
библиотеку зависимости и внешние библиотеки её модулей передаются потребителю;
разделяемая библиотека содержит свои зависимости сама. Корневой модуль цели не может
одновременно собираться её зависимостью, цикл целей и зависимость от Application —
ошибки. `AndroidAssets`/`AndroidNativeLibraries` объявляйте в модулях самого приложения.

Макрос экспорта: для каждого модуля явной цели определяется `<ИМЯ>_API`
(`CORE_API`). Внутри `SharedLibrary` он равен `__declspec(dllexport)`, у потребителей —
`__declspec(dllimport)`; на Android — `__attribute__((visibility("default")))`; в
остальных случаях — пусто. Имя меняется через `ApiMacro = "MY_EXPORT";`, пустая
строка отключает макрос.

```cpp
class CORE_API Logger { public: void Write(const char* text); };
```

Если целей несколько, `build` выбирает совпадающую с именем проекта; иначе
требует `--target`. Имя Android Application пока должно совпадать с именем проекта,
так как Gradle/NativeActivity-шаблон рассчитан на одно приложение.
Visual Studio-конфигурации — Windows x64/ARM64 и Android (см. «Платформы, архитектуры и хосты»).
`clean` без параметров очищает `Intermediate/Build` всего проекта (дожидаясь
запущенных сборок), `clean --target <имя>` — только эту цель во всех платформах и
конфигурациях. Clean/Rebuild проекта в Visual Studio очищают только его цель.
Разные цели и конфигурации одного проекта собираются параллельно разными
процессами; одна и та же цель/конфигурация — последовательно. Ожидание чужой
сборки не ограничено по времени.

После планирования каждой сборки в корне проекта обновляется `compile_commands.json`
(clangd, VS Code, CLion, clang-tidy): записи текущей цели заменяют прежние записи тех же
файлов, файлы Unity получают команды своего блока. Копия для цели лежит в её каталоге
`Intermediate/Build/...`. Отключение: `"generateCompileCommands": false`.

Совместимость: без `.Target.cs` работает старый режим — необходим `Launch`,
все модули включаются автоматически, их настройки объединяются. Этот режим устарел,
сборка выводит предупреждение. `beast migrate <project>` создаёт `Source/<Project>.Target.cs`,
в котором все модули — корневые (поведение как раньше, но уже явная модель). После этого
объявите `PublicDependencies`/`PrivateDependencies` там, где компиляция сообщит о недостающих
заголовках, и перенесите экспортируемые определения из `Definitions` в `PublicDefinitions`.
Поля `DirectoryDependencies` и `ThirdPartyDependices` помечены `[Obsolete]`: Beast их никогда не
использовал; вместо них — зависимости модулей, `InputLibraries`, `VcpkgLibraries` или Conan.

Список команд: `new`, `module`, `generate`, `migrate`, `build`, `run`, `test`, `install`,
`package`, `clean`, `help` (`help` печатает все опции).
Ошибка компиляции больше не останавливает независимые действия: сборка доводит до
конца всё, что не зависит от упавших действий, и в конце перечисляет все ошибки.
Прежнее поведение — `build ... --stop-on-error` или `"stopOnFirstError": true`.
Неизвестные опции командной строки выводятся предупреждением.
Правила `.Build.cs` выполняют C# код: собирайте только доверенные проекты.

## Платформы, архитектуры и хосты

```powershell
beast build . --platform Linux                          # Binaries/Linux/<config> (arm64: LinuxArm64)
beast build . --platform Mac                            # Binaries/Mac/<config> (Intel: --arch x64, MacX64)
beast build . --platform IOSSimulator                   # Binaries/IOSSimulator/<config> (Intel Mac: IOSSimulatorX64)
beast build . --platform IOS                            # Binaries/IOS/<config>: устройство, нужна подпись
beast build . --platform Windows --arch arm64          # Binaries/WindowsArm64/<config>
beast build . --platform Android                        # все ABI из android.abis в один APK
beast build . --platform Android --arch x86_64          # только x86_64 (эмулятор)
```

- Windows ARM64 использует `bin/Hostx64/arm64` (или `HostARM64/arm64` на ARM64-хосте), библиотеки
  `lib/arm64` и SDK `um|ucrt/arm64`. Нужен компонент Visual Studio «MSVC ARM64 build tools».
  Каталоги — `WindowsArm64`, у x64 по-прежнему `Windows`.
- `"windows": { "compiler": "clang-cl" }` компилирует C/C++ через LLVM из Visual Studio
  (`VC/Tools/Llvm`) или `windows.llvmRoot`. Зависимости заголовков — Makefile-depfile, линковка —
  `link.exe`, с LTO — `lld-link`/`llvm-lib`. AddressSanitizer поддерживается (кроме `StaticRuntime`).
- Android: `"android": { "abis": ["arm64-v8a", "x86_64"] }`. Каждая ABI собирается отдельно
  (`Intermediate/Build/Android-x86_64/...`, `Binaries/Android-x86_64/...`; arm64 сохраняет имя
  `Android`), правила получают `info.Architecture`. APK один, `Binaries/Android/<config>`.
  Каталоги ABI, которые больше не собираются, удаляются из `jniLibs`.
- Платформа по умолчанию — `defaultPlatform` конфига; значение `"Host"` (или пустое) — сам хост.
  На хосте без MSVC `"Windows"` заменяется хостом (Linux/Mac). Visual Studio-файлы генерируются
  только на Windows; `compile_commands.json` — везде.
- Android на Linux/macOS: NDK `prebuilt/linux-x86_64` / `darwin-x86_64`, `gradlew`, `adb`, `emulator`
  без `.exe`; SDK по умолчанию из `ANDROID_HOME`/`ANDROID_SDK_ROOT` или стандартного пути Android
  Studio, Java — `JAVA_HOME` или JBR Android Studio.
- Ассемблер: `.asm` — MASM (`ml64`, на ARM64 `armasm64`), только Windows; `.S` (с препроцессором и
  depfile) и `.s` — Clang/GCC, на Android, Linux и macOS. Файлы ассемблера не входят в Unity/PCH и не попадают
  в compile cache; в `.asm` передаются только простые определения `NAME`/`NAME=value`.
- `run --device <serial>` принимает и физическое устройство; эмулятор нужен только для запуска AVD.

### iOS

```json
"ios": { "deploymentTarget": "15.0", "bundleIdentifier": "", "signingIdentity": "", "provisioningProfile": "",
         "simulator": "", "device": "", "cxx": "" }
```

- Нужен Xcode (в Command Line Tools нет iOS SDK). `--platform IOSSimulator` — симулятор (arm64, на Intel
  Mac `--arch x64`), `--platform IOS` — устройства (arm64). Компилятор и SDK `iphonesimulator`/`iphoneos`
  находятся через `xcrun`, цель — `--target=arm64-apple-ios<deploymentTarget>[-simulator]`.
- Каждая Application-цель — бандл `Binaries/<платформа>/<config>/<Target>.app` (плоская структура iOS:
  `<Target>.app/<Target>`, `Info.plist` с `bundleIdentifier` или `com.beast.<target>`, версией из
  `TargetRules.Version`, `MinimumOSVersion`, `UILaunchScreen`). `.dylib` целей-зависимостей и пакетов
  встраиваются в `<Target>.app/Frameworks` (программа ищет их через `@executable_path/Frameworks`).
  Программа линкуется в `Intermediate/.../Program`, затем копируется в бандл и подписывается — так
  подпись не ломает инкрементальность. Фреймворки: `Frameworks.Add("UIKit");`.
- Подпись: симулятор — ad-hoc; устройство — `signingIdentity` (`security find-identity -v -p codesigning`)
  и `provisioningProfile` (`.mobileprovision` для этого bundle identifier): профиль кладётся в бандл как
  `embedded.mobileprovision`, его entitlements передаются `codesign`. Без них сборка для устройства —
  ошибка с подсказкой.
- `run --platform IOSSimulator` загружает симулятор (`ios.simulator` — имя или UDID, иначе запущенный
  или первый iPhone), устанавливает и запускает приложение, показывая его вывод. `run --platform IOS
  --device <имя>` — установка и запуск на устройстве через `xcrun devicectl` (Xcode 15+).
- `test --platform IOSSimulator`: тестовые цели — обычные исполняемые файлы, выполняются в симуляторе
  через `simctl spawn`, код выхода передаётся обратно.
- `install`/`package` копируют `<Target>.app`, а для устройства пишут ещё и `<Target>.ipa` (`Payload/`).
- Шаблон `new` — консольная `main()`: приложение печатает приветствие и завершается. Интерфейс
  (UIKit/SwiftUI) пишется в `.mm`-файлах модуля. Модули C++20 — как на macOS: Apple clang их не умеет,
  `ios.cxx` задаёт другой Clang.

### Linux

```json
"linux": { "compiler": "", "cxx": "", "ar": "", "linker": "", "sysroot": "" }
```

- `compiler`: `"clang"`, `"gcc"` или пусто — Clang, если `clang++` (или новейший `clang++-N`) есть в
  `PATH`, иначе `g++`. `cxx` задаёт драйвер явно (`/usr/bin/clang++-18`, `aarch64-linux-gnu-g++`).
  Архиватор — `llvm-ar` (Clang) или `gcc-ar` (GCC, нужен для LTO), иначе `ar`; `ar` — явно.
- Программы и `.so` получают `RUNPATH $ORIGIN:$ORIGIN/../lib`: зависимости находятся рядом (в
  `Binaries`) и в `lib/` префикса установки без `LD_LIBRARY_PATH`. У `.so` есть `SONAME`,
  неопределённые символы — ошибка (`--no-undefined`, кроме сборок с санитайзерами). Библиотеки
  целей и пакетов линкуются группой (`--start-group`), системные (`-pthread`, `dl`, `m`) — после.
- LTO: Clang — ThinLTO через `lld` (`ld.lld` в `PATH`) или `"linker": "gold"`; GCC — `-flto=auto`.
  `linker` передаётся как `-fuse-ld=` (`lld`, `gold`, `mold`).
- Другая архитектура (`--arch arm64` на x86_64) или не-Linux хост: Clang с `--target=<triple>` и
  `sysroot`, либо кросс-GCC `<triple>-g++`. Такие программы `run`/`test` не запускают.
- Отладочная информация (`-g`) остаётся в самих файлах во всех конфигурациях.

### macOS

```json
"mac": { "deploymentTarget": "11.0", "sdkRoot": "", "cxx": "", "bundleIdentifier": "", "signingIdentity": "" }
```

- Компилятор, `libtool`, `dsymutil` и SDK находятся через `xcrun` (Xcode или Command Line Tools);
  `sdkRoot` задаёт SDK явно, `deploymentTarget` — `-mmacosx-version-min`, `cxx` — другой Clang
  (например, Homebrew LLVM для модулей C++20) с тем же SDK. Архитектура по умолчанию —
  архитектура Mac; `--arch x64` собирает Intel-версию в `MacX64` (она запускается и через Rosetta 2).
- `.dylib` называются `@rpath/lib<Name>.dylib`, программы и библиотеки ищут их в `@loader_path` и
  `@loader_path/../lib`. Development/Release получают `.dSYM` рядом с продуктом (`dsymutil`), Debug
  отлаживается по объектным файлам. `StaticRuntime` на macOS недоступен (libc++ — часть системы).
- Фреймворки: `Frameworks.Add("Cocoa");` в `*.Build.cs` → `-framework Cocoa`. `.m`/`.mm` — с ARC.
- Приложение с `ConsoleApplication = false` собирается как бандл `Binaries/Mac/<config>/<Target>.app`
  (`Contents/MacOS/<Target>`, `Info.plist` с `bundleIdentifier` или `com.beast.<target>`, версией из
  `TargetRules.Version` и `LSMinimumSystemVersion`); `run` запускает его программу. Бандл находит
  `.dylib` в `Contents/Frameworks`, рядом с собой и в `lib/` префикса. Сборка не переподписывает
  бандл (подписи линкера хватает для запуска на этом Mac); `install` подписывает установленный бандл
  `codesign` — ad-hoc или `signingIdentity` (тогда с hardened runtime и вместе с установленными
  `.dylib`). Нотаризация и упаковка `.dmg` не выполняются.

### Санитайзеры и LTO

```csharp
// *.Target.cs
if (info.Configuration == TargetConfiguration.Debug) Sanitizers = Sanitizer.Address; // Windows: только Address
LinkTimeOptimization = true; // Development/Release: MSVC /GL + /LTCG (lib /LTCG), Clang -flto=thin
```

- MSVC: `/fsanitize=address`, без `/RTC1` и инкрементальной линковки; `clang_rt.asan*_dynamic-*.dll`
  копируется рядом с EXE/DLL. Нужен компонент «C++ AddressSanitizer».
- clang-cl: `-fsanitize=address`, рантайм `clang_rt.asan_dynamic` и thunk из каталога LLVM
  (`lib/clang/<версия>/lib/windows`), DLL копируется рядом с EXE. Со `StaticRuntime` — ошибка.
- Android: `-fsanitize=...`; UBSan без ASan — минимальный статический рантайм. С ASan в APK
  кладутся `libclang_rt.asan-<arch>-android.so` и `wrap.sh`, включается `useLegacyPackaging`.
  `wrap.sh` работает только в debuggable APK (Debug); в других конфигурациях выводится предупреждение.
- Linux и macOS: `-fsanitize=address`, `undefined`, `thread` (Thread несовместим с Address); рантайм
  компилятора (на Linux — пакет `libclang-rt-dev` для Clang, `libasan`/`libubsan` для GCC).
- iOS: Address и Undefined (в симуляторе). Thread на Windows, Android и iOS, Memory везде
  отклоняются с ошибкой.

### Android: Gradle из конфигурации

`Config/BuildConfig.json` — единственный источник значений Gradle. Beast передаёт их init-скриптом в
`androidComponents.finalizeDsl`, после вычисления `app/build.gradle` и до фиксации DSL; сам
`build.gradle` не меняется:

```json
"android": {
  "minSdk": 29, "targetSdk": 35, "compileSdk": 0,
  "applicationId": "com.company.game", "versionCode": 12, "versionName": "1.2",
  "abis": ["arm64-v8a", "x86_64"]
}
```

`compileSdk: 0` берёт `targetSdk`; пустые `applicationId`/`versionName` и `versionCode: 0` оставляют
значения Gradle. `run` запускает активность по `applicationId`.

Несколько приложений: первая Application-цель (не тест) по имени проекта — главная, она использует
`Platforms/Android` и `applicationId` из конфига. Каждая другая Application-цель получает свой
Gradle-проект `Platforms/Android-<Target>` (создаётся из шаблона при первой сборке), пакет
`com.beast.<target>` и APK `Binaries/Android/<config>/<Target>.apk`; `run --target <Target>`
запускает именно её. Сборки разных приложений не блокируют друг друга. Gradle и подпись пропускаются, если не
изменились файлы `Platforms/Android` (кроме `build`, `.gradle`), подготовленные assets/`.so`,
init-скрипт, настройки подписи, Java/SDK и сам Beast, а APK в `Binaries` совпадает с записанным
хешем. Удалённый вручную APK собирается заново.

### Visual Studio

`generate` создаёт для каждой цели конфигурации `Debug|x64`, `Development|x64`, `Release|x64`;
`Debug|ARM64` и т. д. — если установлены ARM64-инструменты; `Android Debug|x64` и т. д. — если найден
NDK. Android-конфигурации собирают все ABI, их IntelliSense видит определения и заголовки NDK; F5 у
Android-приложения выполняет `run --no-build --wait` (установка и запуск на устройстве/эмуляторе).
Файлы чужой платформы (`Private/Android/...` в Windows-конфигурации) помечены `ExcludedFromBuild`.

## Тесты, установка и пакеты

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

## Модули C++20

Интерфейсы модулей — файлы `.ixx` или `.cppm` в каталоге модуля (`Source/<Module>/`), как обычные
исходники; Beast сам определяет порядок сборки по объявлениям `export module`, `module` и `import`.

```cpp
// Source/Geometry/Geometry.cppm — основной интерфейс
module;                       // глобальный фрагмент: #include до объявления модуля
#include <cstdio>
export module Geometry;
export import :Shapes;        // раздел-интерфейс (Shapes.cppm: export module Geometry:Shapes;)
import :Detail;               // внутренний раздел (Detail.cpp: module Geometry:Detail;)
export int Area(int w, int h);

// Source/Geometry/Area.cpp — единица реализации
module Geometry;
int Area(int w, int h) { return w * h; }

// Source/Launch/Main.cpp — потребитель (Launch: PrivateDependencies.Add("Geometry"))
import Geometry;
```

- Компиляторы: MSVC (`/interface`, `/internalPartition`, `/ifcOutput`, `/reference`), Clang 16+
  (`-fmodule-output`, `-fmodule-file=`; в том числе Android NDK), GCC 11+ (`-fmodules-ts` и файл
  module mapper). Не поддерживаются clang-cl и Apple clang (в нём нет именованных модулей): на macOS
  для модулей задайте LLVM из Homebrew — `"mac": { "cxx": "/opt/homebrew/opt/llvm/bin/clang++" }`.
- BMI (`.ifc`/`.pcm`/`.gcm`) пишутся в `Intermediate/Build/.../<target>/Modules/`. Модуль, собранный
  целью-зависимостью (`TargetDependencies`), импортируется из её каталога; его объекты линкуются из её
  библиотеки. Каждой единице передаются BMI всех модулей, которые она импортирует, транзитивно.
- Импортировать можно модули своего каталога-модуля и его `PublicDependencies`/`PrivateDependencies`
  (с их публичными зависимостями); иначе ошибка с подсказкой, какую зависимость добавить. Также
  ошибки: неизвестный модуль, два объявления одного модуля, `.ixx`/`.cppm` без объявления модуля.
  `.ixx`/`.cppm` — только для `export module` (интерфейсы и разделы-интерфейсы); внутренние разделы
  (`module X:P;`) и единицы реализации (`module X;`) — в `.cpp`: MSVC иначе отказывается (C7622).
- Изменение интерфейса пересобирает импортирующие единицы; изменение единицы реализации — только её.
  Единицы модулей не входят в Unity и не используют PCH.
- Не поддерживаются: header units (`import <vector>;`), `import std;`, модули в старом режиме без
  `*.Target.cs`. Сканер не выполняет препроцессор: `import` внутри `#if` учитывается всегда.
  IntelliSense Visual Studio не получает `/reference`, поэтому может подчёркивать `import`.

## vcpkg и Conan

Если в корне проекта есть `vcpkg.json` (manifest mode) или `conanfile.txt`/`conanfile.py`, перед сборкой
зависимости устанавливаются в `Intermediate/vcpkg` и `Intermediate/conan/<platform>-<config>`.
Повторная установка — только при изменении манифеста, `vcpkg-configuration.json`/`conan.lock`,
триплета/профиля или самого инструмента. Заголовки пакетов видны всем модулям как внешние
(`/external:I`, `-isystem`); то же теперь относится к `PublicSystemIncludePaths` и
`PrivateSystemIncludePaths` модулей.

```csharp
VcpkgLibraries.Add("fmt");   // fmt.lib / fmtd.lib (Debug), libfmt.a или libfmt.so; можно имя файла
```

```json
"vcpkg": { "root": "", "triplet": "", "arguments": [] },
"conan": { "executable": "", "profile": "", "arguments": [] }
```

- vcpkg: `vcpkg.root`, затем `VCPKG_ROOT`, затем копия Visual Studio (`VC/vcpkg`). Триплет по умолчанию:
  `x64-windows`/`arm64-windows` (`-static`, если у какой-либо цели `StaticRuntime`), `arm64-android`,
  `x64-android`; для Android выставляется `ANDROID_NDK_HOME`. Debug берёт `debug/lib`, `debug/bin`.
  DLL из `bin` копируются рядом с EXE/DLL; `.so` из `VcpkgLibraries` попадают в APK.
- Conan 2: `conan install --format=json`, библиотеки всех host-пакетов (включая компоненты) линкуются
  автоматически, `system_libs` и `defines` применяются. Windows: профиль по умолчанию плюс
  `build_type`, `compiler.runtime(_type)`, `arch=armv8` для ARM64. Android: профиль генерируется
  (API = `minSdk`, ABI, версия NDK clang, `c++_shared`/`c++_static`, `tools.android:ndk_path`), либо
  задайте `conan.profile`. Development/Release используют `build_type=Release`.
- Установщики выполняются по одному на проект (блокировка), IDE-генерация берёт пути из последней
  установки.

## Ускорение сборки

Настройки находятся в `Config/BuildConfig.json`, секция `acceleration`
(регистр имён JSON не важен):

```json
{
  "maxParallelJobs": 8,
  "acceleration": {
    "cacheEnabled": true,
    "cacheDirectory": "Intermediate/Cache",
    "sharedCacheDirectory": "",
    "portableClangCache": false,
    "localCacheLimitMB": 4096,
    "memoryReserveMB": 1024,
    "compileMemoryMB": 512,
    "linkMemoryMB": 1024,
    "maxMemoryMB": 0,
    "workerQueueDirectory": "",
    "workerKeyEnvironment": "BEAST_WORKER_KEY",
    "workerTimeoutSeconds": 120,
    "workerRetryCount": 1
  }
}
```

- SHA-256 исходников, фактически использованных заголовков, параметров,
  исполняемых файлов/DLL инструментов и результатов заменяет проверку только по времени.
  Входы линкера дополнительно учитывают содержимое библиотек в поисковых каталогах.
- Состояние include-поиска учитывает только реально использованные заголовки: для
  каждого написания, под которым заголовок достижим из include-каталога, проверяется,
  существует ли файл с тем же написанием в каждом include-каталоге и каталоге
  включающего файла. Новый `.cpp`, новый неиспользуемый заголовок или `.Build.cs` в
  `Source` больше не пересобирают проект; заголовок, который перекрыл бы используемый,
  пересобирает его потребителей. Если исходник или заголовок проекта использует
  `__has_include`, для этого действия сохраняется прежний консервативный учёт всех
  файлов include-каталогов (без `.c/.cpp/.cs` и объектных файлов): компилятор не
  сообщает о неудачных поисках. Формат кеша — `v4`; старые записи `v3` не читаются и
  первыми удаляются квотой. Макросы времени отключают повторное использование
  для обнаруженных файлов; произвольные плагины компилятора/скрытые внешние входы
  пока не являются поддерживаемым герметичным сценарием кеширования.
- `clean` удаляет `Intermediate/Build`, но сохраняет `Intermediate/Cache`.
  Кеш сохраняет только компиляцию; линковка, `.rc`, Gradle и подпись остаются локальными.
  Повреждённые артефакты кеша не используются. Запись публикуется атомарно.
- MSVC использует `/Z7`: отладочная информация встроена в `.obj`, итоговый PDB
  создаётся линкером. Отдельного compiler-PDB, привязанного к машине worker, нет.
- `maxMemoryMB: 0` — бюджет из доступной физической памяти за вычетом резерва.
  Размеры compile/link — оценки для допуска задач, **не жёсткие лимиты памяти ОС**.
  При недостаточном бюджете сборщик выдаёт ошибку, а не запускает заведомо тесную сборку.
  No-op и восстановление из кеша проверяются до допуска компилятора по памяти.
  В пределах одного запуска оценка для инструмента/типа действия повышается до
  наблюдавшегося пика памяти процесса плюс 25%, но не опускается ниже настроенного
  значения. Это не сумма памяти дерева процессов и не гарантия отсутствия OOM.
- Локальный stdout/stderr выводится во время компиляции, включая предупреждения
  успешных команд. В памяти сохраняется до 4 МиБ каждого потока, затем вывод
  продолжает читаться и показываться. Worker показывает live-вывод у себя;
  координатор получает его сохранённую часть после завершения задания.
- `.rc` учитывает вложенные literal-include, существующие literal-пути к ресурсам
  и файлы каталога ресурсов. Неопределимые макро-include/операнды стандартных
  файловых ресурсов отключают повторное использование `.res`. Это консервативный
  сканер, не полноценный препроцессор произвольного RC-синтаксиса.
- `BuildTrace.json` в папке цели содержит зависимости, длительности, `skipped`,
  `cacheHit`, `remote`, `peakMemoryMB` и `reason` — причину пересборки или повторного использования.
  `executionMs` показывает время дочернего процесса (для worker — ожидание удалённого
  задания), `bookkeepingMs` — остальное время действия: хеширование, кеш, проверку
  результатов. Действия идут параллельно: сумма длительностей не равна времени сборки.
  Хеширование имеет стоимость: на маленьких проектах кеш
  и worker могут оказаться медленнее обычной компиляции. Универсальное ускорение
  без замеров на реальном проекте не обещается.

### Общий кеш и worker

`sharedCacheDirectory` может указывать на общую папку/SMB-share. Все писатели
должны быть доверенными: хеши обнаруживают повреждения, но не заменяют доверие к
поставщику бинарников. Ограничьте доступ правами файловой системы. Кеш пока
MSVC и обычный кеш привязаны к абсолютным путям. Индекс наборов зависимостей
позволяет искать текущую версию входов без перебора всех прошлых ревизий заголовков.
Старый кеш без индекса остаётся читаемым; повреждённые записи не используются.
Небольшие метаданные индекса не входят в квоту и могут сохраняться после удаления артефакта.
Локальный кеш ограничен `localCacheLimitMB` (по умолчанию 4096 МБ; `0` отключает
ограничение). После выполнения графа удаляются старейшие завершённые записи до
достижения лимита; это порядок создания, не LRU. Проверяются только записи Beast,
посторонние файлы не удаляются и не входят в квоту. Во время сборки лимит может
временно превышаться. Очисткой общего кеша управляет его администратор;
если локальный и общий пути совпадают, автоматическая очистка отключена.

Для Clang/Android доступен opt-in `portableClangCache: true` вместе с
`sharedCacheDirectory`: пути проекта и NDK нормализуются, `.o` переносится без
изменений, а `.d` восстанавливается под текущий каталог. Компилятор получает
`-ffile-prefix-map` и `-fdebug-compilation-dir`; `__FILE__` и отладочные пути
используют `/__beast_project` и `/__beast_ndk`, поэтому отладчику может понадобиться
source mapping. Идентичность инструментов и содержимого зависимостей проверяется.
Внешние зависимости вне этих корней остаются привязаны к своим путям;
path-valued `PreprocessorDefinitions` исключают действие из переносимого кеша.
Это не переносимый кеш MSVC и не перенос исходников для worker.

Для распределённой компиляции все машины должны видеть **один общий проект**
по одинаковому абсолютному пути и иметь одинаковые компиляторы/SDK по одинаковым
путям. Worker не загружает исходники или SDK: он проверяет доступные ему файлы и
возвращает результаты через общую файловую систему. Укажите общую очередь в
`workerQueueDirectory`. На обеих машинах задайте одинаковый случайный секрет
не короче 32 байт через `BEAST_WORKER_KEY`; не храните его в репозитории.

Пример запуска отдельного процесса на машине worker:

```powershell
Beast.BuildTool.exe worker --queue 'Z:\BeastQueue' --project-root 'Z:\Projects' --compiler-root 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC'
# Для NDK используйте другой процесс с --compiler-root, указывающим на каталог NDK.
```

Каждый worker выполняет одну компиляцию одновременно. Можно запустить несколько
worker-процессов/машин на общей очереди. Worker пропускает подписанные задания,
чьи компилятор или проект не входят в его `--compiler-root`/`--project-root`,
не забирая их у подходящего worker. Worker
проверяет подпись HMAC, допустимый компилятор/проект и совпадение входов/инструментов.
Общая папка должна быть приватной; HMAC не шифрует пути и параметры. Нет открытого
HTTP-порта, автоматической установки службы или подключения чужих компьютеров.
На ненадёжной сети используйте защищённую SMB-конфигурацию/VPN.

Временные результаты размещаются в `Intermediate/Worker/<job-id>`. Они попадают
в сборку только после проверки ответа. При таймауте задание повторяется до
`workerRetryCount` раз (0–10, по умолчанию 1), каждый раз с новым ID и staging.
Поздний ответ старой попытки не принимается; компилятор не пишет в итоговые файлы.
Ошибки компиляции/подписи не повторяются. После исчерпания попыток сборка завершается
ошибкой; автоматического локального fallback нет. Windows-компилятор привязывается
к Job Object с завершением при закрытии handle: после привязки смерть worker
останавливает дочерний процесс. Между запуском процесса и привязкой есть короткое
окно; это не suspended-process launcher и не security sandbox. Очередь и staging
сохраняются для диагностики; очищайте их только после остановки worker-процессов.
Поддерживаются `--once`, `--idle-timeout <seconds>`, `--reserve-mb <MB>`.
Это первая shared-filesystem реализация, не аналог всех возможностей UBA:
автопереноса зависимостей, удалённой линковки, обнаружения машин и sandbox worker нет.
Секрет даёт право запускать компилятор с аргументами: выдавайте его только доверенным разработчикам.

## Проверки

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
                                  # .app (macOS), iOS Simulator (macOS с Xcode), санитайзеры, GCC (Linux)
bash Tests/Unix.sh --android      # + APK этого проекта (ANDROID_HOME, NDK, JAVA_HOME)
dotnet run --project Tests/ModelTests/ModelTests.csproj -c Release
```

`RunTests.bat` вызывает `.ps1`-скрипты через `pwsh -ExecutionPolicy Bypass` (политику выполнения
менять не нужно), продолжает после упавшего шага и в конце печатает сводку; код выхода — число
упавших шагов. Нужен PowerShell 7 (`winget install Microsoft.PowerShell`): фикстура BuildSteps
использует `pwsh` как генератор.

### PCH и Unity Build

Настройки задаются для каждого модуля в его `*.Build.cs`; по умолчанию оба режима
выключены, в том числе для существующих проектов:

```csharp
public sealed class Core : ModuleRules
{
    public Core(ModulesInfo info)
    {
        PrecompiledHeaderSource = "Public/Pch.h";
        UseUnityBuild = true;
        UnityBatchSize = 8; // от 2 до 64 исходников в одном блоке
        UnityExcludedFiles.Add("Private/PlatformSpecific.cpp");
        PrecompiledHeaderExcludedFiles.Add("Private/CustomMacros.cpp");
    }
}
```

Пути считаются от каталога модуля; исключения — точные имена принадлежащих ему
исходников, не glob. PCH — **заголовок**, не `.cpp`: например, `#pragma once`,
`#include <vector>` и другие общие стабильные include. Он автоматически включается
в начало C++ единиц компиляции. Макросы, необходимые заголовку, задавайте через
правила модуля, а не в `.cpp` перед include. Явные include в исходниках полезно
сохранять: это облегчает работу IntelliSense и проверку сборки без PCH.

- MSVC: `/Yc`, `/Yu`, `/FI`, `/Fp`; отдельный PCH-объект входит в линковку/архив,
  в том числе для Debug `/Z7`. Clang/NDK: `-x c++-header`, `-include-pch`.
- PCH создаётся отдельно для модуля, платформы, конфигурации и цели. Реальные
  зависимости сообщает компилятор; PCH — явный предшественник его потребителей.
  Смена настроек или содержимого заголовков учитывается по хешам, а не только mtime.
- `.pch` и использующие его действия **локальные**: не отправляются worker и не
  сохраняются в кеш артефактов, включая portable Clang cache. No-op работает.
  Ключ их состояния включает имя машины. Это намеренное ограничение первой версии:
  MSVC рекомендует использовать PCH только на создавшей его машине
  ([документация Microsoft](https://learn.microsoft.com/en-us/cpp/build/creating-precompiled-header-files)).
- Unity формирует детерминированные блоки из отсортированных исходников **одного
  модуля**. Содержимое генерируется в `Intermediate/Build/.../Generated`, файлы
  пользователя не меняются. Изменение одного `.cpp` пересобирает его блок;
  добавление/удаление исходника может изменить состав нескольких блоков.
- C-файлы всегда отдельные, без C++ PCH. Исключения PCH также не объединяются в
  Unity, чтобы не смешивать режимы в одном блоке. `UnityExcludedFiles` отключает
  только Unity: такой C++ файл продолжает использовать PCH, если не исключён отдельно.
- Unity меняет единицу трансляции: static/anonymous-namespace имена могут конфликтовать,
  макросы и pragmas могут влиять на следующий файл. Автоматической изоляции или
  скрытого fallback нет. Исключайте конфликтующие файлы и регулярно проверяйте
  обычную сборку; меньше процессов не гарантирует ускорения на каждом проекте.
- Старые сгенерированные файлы могут остаться до `clean`, но не входят в текущий
  граф/список объектов. Отключение режимов не подмешивает старые объекты в продукт.

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

Результаты замеров и ограничения сравнения: [Tests/Benchmarks.md](Tests/Benchmarks.md).

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

## Генерация кода и runtime-файлы

В конструкторе модуля `*.Build.cs` можно объявить локальные шаги генерации:

```csharp
var generate = new BuildStep { Name = "Schema", Executable = "pwsh" };
generate.Arguments.AddRange(new[] {
    "-NoProfile", "-File", "{ModuleDir}/Tools/Generate.ps1",
    "{ModuleDir}/Schema.json", "{GeneratedDir}"
});
generate.Inputs.AddRange(new[] { "Tools/Generate.ps1", "Schema.json" });
generate.Outputs.AddRange(new[] { "Schema.h", "Schema.cpp" });
BuildSteps.Add(generate);
GeneratedSources.Add("Schema.cpp");

RuntimeDependencies.Add("ThirdParty/Library.dll"); // Binaries/<platform>/<config>/Library.dll
RuntimeDependencies.Add("Assets", "Assets");     // рекурсивно, с сохранением структуры
RuntimeDependencies.Add("Config/default.json", "Config/default.json");
```

Скрипт `Generate.ps1` в этом примере должен создать оба объявленных файла.
Это API запуска генераторов, а не встроенный компилятор схем или шейдеров.

- `Inputs` — файлы относительно каталога модуля; объявляйте также скрипт,
  шаблоны и все прочитанные им файлы. Автоматического обнаружения зависимостей
  произвольного генератора нет. Исполняемый файл учитывается автоматически.
- `Outputs` и `GeneratedSources` — относительно `{GeneratedDir}`. Выходы
  разрешены только внутри `Intermediate/Build/<platform>/<config>/<target>/Generated/<module>/Custom`.
  Каждый генерируемый C/C++ исходник необходимо добавить в `GeneratedSources`.
  Этот каталог автоматически входит в include-пути модуля и видимых потребителей,
  включая настройки IntelliSense. `generate` не запускает генераторы: файлы
  появятся при `build`, а не в дереве исходников Visual Studio.
- Доступны подстановки `{ProjectDir}`, `{ModuleDir}`, `{GeneratedDir}`,
  `{BuildDir}`, `{BinaryDir}`, `{Platform}`, `{Configuration}`, `{TargetName}`.
  Их можно использовать в аргументах, путях шагов, include/PCH-настройках,
  значениях окружения и источниках `RuntimeDependencies`.
- `Executable` — программа или её имя в `PATH`. Аргументы передаются отдельными
  значениями, без shell expansion. Для PowerShell/Python указывайте интерпретатор
  явно; `.ps1`/`.cmd`/`.bat` не являются исполняемой программой шага.
- `WorkingDirectory` по умолчанию — каталог модуля; другой существующий каталог
  задаётся явно. `Environment["NAME"] = "value"` меняет переменную дочернего
  процесса, `null` удаляет её. Окружение участвует в ключе состояния.
- Имена шагов уникальны внутри модуля. Связи между шагами определяются по
  совпадению входных и выходных файлов. Циклы, отсутствующие входы и конфликты
  выходов/файлов состояния проверяются до выполнения команд.
- По умолчанию `AffectsCompilation = true`: выходы шага становятся входами
  компиляции модуля и его зависимых модулей. Для генерации только runtime-данных
  задавайте `false`. Генераторы внутри include-каталогов всё равно завершаются
  до проверки инвентаря этих каталогов, но изменение неиспользуемых данных
  само по себе не требует пересборки C++.
- Состояние проверяется по содержимому, параметрам и инструменту, не только
  timestamp. `AlwaysRun = true` отключает пропуск шага. Перед повторным запуском
  удаляются его объявленные выходы: ошибка или отсутствие любого выхода
  не должны маскироваться старыми файлами. PCH и Unity поддерживают эти исходники.
  Шаги и копирование выполняются локально, не через worker/кеш объектов компиляции.

`RuntimeDependencies` копирует файлы рядом с продуктом сборки, относительные
назначения отсчитываются от `Binaries/<platform>/<config>`. Можно указать
генерируемый файл через `{GeneratedDir}`: копирование дождётся производителя.
Изменённые или повреждённые файлы копируются снова; неизменённые пропускаются.
Одинаковое назначение разных источников, выход за разрешённый каталог и
symlink/junction в выходном пути отклоняются. Рекурсивное копирование каталогов
не следует по ссылкам. Пустые каталоги не создаются. Скопированные файлы записываются в
`Intermediate/Build/.../<target>/RuntimeFiles.txt`: если зависимость убрана из правил, при
следующей сборке её файл удаляется из `Binaries` (кроме файлов, которые объявляет другая цель).

На Android `RuntimeDependencies` доставляет файлы в `Binaries/Android/<config>`,
**не добавляет внутрь APK**. Для упаковки используйте отдельные объявления ниже.
Правила и генераторы должны быть доверенными: проверки объявленных путей не
являются песочницей и не ограничивают произвольный код запущенного скрипта.

Проверка генераторов и копирования:

```powershell
pwsh -File Tests/BuildSteps.ps1 -Android -AndroidSdk 'C:/Users/you/AppData/Local/Android/Sdk'
```

### Файлы внутри Android APK

В `*.Build.cs`:

```csharp
if (info.Platform == PlatformType.Android)
{
    AndroidAssets.Add("Assets", "Data");
    AndroidAssets.Add("{GeneratedDir}/settings.json", "Config/settings.json");
    AndroidNativeLibraries.Add("ThirdParty/Android/libCodec.so");
}
```

`AndroidAssets` принимает файл или существующий каталог (рекурсивно).
Назначение — относительно `assets/` внутри APK; без назначения используется
имя исходного файла/каталога. Сгенерированные файлы объявляйте по одному:
их копирование включено в граф и ждёт соответствующий `BuildStep`. Каталоги
перечисляются до запуска генераторов, поэтому вновь создаваемые генератором
файлы нельзя обнаружить таким перечислением в текущей сборке.

`AndroidNativeLibraries` принимает отдельные `.so` текущей ABI (правила вычисляются для каждой ABI:
выбирайте файл по `info.Architecture`). При необходимости второй аргумент задаёт имя `libName.so`.
Проверяется ELF64, little-endian, машина ABI (AArch64/x86-64), тип shared object; это не проверка всех зависимостей
библиотеки или её совместимости с минимальной версией Android. Имена библиотеки
приложения и `libc++_shared.so` зарезервированы. Объявление добавляет библиотеку
в APK, **но не в линковку**: для линковки используйте также `InputLibraries`.
Все необходимые сторонние `.so` следует объявлять явно.

Эти коллекции учитываются только при сборке Android `Application`. Пути источников
считаются от модуля и поддерживают те же подстановки, что `BuildSteps`. Конфликты
назначений между объявлениями и выходы `..` за каталог отклоняются. Обычные
ресурсы `res/`, Java/Kotlin и manifest по-прежнему настраиваются в Gradle-проекте.

Упаковочные копии находятся в `Intermediate/Build/.../AndroidPackage/<layout-hash>`.
При смене набора объявленных файлов используется новый каталог: удалённые
зависимости больше не попадают в APK, а оригиналы не удаляются. Старые каталоги
остаются в `Intermediate` до `clean`. В Release дополнительные `.so` очищаются
от отладочных символов только в упаковочной копии; оригиналы сохраняются также
в `Binaries/Android/Release/Symbols/arm64-v8a` и перечисляются в `manifest.json`.

Сборщик подключает временный [Gradle init script](https://docs.gradle.org/current/userguide/init_scripts.html)
с дополнительными [assets/jniLibs source sets](https://developer.android.com/build/build-variants).
Пользовательский `app/build.gradle` не переписывается; существующие каталоги
Gradle остаются подключёнными. Поддерживается текущий шаблон с модулем `:app`,
без product flavors и split APK. Прямой запуск Gradle без Beast не подключает
его временные упаковочные каталоги. Инкрементальные копии не означают пропуск
Gradle и проверки подписи: упаковщик вызывается при каждом `build` приложения.

```powershell
pwsh -File Tests/AndroidPackaging.ps1 -AndroidSdk 'C:/Users/you/AppData/Local/Android/Sdk'
```

## Лицензия

MIT — см. [LICENSE](LICENSE). Сторонние NuGet-пакеты и Gradle wrapper сохраняют
собственные лицензии; Apache-2.0 для Gradle находится в
`BuildTool/Templates/Android/Project/gradle/wrapper/LICENSE`. SDK/NDK, Java и
Visual Studio устанавливаются отдельно.
`Prototypes/` — локальные справочные материалы, исключённые из Git. Они не
входят в дистрибутив и не перелицензируются под MIT этого проекта.
