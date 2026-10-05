# Builds ppvgm.dll: libvgm's VGM player and sound-chip emulations (static libraries, through libvgm's own
# CMake) plus ppvgm.cpp, a small C interface for PlattaPlayer's VGM codec to P/Invoke.
#
# Not built by dotnet: run build.ps1 next to this file. It fetches libvgm at a pinned commit into .src/ and
# drives this project. See README.md here for the licences.
cmake_minimum_required(VERSION 3.15)
project(ppvgm C CXX)

if(NOT LIBVGM_DIR)
	set(LIBVGM_DIR ${CMAKE_CURRENT_SOURCE_DIR}/.src/libvgm)
endif()

# libvgm asks for CMake 3.12, under which the static-CRT setting below would be ignored inside its folder.
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)
set(CMAKE_POLICY_DEFAULT_CMP0077 NEW)
# Static CRT, so the DLL needs nothing else installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Every sound core, nothing else: no audio output, programs, threading or file loaders (the loaders need
# zlib; ppvgm.cpp has a loader of its own over the managed side's already inflated data). Text conversion
# (for the GD3 tags libvgm reads) goes through the Windows API, so no iconv either.
set(LIBRARY_TYPE STATIC)
set(BUILD_LIBAUDIO OFF)
set(BUILD_LIBEMU ON)
set(BUILD_LIBPLAYER OFF)
set(BUILD_TESTS OFF)
set(BUILD_PLAYER OFF)
set(BUILD_VGM2WAV OFF)
set(USE_SANITIZERS OFF)
set(UTIL_LOADERS OFF)
set(UTIL_THREADING OFF)
set(UTIL_CHARSET_CONV ON)
set(UTIL_CHARCNV_ICONV OFF)
set(UTIL_CHARCNV_WINAPI ON)
set(SNDEMU__ALL ON)
add_subdirectory(${LIBVGM_DIR} libvgm EXCLUDE_FROM_ALL)

# Of libvgm's player library only the VGM player is built here: its GYM player needs zlib. DataLoader.c is
# the loader base class, which libvgm builds only along with its zlib-based loaders.
set(LIBVGM_PLAYER_FILES
	${LIBVGM_DIR}/player/dblk_compr.c
	${LIBVGM_DIR}/player/helper.c
	${LIBVGM_DIR}/player/playerbase.cpp
	${LIBVGM_DIR}/player/vgmplayer.cpp
	${LIBVGM_DIR}/player/vgmplayer_cmdhandler.cpp
	${LIBVGM_DIR}/utils/DataLoader.c)
add_library(ppvgm SHARED ppvgm.cpp ppvgm.def ${LIBVGM_PLAYER_FILES})
target_include_directories(ppvgm PRIVATE ${LIBVGM_DIR})
target_link_libraries(ppvgm PRIVATE vgm-emu vgm-utils)
target_compile_options(ppvgm PRIVATE /W3 /wd4244 /wd4267 /wd4018)
# The player sources must see the definitions libvgm's top-level CMakeLists gives everything (byte order,
# stdint.h). This build is x64 Windows only, so they are fixed.
target_compile_definitions(ppvgm PRIVATE VGM_LITTLE_ENDIAN HAVE_STDINT_H _CRT_SECURE_NO_WARNINGS)
set_target_properties(ppvgm PROPERTIES
	RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR}/out)
