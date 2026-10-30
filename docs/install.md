# Установка и быстрый старт

Готовые сборки Beast, сборка из исходников и первый проект.

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

Из исходников нужны .NET 10 SDK (минимальная версия; подойдёт и более новый) и компилятор платформы:
на Windows — Visual Studio C++ Desktop (MSVC и Windows SDK), на Linux — `clang` (или `g++`), на macOS —
Xcode или `xcode-select --install`.

### .NET 10 SDK

Скрипты в корне репозитория ставят SDK без прав администратора официальным установщиком Microsoft
(`dotnet-install`, канал 10.0) и прописывают его в `PATH`; если подходящий SDK уже есть, ничего не делают:

```bash
bash InstallDotnet.sh          # Linux, macOS: в ~/.dotnet, PATH и DOTNET_ROOT — в ~/.zshrc / ~/.bashrc
```

```powershell
powershell -ExecutionPolicy Bypass -File InstallDotnet.ps1   # Windows: в %LOCALAPPDATA%\Microsoft\dotnet
```

Параметры: `--dir`/`-Dir` — другая папка, `--force`/`-Force` — установить даже при наличии SDK,
`--no-profile`/`-NoPath` — не менять `PATH`. После установки откройте новый терминал: `dotnet --version`.
Другие способы — установщик с [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0),
`brew install --cask dotnet-sdk` (macOS), `winget install Microsoft.DotNet.SDK.10` (Windows).

`global.json` в корне требует SDK 10 или новее: со старым SDK сборка сразу сообщает, какая версия нужна.
`Build.sh`/`Build.bat` сами находят подходящий SDK (в `PATH`, `DOTNET_ROOT`, `~/.dotnet`,
`%LOCALAPPDATA%\Microsoft\dotnet`), даже если первым в `PATH` стоит старый.

### Первый проект

Команды ниже запускаются из корня репозитория. Windows:

```powershell
Build.bat
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

Шаблон `new` даёт для каждой платформы свою точку входа в модуле `Launch`:

| Платформа | Файл | Что делает |
| --- | --- | --- |
| Windows, Linux | `Launch.cpp` | консольная `main()` печатает приветствие |
| Android | `Launch.cpp` | `ANativeActivity_onCreate`: приветствие в logcat, окно заливается тёмным цветом |
| macOS | `Mac/LaunchMac.mm` | AppKit: `<Project>.app` с окном и надписью (`ConsoleApplication = false` только на Mac) |
| iOS | `IOS/LaunchIOS.mm` | UIKit: экран с надписью (`UIApplicationMain`, делегат приложения) |

Файлы в `Mac/` и `IOS/` компилируются только для своей платформы, `Launch.Build.cs` подключает
`Cocoa` или `UIKit`. Оконные приложения пишут приветствие и в stdout; с переменной окружения
`BEAST_QUIT_AFTER_LAUNCH=1` они завершаются сразу после запуска (так их проверяет CI; в симуляторе —
`SIMCTL_CHILD_BEAST_QUIT_AFTER_LAUNCH=1`). Это минимальные заготовки, не UI-библиотека.

### Android SDK, NDK и JDK

Для Android нужны JDK 17, Android SDK (platform-tools, `platforms;android-35`, `build-tools;35.0.0`) и
NDK. Скрипты в корне репозитория ставят всё это без прав администратора — так же, как
`InstallDotnet`:

```bash
bash InstallAndroid.sh                 # Linux, macOS
bash InstallAndroid.sh --emulator      # плюс эмулятор, образ android-34 и AVD beast_api34
```

```powershell
powershell -ExecutionPolicy Bypass -File InstallAndroid.ps1            # Windows
powershell -ExecutionPolicy Bypass -File InstallAndroid.ps1 -Emulator
```

- **JDK 17** — Eclipse Temurin под ОС и архитектуру (`api.adoptium.net`, проверка SHA-256). Если
  JDK 17+ уже есть в `JAVA_HOME` или в Android Studio (JBR), он используется. Ставится в
  `%LOCALAPPDATA%\Beast\jdk-17`, `~/.local/share/beast/jdk-17` или
  `~/Library/Java/JavaVirtualMachines/beast-temurin-17` (`--jdk-dir`/`-JdkDir`).
- **Android SDK** — в тот же каталог, что у Android Studio: `%LOCALAPPDATA%\Android\Sdk`, `~/Android/Sdk`,
  `~/Library/Android/sdk` (или `ANDROID_HOME`; `--dir`/`-Dir`). Command-line tools берутся из каталога
  репозитория Google (проверка SHA-1), остальное ставит `sdkmanager`; лицензии принимаются автоматически.
  Версия command-line tools закреплена — **19.0**, в каталог `cmdline-tools/19.0` (существующий
  `cmdline-tools/latest` Android Studio не трогается): начиная с 23.0 `sdkmanager` устарел, сам скачивает
  «Android CLI» и включает сбор статистики использования Google. Другую версию задаёт
  `--cmdline-tools`/`-CmdlineTools` (для 23.0+ скрипт предупреждает об этом и не вызывает больше не
  нужный `--licenses`). NDK — версия, на которой проверяется CI (`27.3.13750724`), другая —
  `--ndk`/`-Ndk`.
- **Эмулятор** (`--emulator`/`-Emulator`): `emulator`, `platforms;android-34`, `system-images;android-34;google_apis;x86_64`
  (на Apple Silicon — `arm64-v8a`) и AVD `beast_api34` — для `run --platform Android --avd beast_api34`
  или `"defaultAvd": "beast_api34"`.
- Уже установленное не скачивается повторно; `--force`/`-Force` переустанавливает всё, включая JDK.
- `ANDROID_HOME`, `JAVA_HOME` и `platform-tools` в `PATH` прописываются в профиль shell
  (`~/.zshrc`, `~/.bashrc`) или в переменные окружения пользователя Windows; `--no-profile`/`-NoProfile`
  отключает это. В конце скрипт печатает пути, которые подхватит Beast (по умолчанию `android.sdkRoot` —
  `ANDROID_HOME`, `android.ndkRoot` — его `ndk/` с новейшей версией, `android.javaHome` — `JAVA_HOME`), а
  без профиля — готовый фрагмент `Config/BuildConfig.json`.

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

## Свои шаблоны проектов

```bash
beast new MyApp --path Projects --template ../BeastSDK/Templates/UIApplication
```

Шаблон — обычный каталог: его файлы копируются в новый проект, а в именах файлов и каталогов и в
текстовых файлах заменяются подстановки:

| Подстановка | Значение |
|---|---|
| `{{ProjectName}}` | имя проекта, как в команде |
| `{{ProjectIdentifier}}` (и `{{PROJECT_NAME}}`) | то же как идентификатор C#/C++ (для имён классов) |
| `{{TemplateDir}}` | абсолютный путь к шаблону |
| `{{TemplateDirRelative}}` | путь к шаблону от нового проекта (`/`), например для `externalProjects` |
| `{{AndroidPackage}}` | Java-пакет приложения, `com.beast.<имя в нижнем регистре>`: `run` запускает `<пакет>.GameActivity` |
| `{{AndroidPackagePath}}` | тот же пакет путём: `com/beast/<имя>` |

Шаблон может содержать `Config/BuildConfig.json` (с `externalProjects`), `Source/{{ProjectName}}.Target.cs`
и модули, `Platforms/Windows/Resources` (иконка, `.rc`), файлы Android-проекта (`Platforms/Android/...`:
ресурсы `res/`, манифест, своя активность
`Platforms/Android/app/src/main/java/{{AndroidPackagePath}}/GameActivity.java` с
`package {{AndroidPackage}};` — например, наследник базовой активности SDK). Файлы без подстановок и двоичные (картинки, UTF-16 `.rc`) копируются без
изменений. Каталоги `Intermediate`, `Binaries`, `.git`, `.vs`, `.gradle`, `.idea` не копируются.
Чего в шаблоне нет, Beast дополняет как для `new` без шаблона: `Config/BuildConfig.json`, ресурсы
Windows, Gradle-проект Android — файлы шаблона при этом не заменяются. Затем, как обычно,
создаются файлы IDE (`.sln`, проект Xcode, `.vscode`). Пример — `Tests/Fixtures/Templates/UIApplication`
(приложение на внешнем SDK из `Tests/Fixtures/External`).

---
[Оглавление](index.md)
