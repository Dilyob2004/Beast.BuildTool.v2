#include "Widgets.h"                 // public include path of the external module
#include "Core/Public/SdkCore.h"     // the external project's Source/ is on the include path
#include "BeastResources.h"
#include <cstdio>
#include <string>

int main()
{
    std::string text;
    bool resource = Beast::ReadResource("Assets/SDK/sdk.txt", text);
    std::printf("AppA %d %d %d %s\n", WidgetCount(), SDK_VERSION, SDK_IS_EXTERNAL, resource ? "resource" : "no-resource");
    return 0;
}
