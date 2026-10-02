/* lazyusf2's r4300/fpu.h sets the FPU rounding mode with __control87_2, which the CRT only has on 32-bit x86.
   On x64 all floating point is SSE2, so the rounding mode lives in MXCSR, which _controlfp_s sets. */
#include <float.h>

int __cdecl __control87_2(unsigned int newValue, unsigned int mask, unsigned int *x87, unsigned int *sse2)
{
    unsigned int current = 0;
    if (_controlfp_s(&current, newValue, mask) != 0) return 0;
    if (x87) *x87 = current;
    if (sse2) *sse2 = current;
    return 1;
}
