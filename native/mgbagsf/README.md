# mgbagsf.dll — Game Boy Advance GSF playback

PlattaPlayer plays GSF / miniGSF rips (Game Boy Advance music) on **mGBA** (https://mgba.io/,
https://github.com/mgba-emu/mgba), the emulator the lazygsf library of foobar2000's and DeaDBeeF's GSF inputs
was built on. lazygsf itself is no longer available online, so `mgbagsf.c` here is PlattaPlayer's own thin
interface over mGBA's core API, doing what lazygsf did: load the GSF image as a cartridge ROM (or into EWRAM,
for a multiboot rip), skip the BIOS (mGBA's HLE BIOS answers the SWI calls), run, and collect the audio.
PlattaPlayer P/Invokes it from `src/PlattaPlayer.Codecs.Gsf/Emulation`.

## Build

```powershell
./build.ps1
```

Needs git, CMake and Visual Studio 2026 with the C++ workload (toolset v145). The script fetches mGBA at the
pinned release (**0.10.5**, commit `26b7884b`) into `.src/`, applies `patches/` if there are any (there are
none: mGBA builds unmodified), builds this folder's `CMakeLists.txt` (Release x64, static CRT) and copies
`mgbagsf.dll` to `src/PlattaPlayer.Codecs.Gsf/native/`, from where the build stages it into the GSF codec
plugin's folder (`plugins\PlattaPlayer.Codecs.Gsf\`). Pass `-Commit <sha>` to build another revision. The DLL
needs only Windows system libraries.

`CMakeLists.txt` uses mGBA's own CMake in its library-only mode (`LIBMGBA_ONLY`) with only the GBA core: no
Game Boy core, frontends, debuggers, scripting, or optional libraries (zlib, PNG, SQLite, Lua, libelf,
FFmpeg). mGBA's CMake asks for CMake 3.1, which CMake 4 refuses, so the wrapper sets
`CMAKE_POLICY_VERSION_MINIMUM`.

## The interface (`mgbagsf.c`)

| Export | |
|---|---|
| `pp_gsf_create(image, size, entry)` | A machine running the image from `entry` (0x08/0x09…: ROM; 0x02…: multiboot in EWRAM), booted with the BIOS skipped. Copies the image. Null for an entry point elsewhere or an image too big for its region. |
| `pp_gsf_sample_rate()` | 32768. |
| `pp_gsf_render(gsf, buffer, frames)` | Interleaved stereo int16 frames; a null buffer discards them. |
| `pp_gsf_restart(gsf)` | Boots again. |
| `pp_gsf_state_size` / `pp_gsf_save_state` / `pp_gsf_load_state` | Everything the coming output depends on, for the decoder's seek checkpoints. |
| `pp_gsf_destroy(gsf)` | |

What it does that a plain mGBA frontend wouldn't:

- **No video.** No video buffer is set, so mGBA keeps its dummy renderer and draws nothing.
- **The output is the mixer's own samples**, taken from `mAVStream.postAudioFrame` rather than mGBA's blip_buf
  resampler: one per audio tick, 32768 Hz at the default SOUNDBIAS. A game that raises SOUNDBIAS's rate
  (65536 Hz and up) has each group of ticks averaged down to 32768 Hz, so the rate never changes mid-song.
  The host resamples to the device, as it does for the SPC and USF codecs.
- **Deterministic.** No BIOS file or mGBA configuration is read; the cartridge clock (for the few games that
  have one) runs from a fixed start with the emulation, not from the wall clock; mGBA's log is silenced.
- **Exact save states.** mGBA 0.10.5's state loader doesn't restore the PSG channels exactly (their frequency
  registers are write-only and never stored, so they come back as 0, and restoring the I/O registers runs the
  channels on with stale frequencies). This is still so on mGBA's master branch. Since the decoder only loads a
  state back into the machine that saved it, the wrapper saves the four channel structures alongside mGBA's
  state and puts them back after the load. The harness's `synth` / `seektest` check that seeking through
  checkpoints gives exactly the samples of playing straight through (PSG and Direct Sound).

Without `mgbagsf.dll` the app runs as before. GSF files are still listed with their tags but don't play.

## License

mGBA is under the **Mozilla Public License 2.0**. The MPL's copyleft is per file: the mGBA source files in
`mgbagsf.dll` (and any changes made to them) must stay available under the MPL, but it doesn't reach the
code they're combined with, so shipping `mgbagsf.dll` beside PlattaPlayer and the proprietary BASS library is
fine. PlattaPlayer uses mGBA unmodified, and `build.ps1` names the exact upstream release, so pointing to it
(as `src/PlattaPlayer.Codecs.Gsf/THIRD-PARTY-NOTICES.md` does) meets that; if patches are ever added under
`patches/`, they must be published with the build too. `mgbagsf.c` is PlattaPlayer's own.

mGBA's tree bundles a few third-party files that are compiled in: inih (BSD-3-Clause), a CRC-32 table (no
restrictions), MurmurHash3 (public domain) and **blip_buf** (Shay Green, **LGPL-2.1-or-later**). blip_buf is
linked into `mgbagsf.dll` (mGBA's audio code feeds it, although mgbagsf doesn't read its output). The LGPL is
satisfied the same way: the DLL is a separate library the app loads at run time, its sources are public, and
it can be rebuilt and replaced with `build.ps1`. The notices file lists these.
