# Builds lazyusf2.dll: kode54's lazyusf2 (a Mupen64Plus-derived N64 emulator that plays USF rips) as a
# shared library exporting its C interface (usf/usf.h), for PlattaPlayer's USF codec to P/Invoke.
#
# Not built by dotnet: run build.ps1 next to this file. It fetches lazyusf2 at a pinned commit into .src/
# and drives this project. lazyusf2 is GPL-2.0-or-later; see README.md here.
cmake_minimum_required(VERSION 3.15)
project(lazyusf2 C)

if(NOT LAZYUSF2_DIR)
	set(LAZYUSF2_DIR ${CMAKE_CURRENT_SOURCE_DIR}/.src/lazyusf2)
endif()

# The x64 build uses the cached interpreter: lazyusf2's x86_64 recompiler needs GCC inline assembly, and its
# README says to build r4300/empty_dynarec.c instead of a recompiler, without DYNAREC, and never new_dynarec/.
set(LAZYUSF2_SOURCES
	ai/ai_controller.c
	api/callbacks.c
	debugger/dbg_decoder.c
	main/main.c main/rom.c main/savestates.c main/util.c
	memory/memory.c
	pi/cart_rom.c pi/pi_controller.c
	r4300/cached_interp.c r4300/cp0.c r4300/cp1.c r4300/empty_dynarec.c r4300/exception.c
	r4300/instr_counters.c r4300/interupt.c r4300/mi_controller.c r4300/pure_interp.c r4300/r4300.c
	r4300/r4300_core.c r4300/recomp.c r4300/reset.c r4300/tlb.c
	rdp/rdp_core.c
	ri/rdram.c ri/rdram_detection_hack.c ri/ri_controller.c
	rsp/rsp_core.c
	rsp_hle/alist.c rsp_hle/alist_audio.c rsp_hle/alist_naudio.c rsp_hle/alist_nead.c rsp_hle/audio.c
	rsp_hle/cicx105.c rsp_hle/hle.c rsp_hle/hvqm.c rsp_hle/jpeg.c rsp_hle/memory.c rsp_hle/mp3.c
	rsp_hle/musyx.c rsp_hle/plugin.c rsp_hle/re2.c
	rsp_lle/rsp.c
	si/cic.c si/game_controller.c si/n64_cic_nus_6105.c si/pif.c si/si_controller.c
	usf/barray.c usf/resampler.c usf/usf.c
	vi/vi_controller.c)
list(TRANSFORM LAZYUSF2_SOURCES PREPEND ${LAZYUSF2_DIR}/)

add_library(lazyusf2 SHARED ${LAZYUSF2_SOURCES} lazyusf2_exports.c compat/control87.c lazyusf2.def)
target_include_directories(lazyusf2 PRIVATE ${LAZYUSF2_DIR} ${CMAKE_CURRENT_SOURCE_DIR}/compat)
target_compile_definitions(lazyusf2 PRIVATE WIN32 NDEBUG _CRT_SECURE_NO_WARNINGS)
# The LLE RSP's vector unit uses SSE2 intrinsics (always present on x64).
set_source_files_properties(${LAZYUSF2_DIR}/rsp_lle/rsp.c PROPERTIES COMPILE_DEFINITIONS ARCH_MIN_SSE2)
# Static CRT, so the DLL needs nothing else installed.
set_property(TARGET lazyusf2 PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_options(lazyusf2 PRIVATE /W1)
set_target_properties(lazyusf2 PROPERTIES
	RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR}/out)
