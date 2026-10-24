#include <iostream>
#include "BeastMath.h"
#if !defined(MATH_PUBLIC) || !defined(HELPER_PUBLIC)
#error Public definitions were not exported transitively
#endif
#if defined(MATH_PRIVATE) || defined(DETAIL_PRIVATE) || defined(DETAIL_PUBLIC) || defined(UNUSED_DEFINE)
#error Private or unrelated module definitions leaked
#endif
int main() { std::cout << MathValue() << std::endl; }
