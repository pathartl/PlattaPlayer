# Third-party notices

`mgbagsf.dll`, shipped beside this plugin, is built from **mGBA** 0.10.5 by Vicki Pfau and contributors,
https://mgba.io/ (source: https://github.com/mgba-emu/mgba), licensed under the **Mozilla Public License,
version 2.0** (https://mozilla.org/MPL/2.0/). It contains mGBA's Game Boy Advance core, unmodified, and a
small interface file of PlattaPlayer's (`mgbagsf.c`). The source of every mGBA file in it is available at the
link above (tag `0.10.5`); the build files PlattaPlayer uses are in `native/mgbagsf/` of the PlattaPlayer
source tree (`build.ps1` fetches mGBA at that release).

```
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. If a copy of the MPL was not distributed with this
file, You can obtain one at http://mozilla.org/MPL/2.0/.
```

mGBA bundles third-party code, of which these are built into `mgbagsf.dll` too, unmodified:

- **blip_buf** 1.1.0, Copyright (C) 2003-2009 Shay Green, under the **GNU Lesser General Public License,
  version 2.1 or later**. Its source is in mGBA's tree (`src/third-party/blip_buf/`); `mgbagsf.dll` can be
  rebuilt from that source with `native/mgbagsf/build.ps1`.
- **inih**, Copyright (C) 2009-2020 Ben Hoyt, under the BSD 3-Clause licence.
- A CRC-32 table by Gary S. Brown (use without restriction) and MurmurHash3 by Austin Appleby (public domain).
