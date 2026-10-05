# Third-party notices

The Game Boy emulation in `Emulation/` (the SM83 CPU, APU and its output path, timers, PPU timing, DMA, memory
map, MBC3 banking and the GBS player's start-up and idle loop) is a C# port of the corresponding parts of
**SameBoy** v1.0.3 (`Core/sm83_cpu.c`, `Core/apu.c`, `Core/timing.c`, `Core/display.c`, `Core/memory.c`,
`Core/mbc.c`, `Core/gb.c`), https://github.com/LIJI32/SameBoy, used under the MIT (Expat) licence:

```
Expat License

Copyright (c) 2015-2026 Lior Halphon

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
