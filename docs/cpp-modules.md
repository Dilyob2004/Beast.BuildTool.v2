# Модули C++20

Именованные модули, разделы, `import std`.

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

## import std

`import std;` и `import std.compat;` (C++23, работают и в C++20-режиме компиляторов) собираются из
исходников, которые поставляет сам компилятор:

| Компилятор | Источник | Условие |
| --- | --- | --- |
| MSVC | `VC/Tools/MSVC/<версия>/modules/std.ixx`, `std.compat.ixx` | Visual Studio 2022 17.5+ |
| Clang (Linux) | `<llvm>/share/libc++/v1/std.cppm` | `"linux": { "stdlib": "libc++" }`, пакет `libc++-18-dev` |
| Android NDK | `share/libc++/v1/std.cppm` NDK | если NDK его поставляет |
| GCC, Apple clang, clang-cl | — | пока не поддерживается (понятная ошибка) |

- Модуль `std` компилирует самая «глубокая» цель среди тех, что его импортируют (например,
  статическая библиотека, от которой зависит приложение): цель и её цели-зависимости используют один
  BMI. Объект `std` линкуется в эту цель, как её собственные объекты.
- Флаги берутся у первой единицы, импортирующей `std` (стандарт, макросы, CRT); предупреждения в
  исходниках стандартной библиотеки выключены. Если модули одной цели собираются с несовместимыми
  флагами (например, разные `CppStandard`), компилятор сообщит о несовпадении BMI.
- Для `std::print`, `std::ranges::fold_left` и т. п. задайте `CppVersion = CppVersion.Latest`.

```cpp
import std;
int main() { std::println("{}", std::vector{1, 2, 3}.size()); }
```

## IntelliSense

`generate` помечает в `.vcxproj` интерфейсы (`.ixx`/`.cppm`) как `CompileAsCppModule`, а внутренние
разделы — как `CompileAsCppModuleInternalPartition`, чтобы IntelliSense Visual Studio разбирал их как
модули. Ссылки на собранные BMI (`/reference`) IntelliSense не получает: если подсветка отстаёт,
помогает сборка проекта. clangd получает полные команды из `compile_commands.json`.

---
[Оглавление](index.md)
