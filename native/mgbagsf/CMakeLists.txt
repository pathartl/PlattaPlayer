# Builds mgbagsf.dll: mGBA's Game Boy Advance core (as a static library, through mGBA's own CMake in its
# library-only mode) plus mgbagsf.c, a small C interface for PlattaPlayer's GSF codec to P/Invoke.
#
# Not built by dotnet: run build.ps1 next to this file. It fetches mGBA at a pinned release into .src/ and
# drives this project. mGBA is MPL-2.0; see README.md here.
cmake_minimum_required(VERSION 3.15)
project(mgbagsf C)

if(NOT MGBA_DIR)
	set(MGBA_DIR ${CMAKE_CURRENT_SOURCE_DIR}/.src/mgba)
endif()

# mGBA asks for CMake 3.1, which CMake 4 no longer accepts, and under which the static-CRT setting below
# would be ignored inside its folder.
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)
# Static CRT, so the DLL needs nothing else installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Only the GBA core: no Game Boy core, frontends, debuggers, scripting or optional libraries (zlib, PNG,
# SQLite, Lua, libelf, FFmpeg…). LIBMGBA_ONLY makes mGBA build a static library and look for no dependencies.
set(LIBMGBA_ONLY ON)
set(M_CORE_GBA ON)
set(M_CORE_GB OFF)
set(ENABLE_SCRIPTING OFF)
set(USE_DEBUGGERS OFF)
set(USE_GDB_STUB OFF)
set(USE_ELF OFF)
set(USE_LUA OFF)
set(USE_ZLIB OFF)
set(USE_PNG OFF)
set(USE_SQLITE3 OFF)
set(USE_FFMPEG OFF)
set(USE_LZMA OFF)
set(USE_LIBZIP OFF)
set(USE_MINIZIP OFF)
set(USE_EPOXY OFF)
set(USE_DISCORD_RPC OFF)
set(BUILD_GL OFF)
set(BUILD_GLES2 OFF)
set(BUILD_GLES3 OFF)
add_subdirectory(${MGBA_DIR} mgba EXCLUDE_FROM_ALL)

add_library(mgbagsf SHARED mgbagsf.c mgbagsf.def)
target_link_libraries(mgbagsf PRIVATE mgba)
# mgbagsf.c reads mGBA's structures, so it must see them with the same definitions mGBA was compiled with.
get_directory_property(MGBA_DIRECTORY_DEFINITIONS DIRECTORY ${MGBA_DIR} COMPILE_DEFINITIONS)
get_target_property(MGBA_TARGET_DEFINITIONS mgba COMPILE_DEFINITIONS)
target_compile_definitions(mgbagsf PRIVATE ${MGBA_DIRECTORY_DEFINITIONS} ${MGBA_TARGET_DEFINITIONS} _CRT_SECURE_NO_WARNINGS)
target_compile_options(mgbagsf PRIVATE /W3 /wd4244 /wd4267)
set_target_properties(mgbagsf PROPERTIES
	RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR}/out)
