# datvrx: the DVB-S / DVB-S2 receiver of OnAir (https://github.com/adswill/OnAir) as a stand-alone program.
# Linux: gcc or clang. Windows: MinGW-w64 in MSYS2 (the compiler OnAir uses), with Rust's x86_64-pc-windows-gnu target.
cmake_minimum_required(VERSION 3.16)
project(datvrx LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()
option(DATVRX_NATIVE "Build for this computer's CPU only (-march=native): faster, not portable" OFF)
option(DATVRX_TOOLS "Also build dvbstool (test signal generator and offline receiver)" ON)
option(DATVRX_RTL "Build the optional binary-safe RTL-SDR pipe source" OFF)
option(DATVRX_PLUTO "Build the optional ADALM-Pluto pipe source (libiio)" OFF)
if(DATVRX_RTL)
  find_path(RTLSDR_INCLUDE_DIR rtl-sdr.h REQUIRED)
  find_library(RTLSDR_LIBRARY NAMES rtlsdr REQUIRED)
  # rtl_iq: I/Q pipe source for datvrx; rtl_sweep: stitched spectrum for the local spectrum view
  find_package(Threads REQUIRED)
  foreach(tool rtl_iq rtl_sweep)
    add_executable(${tool} tools/${tool}.cpp)
    target_include_directories(${tool} PRIVATE ${RTLSDR_INCLUDE_DIR})
    # Threads: the --watch-stdin stop used by the Windows app.
    target_link_libraries(${tool} PRIVATE ${RTLSDR_LIBRARY} Threads::Threads)
    # A release carries the V4 driver next to the tools; look there first.
    set_target_properties(${tool} PROPERTIES BUILD_RPATH "\$ORIGIN")
    if(WIN32)
      target_link_options(${tool} PRIVATE -static-libgcc -static-libstdc++)
    endif()
  endforeach()
endif()
if(DATVRX_PLUTO)
  find_path(IIO_INCLUDE_DIR iio.h REQUIRED)
  find_library(IIO_LIBRARY NAMES iio REQUIRED)
  # pluto_iq: 16-bit I/Q pipe source for datvrx --format cs16
  add_executable(pluto_iq tools/pluto_iq.cpp)
  target_include_directories(pluto_iq PRIVATE ${IIO_INCLUDE_DIR})
  target_link_libraries(pluto_iq PRIVATE ${IIO_LIBRARY})
endif()

# ---- Rust part (BCH, Reed-Solomon, LDPC table set-up, TS tables, DVB text): one static library made by cargo
find_program(CARGO cargo REQUIRED)
set(RUST_TARGET "" CACHE STRING "Rust target triple (empty: the host's; Windows MinGW: x86_64-pc-windows-gnu)")
if(WIN32 AND NOT RUST_TARGET)
  set(RUST_TARGET x86_64-pc-windows-gnu)
endif()
set(RUST_DIR ${CMAKE_BINARY_DIR}/rust)
if(RUST_TARGET)   # cargo names the archive libdatvrx_rust.a on Linux and on the windows-gnu target alike
  set(RUST_TARGET_ARG --target ${RUST_TARGET})
  set(RUST_LIB ${RUST_DIR}/${RUST_TARGET}/release/libdatvrx_rust.a)
else()
  set(RUST_LIB ${RUST_DIR}/release/libdatvrx_rust.a)
endif()
file(GLOB_RECURSE RUST_SOURCES CONFIGURE_DEPENDS ${CMAKE_SOURCE_DIR}/rust/*.rs ${CMAKE_SOURCE_DIR}/rust/*.toml)
add_custom_command(OUTPUT ${RUST_LIB}
  COMMAND ${CMAKE_COMMAND} -E env --unset=MAKEFLAGS --unset=MFLAGS --unset=CARGO_MAKEFLAGS
          ${CARGO} build --release --offline --manifest-path ${CMAKE_SOURCE_DIR}/rust/Cargo.toml --target-dir ${RUST_DIR} ${RUST_TARGET_ARG}
  DEPENDS ${RUST_SOURCES}
  COMMENT "Building the Rust crates with cargo"
  VERBATIM)
add_custom_target(datvrx_rust_build DEPENDS ${RUST_LIB})
add_library(datvrx_rust STATIC IMPORTED)
set_target_properties(datvrx_rust PROPERTIES IMPORTED_LOCATION ${RUST_LIB})
add_dependencies(datvrx_rust datvrx_rust_build)

# ---- C++ part: the receiver and the few shared pieces it uses
file(GLOB DVBS_SOURCES CONFIGURE_DEPENDS core/src/dvbs/*.cpp)
add_library(dvbsrx STATIC
  ${DVBS_SOURCES}
  core/src/dsp/ldpc.cpp core/src/dsp/dsp_compat.cpp
  core/src/dsp/gpu_ldpc_stub.cpp core/src/dsp/gpu_viterbi_stub.cpp
  core/src/t2/t2fec.cpp core/src/t2/t2fec_tables.cpp
  core/src/dvbt/dvbt.cpp core/src/dvbt/dvbt_fec.cpp
  core/src/fec/fec_proto.cpp core/src/fec/fec_proto_rs.cpp
  core/src/tv/ts.cpp core/src/tv/ts_proto_rs.cpp core/src/tv/si_tables.cpp core/src/tv/ts_data.cpp
  core/src/tv/dsmcc.cpp core/src/tv/rcs.cpp core/src/tv/epg.cpp core/src/tv/dvb_text.cpp core/src/tv/dvb_text_rs.cpp
  core/src/common/text_charset.cpp core/src/common/text_charset_rs.cpp core/src/common/threadpool.cpp core/src/common/inflate.cpp
  core/src/stub/demo_ts_stub.cpp)
target_include_directories(dvbsrx PUBLIC core/include core/src)

include(CheckCXXCompilerFlag)
check_cxx_compiler_flag(-fcx-limited-range DATVRX_HAS_CX_LIMITED)
check_cxx_compiler_flag(-fcomplex-arithmetic=basic DATVRX_HAS_COMPLEX_BASIC)
if(DATVRX_HAS_CX_LIMITED)
  target_compile_options(dvbsrx PRIVATE -fcx-limited-range)   # plain complex multiply: a large speed-up for std::complex<float>
elseif(DATVRX_HAS_COMPLEX_BASIC)
  target_compile_options(dvbsrx PRIVATE -fcomplex-arithmetic=basic)
endif()
if(DATVRX_NATIVE)
  target_compile_options(dvbsrx PUBLIC -march=native)
endif()

find_package(Threads REQUIRED)
target_link_libraries(dvbsrx PUBLIC datvrx_rust Threads::Threads ${CMAKE_DL_LIBS})
if(WIN32)
  target_compile_definitions(dvbsrx PUBLIC _USE_MATH_DEFINES WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
  target_link_libraries(dvbsrx PUBLIC ws2_32 userenv bcrypt ntdll advapi32)   # what Rust's standard library needs on Windows
  target_link_options(dvbsrx INTERFACE -static)                                 # one .exe without MinGW DLLs next to it
else()
  target_link_libraries(dvbsrx PUBLIC m)
endif()

add_executable(datvrx tools/datvrx.cpp)
target_link_libraries(datvrx PRIVATE dvbsrx)

if(DATVRX_TOOLS)
  add_executable(dvbstool tools/dvbstool.cpp)
  target_link_libraries(dvbstool PRIVATE dvbsrx)
endif()

install(TARGETS datvrx RUNTIME DESTINATION bin)
