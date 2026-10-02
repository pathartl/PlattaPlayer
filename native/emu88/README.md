# emu88.dll — emulated MIDI sound modules

PlattaPlayer can play MIDI on a low-level emulation of a hardware sound module instead of a SoundFont:
gearmulator's **88emu** (https://github.com/dsp56300/gearmulator, `source/ronaldo/88emu`), which runs the
original firmware of the Roland SC-55 / SC-55mkII / SC-88 / SC-88VL / SC-88Pro / SC-8850 (plus the
MT-32 / CM family and others, some marked experimental). PlattaPlayer P/Invokes its C interface
(`88lib/c_interface.h`) from `PlattaPlayer.Playback/Emulation`.

## Build

```powershell
./build.ps1
```

Needs git, CMake and Visual Studio 2026 with the C++ workload (toolset v145). The script fetches gearmulator
at a pinned commit into `.src/` (only the submodules 88emu needs), builds the `emu88` target from this
folder's `CMakeLists.txt` and copies `emu88.dll` to `src/PlattaPlayer.Codecs.Midi/native/`, from where the
build stages it into the MIDI codec plugin's folder (`plugins\PlattaPlayer.Codecs.Midi\`). Pass `-Commit <sha>` to build a different gearmulator revision.

Without `emu88.dll` the app runs as before. Emulated devices just don't appear.

## ROMs

The emulator needs dumps of the module's firmware and wave ROMs, which are not distributed. Drop them into
`%LOCALAPPDATA%\PlattaPlayer\Roms` (subfolders are fine). Images are identified by content, so names do
not matter. Every model whose ROMs are complete appears in Settings → MIDI (Default MIDI device) as
"Emulated · <model>", and is preferred over SoundFonts by default.

## License

gearmulator is **GPL-3.0**. `emu88.dll` is built from it and loaded in-process. That is fine for
personal use, but **distributing** PlattaPlayer together with `emu88.dll` would put the combined work under
the GPL's terms, which conflicts with the proprietary BASS library. Before shipping a build that includes
it, resolve that, e.g. by hosting the emulator out of process.
