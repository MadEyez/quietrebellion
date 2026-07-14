cmake_minimum_required(VERSION 3.22.1)
project("quiet-rebellion")

add_library(quiet-rebellion-rfcomm SHARED rfcomm.c)
target_link_libraries(quiet-rebellion-rfcomm log)

