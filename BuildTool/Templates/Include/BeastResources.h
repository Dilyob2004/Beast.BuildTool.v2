// BeastResources.h - provided by Beast BuildTool (written to Intermediate/Beast/Include, on every
// module's include path). Finds and reads the files a module declares with Resources.Add(...):
//
//   Resources.Add("Assets");                      // in *.Build.cs
//   std::vector<std::byte> data;
//   if (Beast::ReadResource("Assets/hero.png", data)) { ... }
//
// Windows, Linux:  next to the program (Binaries/<platform>/<config>/Assets/...)
// macOS (.app):    Game.app/Contents/Resources/Assets/...   (console programs: next to the program)
// iOS:             Game.app/Assets/...
// Android:         inside the APK (assets/Assets/...), read through AAssetManager; call
//                  Beast::SetAssetManager(activity->assetManager) once at startup.
#pragma once

#include <cstddef>
#include <cstdio>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#define BEAST_RESOURCES_LEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#define BEAST_RESOURCES_NOMINMAX
#endif
#include <windows.h>
#ifdef BEAST_RESOURCES_LEAN
#undef WIN32_LEAN_AND_MEAN
#undef BEAST_RESOURCES_LEAN
#endif
#ifdef BEAST_RESOURCES_NOMINMAX
#undef NOMINMAX
#undef BEAST_RESOURCES_NOMINMAX
#endif
#elif defined(__ANDROID__)
#include <android/asset_manager.h>
#elif defined(__APPLE__)
#include <climits>
#include <cstdint>
#include <cstdlib>
#include <mach-o/dyld.h>
#else
#include <climits>
#include <unistd.h>
#endif

namespace Beast
{
#if defined(__ANDROID__)
    inline AAssetManager*& ResourceAssetManager()
    {
        static AAssetManager* manager = nullptr;
        return manager;
    }

    /// Android: the activity's asset manager (ANativeActivity::assetManager); call once at startup.
    inline void SetAssetManager(AAssetManager* manager) { ResourceAssetManager() = manager; }
#endif

#if defined(_WIN32)
    namespace Detail
    {
        inline std::wstring Wide(std::string_view utf8)
        {
            int size = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
            std::wstring wide(static_cast<std::size_t>(size), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), wide.data(), size);
            return wide;
        }
    }
#endif

    /// Directory of the resources, absolute and ending with a separator (UTF-8).
    /// Empty on Android: resources are inside the APK and are read with ReadResource.
    inline std::string ResourceDirectory()
    {
#if defined(_WIN32)
        std::wstring path(32768, L'\0');
        DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        path.resize(length);
        path.resize(path.find_last_of(L"\\/") + 1);
        int size = WideCharToMultiByte(CP_UTF8, 0, path.c_str(), static_cast<int>(path.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(static_cast<std::size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, path.c_str(), static_cast<int>(path.size()), utf8.data(), size, nullptr, nullptr);
        return utf8;
#elif defined(__ANDROID__)
        return {};
#elif defined(__APPLE__)
        std::uint32_t size = 0;
        _NSGetExecutablePath(nullptr, &size);
        std::string path(size, '\0');
        if (_NSGetExecutablePath(path.data(), &size) != 0) return {};
        path.resize(std::strlen(path.c_str()));
        char resolved[PATH_MAX];
        if (realpath(path.c_str(), resolved)) path = resolved;
        path.resize(path.find_last_of('/') + 1);
        // macOS application bundle: X.app/Contents/MacOS/X -> X.app/Contents/Resources/.
        constexpr std::string_view bundle = ".app/Contents/MacOS/";
        if (path.size() >= bundle.size() && path.compare(path.size() - bundle.size(), bundle.size(), bundle) == 0)
            path.replace(path.size() - 6, 6, "Resources/");
        return path;
#else
        char buffer[PATH_MAX];
        ssize_t length = readlink("/proc/self/exe", buffer, sizeof buffer);
        if (length <= 0) return {};
        std::string path(buffer, static_cast<std::size_t>(length));
        path.resize(path.find_last_of('/') + 1);
        return path;
#endif
    }

    /// Full path of a resource ("Assets/hero.png", '/' separators). On Android: the path inside the APK.
    inline std::string ResourcePath(std::string_view relative)
    {
        return ResourceDirectory() + std::string(relative);
    }

    /// Reads a whole resource into 'data'. Returns false if it does not exist or cannot be read.
    inline bool ReadResource(std::string_view relative, std::vector<std::byte>& data)
    {
        data.clear();
#if defined(__ANDROID__)
        AAssetManager* manager = ResourceAssetManager();
        if (!manager) return false;
        AAsset* asset = AAssetManager_open(manager, std::string(relative).c_str(), AASSET_MODE_BUFFER);
        if (!asset) return false;
        const void* buffer = AAsset_getBuffer(asset);
        if (buffer)
        {
            data.resize(static_cast<std::size_t>(AAsset_getLength64(asset)));
            if (!data.empty()) std::memcpy(data.data(), buffer, data.size());
        }
        AAsset_close(asset);
        return buffer != nullptr;
#else
#if defined(_WIN32)
        std::FILE* file = nullptr;
        if (_wfopen_s(&file, Detail::Wide(ResourcePath(relative)).c_str(), L"rb") != 0) file = nullptr;
#else
        std::FILE* file = std::fopen(ResourcePath(relative).c_str(), "rb");
#endif
        if (!file) return false;
        bool ok = true;
        std::byte chunk[65536];
        for (std::size_t read; (read = std::fread(chunk, 1, sizeof chunk, file)) > 0;)
            data.insert(data.end(), chunk, chunk + read);
        if (std::ferror(file)) ok = false;
        std::fclose(file);
        return ok;
#endif
    }

    /// Reads a text resource; false if it does not exist.
    inline bool ReadResource(std::string_view relative, std::string& text)
    {
        std::vector<std::byte> data;
        if (!ReadResource(relative, data)) return false;
        text.assign(reinterpret_cast<const char*>(data.data()), data.size());
        return true;
    }
}
