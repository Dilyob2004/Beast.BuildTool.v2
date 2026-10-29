# Генерация кода, ресурсы и runtime-файлы

Шаги генерации, ресурсы приложения (все платформы), копирование файлов рядом с продуктом и файлы
внутри APK.

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

## Ресурсы приложения (все платформы)

Текстуры, звуки, уровни, конфиги — одно объявление в `*.Build.cs`, без условий по платформе:

```csharp
Resources.Add("Assets");                         // каталог модуля Assets -> Assets/...
Resources.Add("Levels", "Assets/Levels");        // с другим назначением
Resources.Add("{GeneratedDir}/atlas.bin", "Assets/atlas.bin");   // результат BuildStep
```

Beast кладёт их туда, где их ищет каждая платформа (назначение — относительно корня ресурсов):

| Платформа | Корень ресурсов | `Assets/hero.png` оказывается в |
|---|---|---|
| Windows, Linux | каталог программы | `Binaries/<platform>/<config>/Assets/hero.png` |
| macOS, `.app` (`ConsoleApplication = false`) | `X.app/Contents/Resources` | `Game.app/Contents/Resources/Assets/hero.png` |
| macOS, консольная программа | каталог программы | рядом с программой |
| iOS | корень `X.app` | `Game.app/Assets/hero.png` (подпись бандла включает ресурсы) |
| Android | `assets/` APK | `assets/Assets/hero.png` |

Ресурсы может объявлять и модуль цели-зависимости (библиотеки): они попадают в каждое приложение,
которое от неё зависит. У самих библиотек ресурсы не копируются. Копирование инкрементально, как у
`RuntimeDependencies`: копируются только изменённые файлы, удалённые из правил — удаляются из
`Binaries`. `install`/`package` берут ресурсы вместе с программой (на macOS и iOS — внутри `.app`).

Чтение — заголовок `BeastResources.h`, который Beast добавляет в include path каждого модуля
(копия лежит в `Intermediate/Beast/Include`):

```cpp
#include "BeastResources.h"

std::vector<std::byte> image;
if (Beast::ReadResource("Assets/hero.png", image)) { /* ... */ }

std::string config;
Beast::ReadResource("Assets/config.json", config);          // текст

std::string path = Beast::ResourcePath("Assets/music.ogg"); // полный путь (кроме Android)
```

Пути — относительные, с `/`, в UTF-8 (на Windows работают и не-ASCII имена). На Android файлы читаются
из APK через `AAssetManager`: один раз при запуске вызовите
`Beast::SetAssetManager(activity->assetManager)` — шаблон `beast new` уже делает это; `ResourcePath`
там возвращает путь внутри APK. Старые `RuntimeDependencies` и `AndroidAssets` продолжают работать.

## Файлы внутри Android APK

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

---
[Оглавление](index.md)
