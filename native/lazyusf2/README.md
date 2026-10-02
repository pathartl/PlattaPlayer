# lazyusf2.dll — Nintendo 64 USF playback

PlattaPlayer plays USF / miniUSF rips (Nintendo 64 music) on **lazyusf2** by kode54
(https://gitlab.com/kode54/lazyusf2), the library foobar2000's and DeaDBeeF's USF inputs use. lazyusf2 is a
Mupen64Plus-derived N64 emulator (R4300 CPU, RSP, audio interface) driven from the rip's save state.
PlattaPlayer P/Invokes its C interface (`usf/usf.h`) from `src/PlattaPlayer.Codecs.Usf/Emulation`.

## Build

```powershell
./build.ps1
```

Needs git, CMake and Visual Studio 2026 with the C++ workload (toolset v145). The script fetches lazyusf2 at a
pinned commit into `.src/`, applies `patches/`, builds this folder's `CMakeLists.txt` (Release x64) and
copies `lazyusf2.dll` to `src/PlattaPlayer.Codecs.Usf/native/`, from where the build stages it into the USF
codec plugin's folder (`plugins\PlattaPlayer.Codecs.Usf\`). Pass `-Commit <sha>` to build another revision.

What differs from lazyusf2's own (32-bit) MSVC project:

- **x64, cached interpreter.** The x86-64 recompiler needs GCC inline assembly, so the CPU runs on
  lazyusf2's cached interpreter (`r4300/empty_dynarec.c`, no `DYNAREC`), as on non-x86 platforms.
- `compat/zlib.h` provides the one zlib function used (`adler32`); `compat/control87.c` provides
  `__control87_2`, which the CRT only has on 32-bit x86, over `_controlfp_s`.
- `lazyusf2_exports.c` wraps the render calls to restore the caller's floating-point rounding mode, which
  the emulated R4300 FPU changes and would otherwise leave changed on the .NET audio thread.
- `patches/0001-hle-forward-task-return.patch`: `HleForwardTask` lacked a return value.
- `patches/0002-upload-section-bounds.patch`: `usf_upload_section` trusted the file's chunk headers (a crafted
  rip could read past its data or write outside the ROM / save state buffers); such chunks are now refused.

Without `lazyusf2.dll` the app runs as before. USF files are still listed with their tags but don't play.

## License

lazyusf2 is **GPL-2.0-or-later** (as Mupen64Plus is). `lazyusf2.dll` is loaded in-process. That's fine for
personal use, but **distributing** PlattaPlayer together with it would put the combined work under the GPL's
terms, which conflicts with the proprietary BASS library (the same situation as `emu88.dll`). Before shipping a
build that includes it, resolve that, e.g. by hosting the emulator out of process.
