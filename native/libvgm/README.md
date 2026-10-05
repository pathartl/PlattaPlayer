# ppvgm.dll — VGM playback

PlattaPlayer plays VGM / VGZ files (sound-chip logs: Mega Drive / Genesis, Master System, arcade boards, PC
Engine, Neo Geo, MSX, OPL…) on **libvgm** (https://github.com/ValleyBell/libvgm), Valley Bell's library
behind VGMPlay and the reference VGM player: it parses the log and drives emulations of the forty-odd chips
VGM can name. `ppvgm.cpp` here is PlattaPlayer's own thin interface over libvgm's `VGMPlayer`. PlattaPlayer
P/Invokes it from `src/PlattaPlayer.Codecs.Vgm/Emulation`.

## Build

```powershell
./build.ps1
```

Needs git, CMake and Visual Studio 2026 with the C++ workload (toolset v145). The script fetches libvgm at a
pinned commit (libvgm has no releases; `c8b998b6`, master as of 2026-09-05) into `.src/`, applies `patches/`
if there are any (there are none), builds this folder's `CMakeLists.txt` (Release x64, static CRT) and
copies `ppvgm.dll` to `src/PlattaPlayer.Codecs.Vgm/native/`, from where the build stages it into the VGM
codec plugin's folder (`plugins\PlattaPlayer.Codecs.Vgm\`). The DLL needs only Windows system libraries.

`CMakeLists.txt` builds libvgm's sound-core and utility libraries through libvgm's own CMake, with every
core, but no audio output, programs, threading or zlib: of the player library it compiles only the VGM
player itself (the GYM player needs zlib), and `ppvgm.cpp` has its own memory loader, since the managed side
inflates .vgz files.

## The interface (`ppvgm.cpp`)

| Export | |
|---|---|
| `pp_vgm_create(data, size, rate, flags, rom, romSize)` | A started player for the (uncompressed, copied) VGM. `flags` bit 0: the Nuked cores for YM2612, YM2151, YM2413, YM3812 and YMF262. `rom`: the YRW801 sample ROM for OPL4 songs, or null. Null when libvgm can't load the data. |
| `pp_vgm_volume_gain(vgm)` | The header's volume modifier as a 16.16 factor. |
| `pp_vgm_render(vgm, buffer, frames)` | Interleaved stereo int32 frames, about 24-bit; a null buffer discards them. Fewer frames only when a song without a loop has ended. |
| `pp_vgm_seek(vgm, frame)` | libvgm's seek: replays the commands up to the frame without rendering. |
| `pp_vgm_restart(vgm)` | Back to the start. |
| `pp_vgm_destroy(vgm)` | |

The output is libvgm's raw mix, before its `PlayerA` layer: the managed decoder applies the song's volume,
counts loops and fades out (with PlayerA's curve), so it knows the length up front.

Without `ppvgm.dll` the app runs as before. VGM files are still listed with their tags but don't play.

## License

The cores are under a mix of licences (GPL-2.0+, GPL-2.0, LGPL-2.1+, BSD-3-Clause, MIT; see
`src/PlattaPlayer.Codecs.Vgm/THIRD-PARTY-NOTICES.md`), so `ppvgm.dll` as a whole is a **GPL-2.0** work. It is
loaded in-process. That's fine for personal use, but **distributing** PlattaPlayer together with it would put
the combined work under the GPL's terms, which conflicts with the proprietary BASS library (the same situation
as `emu88.dll` and `lazyusf2.dll`). Before shipping a build that includes it, resolve that, e.g. by hosting
the emulator out of process, or by building only BSD/MIT/LGPL cores (the Nuked FM cores are LGPL; the
SN76489 has a BSD core).
