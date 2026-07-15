cmake_minimum_required(VERSION 3.22.1)
project("quiet-rebellion")

add_library(quiet-rebellion-rfcomm SHARED rfcomm.c)

target_compile_options(quiet-rebellion-rfcomm PRIVATE
    -g0
    -ffile-prefix-map=${CMAKE_SOURCE_DIR}=.
    -ffile-prefix-map=${CMAKE_BINARY_DIR}=.
)

target_link_options(quiet-rebellion-rfcomm PRIVATE
    -Wl,--build-id=none
)

target_link_libraries(quiet-rebellion-rfcomm log)