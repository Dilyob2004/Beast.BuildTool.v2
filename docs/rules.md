# Модули, цели и правила сборки

Правила на C#: `*.Build.cs` (модули) и `*.Target.cs` (цели), опции компилятора, конфигурации, зависимости.

## Модули

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
ошибки. `AndroidAssets`, `AndroidNativeLibraries` и `Resources` модулей целей-зависимостей (в том
числе целей внешних проектов) попадают в APK и `Binaries` приложения.

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

#### Внешние проекты

Цели и модули другого проекта Beast (например, SDK в git submodule) подключаются в
`Config/BuildConfig.json` приложения:

```json
"externalProjects": [
  { "name": "Beast", "path": "../../BeastSDK" }
]
```

`path` — относительно корня проекта или абсолютный; раскрываются `%VAR%`, `$VAR`, `${VAR}` и `~`.
Внешний проект — обычный проект Beast со своими `Source/**/*.Build.cs` и `Source/*.Target.cs`
(и, при необходимости, своими `externalProjects` — они подключаются тоже). Его цели доступны в
`TargetDependencies`, модули — в `PublicDependencies`/`PrivateDependencies`:

```csharp
// Source/EcoSbor.Target.cs
public sealed class EcoSborTarget : TargetRules {
    public EcoSborTarget(ModulesInfo info) { Modules.Add("Launch"); TargetDependencies.Add("Beast"); }
}
// Source/Launch/Launch.Build.cs
PrivateDependencies.Add("UI");   // модуль внешнего проекта
```

- Имена целей и модулей общие для всех проектов: совпадение — ошибка с путями обоих файлов.
- Пути в правилах внешнего модуля (`PublicIncludePaths`, `BuildSteps`, `RuntimeDependencies`,
  `Resources`, `{ModuleDir}`, `{ProjectDir}`) отсчитываются от внешнего проекта; внутри его правил
  `info.ProjectDirectory`/`info.ProjectSourceDirectory` указывают на него, `info.ExternalProjectName` —
  его имя, `info.BuildProjectDirectory` — собираемое приложение.
- `Source/` внешнего проекта добавляется в include-пути его модулей и их потребителей
  (`#include "Core/Public/SdkCore.h"`); `Source/` приложения внешним модулям не виден.
- Промежуточные файлы внешней цели пишутся в `Intermediate/Build/<platform>/<config>/External/<name>/`
  приложения, библиотека — в его `Binaries`. Дерево внешнего проекта остаётся чистым, поэтому
  несколько приложений собираются с одним SDK параллельно. Инкрементальность и кеш работают как для
  своих целей: правка заголовка SDK перекомпилирует нужное в SDK и в приложении.
- `AndroidAssets`, `AndroidNativeLibraries`, `Resources`, `RuntimeDependencies` модулей внешней цели
  попадают в APK / `Binaries` приложения.
- `clean` очищает и копию сборки внешнего проекта; `clean --target Beast` — только её.
- Цель по умолчанию, `install`/`test` без `--target` и VS Code берут только цели самого проекта;
  `generate` добавляет в решение Visual Studio (и проект Xcode) цели внешних проектов, от которых
  зависит приложение, а их исходники — в папку `External\<name>` Solution Explorer; IntelliSense и
  `compile_commands.json` включают исходники внешнего проекта.
- Настройки сборки (`Config/BuildConfig.json`: компиляторы, Android, vcpkg) берутся из приложения;
  из конфигурации внешнего проекта читается только его `externalProjects`.

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

---
[Оглавление](index.md)
