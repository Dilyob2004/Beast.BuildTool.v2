#if defined(__ANDROID__)
#include <android/log.h>
#include <android/native_activity.h>
#include <android/native_window.h>
#include <cstdint>
#include "BeastResources.h"

// Minimal platform entry point: no engine, UI framework or render loop.
// NativeActivity owns the window; callbacks run on the Android UI thread.
static void DrawBackground(ANativeActivity*, ANativeWindow* window)
{
    if (ANativeWindow_setBuffersGeometry(window, 0, 0, WINDOW_FORMAT_RGBA_8888) != 0)
        return;
    ANativeWindow_Buffer buffer{};
    if (ANativeWindow_lock(window, &buffer, nullptr) != 0)
        return;
    auto* pixels = static_cast<std::uint32_t*>(buffer.bits);
    for (int y = 0; y < buffer.height; ++y)
        for (int x = 0; x < buffer.width; ++x)
            pixels[y * buffer.stride + x] = 0xff202020u;
    ANativeWindow_unlockAndPost(window);
}

extern "C" __attribute__((visibility("default")))
void ANativeActivity_onCreate(ANativeActivity* activity, void*, size_t)
{
    // Resources (Resources.Add in *.Build.cs) are inside the APK: Beast::ReadResource reads them through this.
    Beast::SetAssetManager(activity->assetManager);
    __android_log_print(ANDROID_LOG_INFO, "{{PROJECT_NAME}}", "Hello from {{PROJECT_NAME}}!");
    activity->callbacks->onNativeWindowCreated = DrawBackground;
    activity->callbacks->onNativeWindowRedrawNeeded = DrawBackground;
}
#elif !defined(__APPLE__)
// Windows and Linux: a console program. macOS and iOS use Mac/LaunchMac.mm and IOS/LaunchIOS.mm.
#include <iostream>

int main()
{
    std::cout << "Hello from {{PROJECT_NAME}}!" << std::endl;
    return 0;
}
#endif
