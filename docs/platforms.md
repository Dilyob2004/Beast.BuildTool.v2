# Платформы

Windows, Linux, macOS, iOS и Android: архитектуры, компиляторы, настройки `Config/BuildConfig.json`.

## Выбор платформы и архитектуры

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

## iOS

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
- Шаблон `new` для iOS — UIKit-приложение `IOS/LaunchIOS.mm` (экран с надписью); свой интерфейс
  пишется в `.mm`-файлах модуля. Модули C++20 — как на macOS: Apple clang их не умеет,
  `ios.cxx` задаёт другой Clang.

## Linux

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

### Стандартная библиотека и запуск другой архитектуры

- `"linux": { "stdlib": "libc++" }` — Clang с `-stdlib=libc++` для C++ (компиляция и линковка); нужен для
  `import std`. По умолчанию — стандартная библиотека компилятора (libstdc++).
- Программы другой архитектуры (`--arch arm64` на x86_64) `run` и `test` запускают через qemu-user
  (`qemu-aarch64`, `qemu-x86_64`, пакет `qemu-user`), библиотеки берутся из `linux.sysroot` или
  `/usr/<triple>` кросс-компилятора: `beast run . --arch arm64` работает как обычный запуск.

## macOS

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

### Universal-бинарники, .dmg и нотаризация

```bash
beast build . --platform Mac --arch universal            # arm64 + x86_64 -> Binaries/MacUniversal/<config>
beast package . --platform Mac --arch universal --dmg    # Binaries/Packages/<Project>-MacUniversal-Release.dmg
beast package . --platform Mac --arch universal --dmg --notarize
```

- `--arch universal` собирает обе архитектуры (`Binaries/Mac`, `Binaries/MacX64`) и объединяет все
  Mach-O файлы — программы, `.dylib`, `.a`, программы бандлов, `.dSYM` — через `lipo` в
  `Binaries/MacUniversal`. `install`/`package` берут файлы оттуда.
- `--dmg` создаёт сжатый образ (`hdiutil`, UDZO). Приложения кладутся в корень рядом со ссылкой
  `Applications`, а `.dylib` из `lib/` встраиваются в `<App>.app/Contents/Frameworks` (приложение
  ищет их через `@executable_path/../Frameworks`), после чего бандл подписывается заново.
- `--notarize`: нужны `mac.signingIdentity` (`Developer ID Application: …`) и `mac.notaryProfile` —
  профиль связки ключей, созданный один раз командой
  `xcrun notarytool store-credentials <профиль> --apple-id … --team-id …`. Образ подписывается,
  отправляется в `notarytool submit --wait`, при статусе Accepted к нему прикрепляется билет
  (`stapler staple`). Без нотаризации macOS блокирует скачанное приложение на других компьютерах.

## Санитайзеры и LTO

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

## Android: Gradle из конфигурации

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

---
[Оглавление](index.md)
