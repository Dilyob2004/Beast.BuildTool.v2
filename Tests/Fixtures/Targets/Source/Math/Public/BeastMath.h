#pragma once
#if defined(_WIN32)
#define MATH_EXPORT __declspec(dllexport)
#else
#define MATH_EXPORT __attribute__((visibility("default")))
#endif
extern "C" MATH_EXPORT int MathValue();
