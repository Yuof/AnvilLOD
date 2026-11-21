cmake_minimum_required(VERSION 3.21)

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../VERSION" PENGULOD_VERSION)
string(STRIP "${PENGULOD_VERSION}" PENGULOD_VERSION)
string(REGEX MATCH "^[0-9]+[.][0-9]+[.][0-9]+" PENGULOD_NUMERIC_VERSION "${PENGULOD_VERSION}")
project(PenguLOD VERSION ${PENGULOD_NUMERIC_VERSION} LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

set(PENGULOD_RUNTIME_LINE "1" CACHE STRING "Build line: 1 (SE 1.5.97 - AE 1.6.1170, GOG 1.6.1179), 17 (Skyrim 1.7 and newer) or 14 (Skyrim VR 1.4.15)")

find_package(CommonLibSSE CONFIG REQUIRED)
find_package(spdlog CONFIG REQUIRED)
if(PENGULOD_RUNTIME_LINE STREQUAL "17" OR PENGULOD_RUNTIME_LINE STREQUAL "14")
    # CommonLibSSE-NG 7.2.0 links Microsoft::DirectXTK in its interface without finding it itself,
    # so the plugin has to, or the generate step fails.
    find_package(directxtk CONFIG REQUIRED)
endif()

# Two build lines from one source tree (like ApocryphaRealm Menu Framework):
#   1  = Skyrim SE 1.5.97 / AE 1.6.x (Steam and GOG), CommonLibSSE-NG from the colorglass registry (root vcpkg.json)
#   17 = Skyrim 1.7.x, CommonLibSSE-NG 7.2.0 (alandtse ng), which reads Address Library's format-5 databases
#        (cmake/manifest-17 + cmake/ports-17, preset release-17)
#   14 = Skyrim VR 1.4.15, the same CommonLibSSE-NG 7.2.0 built for VR only
#        (cmake/manifest-vr + cmake/ports-vr, preset release-vr)
add_commonlibsse_plugin(${PROJECT_NAME}
    AUTHOR "CageTV; opteron-x86; PenguLOD contributors"
    SOURCES
        src/main.cpp
        src/DataFile.cpp
        src/Controller.cpp
        src/Settings.cpp
        src/LodDistances.cpp
        src/IniFile.cpp
        src/IniParse.cpp
        src/Menu.cpp
        src/LodLayers.cpp
        src/Adaptive.cpp
)

if(PENGULOD_RUNTIME_LINE STREQUAL "14")
    # The VR build of CommonLib pulls in <openvr.h> (installed by cmake/ports-vr); link its import library too.
    find_library(OPENVR_API_LIB openvr_api REQUIRED)
    target_link_libraries(${PROJECT_NAME} PRIVATE ${OPENVR_API_LIB})
endif()

# Version information (PenguLOD.rc.in). It is compiled in a target of its own so that the C++ options of the plugin and of
# CommonLib do not reach the resource compiler, which rejects them.
if(PENGULOD_VERSION STREQUAL PENGULOD_NUMERIC_VERSION)
    set(PENGULOD_RC_FILEFLAGS "0x0L")
else()
    set(PENGULOD_RC_FILEFLAGS "VS_FF_PRERELEASE")
endif()
if(PENGULOD_RUNTIME_LINE STREQUAL "17")
    set(PENGULOD_RUNTIME_NAME "Skyrim 1.7")
elseif(PENGULOD_RUNTIME_LINE STREQUAL "14")
    set(PENGULOD_RUNTIME_NAME "Skyrim VR 1.4.15")
else()
    set(PENGULOD_RUNTIME_NAME "Skyrim SE 1.5.97 and AE 1.6")
endif()
enable_language(RC)
configure_file(PenguLOD.rc.in "${CMAKE_CURRENT_BINARY_DIR}/PenguLOD.rc" @ONLY)
add_library(PenguLODVersion OBJECT "${CMAKE_CURRENT_BINARY_DIR}/PenguLOD.rc")
target_sources(${PROJECT_NAME} PRIVATE $<TARGET_OBJECTS:PenguLODVersion>)

target_compile_features(${PROJECT_NAME} PRIVATE cxx_std_23)
target_compile_definitions(${PROJECT_NAME} PRIVATE PENGULOD_RUNTIME_LINE=${PENGULOD_RUNTIME_LINE} PENGULOD_VERSION_STRING="${PENGULOD_VERSION}" NOMINMAX WIN32_LEAN_AND_MEAN)
target_precompile_headers(${PROJECT_NAME} PRIVATE src/PCH.h)
target_include_directories(${PROJECT_NAME} PRIVATE src)
target_compile_options(${PROJECT_NAME} PRIVATE /W4 /permissive- /Zc:preprocessor /utf-8)
