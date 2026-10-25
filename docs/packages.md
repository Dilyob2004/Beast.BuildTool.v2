# vcpkg и Conan

Зависимости из пакетных менеджеров.

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

---
[Оглавление](index.md)
