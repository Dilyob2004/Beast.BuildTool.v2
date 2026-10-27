#include "Widgets.h"
#include "BeastResources.h"
#include <cstdio>
#include <vector>

int main()
{
    std::vector<std::byte> logo;
    bool resource = Beast::ReadResource("Assets/logo.bin", logo);
    std::printf("{{ProjectName}} template %d %zu\n", WidgetCount(), resource ? logo.size() : 0);
    return 0;
}
