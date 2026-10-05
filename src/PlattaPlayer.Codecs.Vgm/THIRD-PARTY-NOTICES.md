# Third-party notices

`ppvgm.dll`, shipped beside this plugin, is built from **libvgm** by Valley Bell and contributors,
https://github.com/ValleyBell/libvgm (commit `c8b998b6`), unmodified, plus a small interface file of
PlattaPlayer's (`ppvgm.cpp`). The build files PlattaPlayer uses are in `native/libvgm/` of the PlattaPlayer
source tree (`build.ps1` fetches libvgm at that commit).

libvgm has no licence file of its own. Its sound cores come from many projects, each under its own
licence, as stated in each source file:

- **GNU General Public License, version 2 or later**: the MAME-derived FM cores (`fmopn.c`, `fmopl.c`,
  `ym2151.c`, `ym2413.c`, `ymf262.c`, `msm5232.c`, `nes_apu.c`), Nuked OPLL (`nukedopll.c`), the Gens-derived
  cores (`ym2612.c`, `scd_pcm.c`, `pwm.c`), Mednafen's VSU (`vsu.c`), Ootake's PSG (`Ootake_PSG.c`).
- **GNU General Public License, version 2**: `ymf278b.c`.
- **GNU Lesser General Public License, version 2.1 or later**: Nuked OPN2 (`ym3438.c`), Nuked OPM
  (`nukedopm.c`), Nuked OPL3 (`nukedopl3.c`), DOSBox's AdLibEmu (`adlibemu_opl_inc.c`).
- **BSD 3-Clause**: the MAME-derived PCM and PSG cores (`sn76496.c`, `segapcm.c`, `rf5c68.c`, `okim6295.c`,
  `c140.c`, `k054539.c`, `qsound_mame.c`, `scsp.c`, `ymz280b.c`, `ay8910.c` and the others marked so).
- **MIT**: SameBoy's APU (`sameboy_apu.c`, Lior Halphon), Mikey (`mikey.c`, laoo).
- Mitsutaka Okazaki's EMU2413 and EMU2149, Maxim's SN76489, NSFPlay's NES cores, superctr's QSound and
  libvgm's own player and glue code carry their authors' copyright notices.

Taken together, `ppvgm.dll` is a GPL-2.0 work. Its complete source is available at the link above, and it
can be rebuilt and replaced with `native/libvgm/build.ps1`.

```
This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```
