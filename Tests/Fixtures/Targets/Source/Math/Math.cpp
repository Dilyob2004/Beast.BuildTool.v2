#include "BeastMath.h"
#include <Helper.h>
#include "Detail.h"
#if !defined(MATH_PRIVATE) || !defined(DETAIL_PUBLIC)
#error Own private definition or direct dependency API missing
#endif
int ExtraValue();
extern "C" MATH_EXPORT int MathValue() { return HelperValue() + DetailValue() + ExtraValue(); }
