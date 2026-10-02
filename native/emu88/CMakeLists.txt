# Builds emu88.dll: gearmulator's Roland "88emu" (Sound Canvas / MT-32 / CM low-level emulation) as a
# shared library exporting its C interface (88lib/c_interface.h), for PlattaPlayer to P/Invoke.
#
# Not built by dotnet: run build.ps1 next to this file. It fetches gearmulator at a pinned commit into
# .src/ and drives this project. gearmulator is GPL-3.0; see README.md here.
cmake_minimum_required(VERSION 3.15)

if(NOT GEARMULATOR_DIR)
	set(GEARMULATOR_DIR ${CMAKE_CURRENT_SOURCE_DIR}/.src/gearmulator)
endif()

# gearmulator's CMake names its options after the top-level project, so this project must be called
# "gearmulator" for gearmulator_SYNTH_* / gearmulator_BUILD_* below to reach it.
project(gearmulator VERSION 1.0)

set(gearmulator_BUILD_JUCEPLUGIN OFF CACHE BOOL "" FORCE)
set(gearmulator_BUILD_JUCEPLUGIN_CLAP OFF CACHE BOOL "" FORCE)
set(gearmulator_BUILD_FX_PLUGIN OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_OSIRUS OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_OSTIRUS OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_VAVRA OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_XENIA OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_NODALRED2X OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_JE8086 OFF CACHE BOOL "" FORCE)
set(gearmulator_SYNTH_88EMU ON CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)

include(${GEARMULATOR_DIR}/source/cmake/base.cmake)
add_subdirectory(${GEARMULATOR_DIR}/source ${CMAKE_BINARY_DIR}/gearmulator)

add_library(emu88 SHARED emu88_exports.cpp)
target_link_libraries(emu88 PRIVATE 88lib)
set_target_properties(emu88 PROPERTIES
	RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/out
	RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR}/out)
