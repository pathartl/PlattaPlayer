/* lazyusf2 includes <zlib.h> only for adler32() (r4300/interpreter_tlb.def, to notice code pages that a TLB
   remap left unchanged). This stands in for it so the build needs no zlib. */
#ifndef LAZYUSF2_COMPAT_ZLIB_H
#define LAZYUSF2_COMPAT_ZLIB_H

#include <stddef.h>

static unsigned long adler32(unsigned long adler, const unsigned char *buf, unsigned int len)
{
    unsigned long a = adler & 0xffff, b = (adler >> 16) & 0xffff;
    if (!buf) return 1;
    while (len)
    {
        /* 5552 is the most bytes that can be summed before the 32-bit sums could overflow. */
        unsigned int n = len < 5552 ? len : 5552;
        len -= n;
        while (n--)
        {
            a += *buf++;
            b += a;
        }
        a %= 65521;
        b %= 65521;
    }
    return (b << 16) | a;
}

#endif
