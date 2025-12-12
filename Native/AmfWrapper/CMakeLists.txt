cmake_minimum_required(VERSION 3.20)
project(AmfWrapper VERSION 1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# AMF SDK path
set(AMF_SDK_PATH "${CMAKE_CURRENT_SOURCE_DIR}/amf/public" CACHE PATH "Path to AMF SDK public folder")

if(NOT EXISTS "${AMF_SDK_PATH}/include/core/Factory.h")
    message(FATAL_ERROR "AMF SDK not found at ${AMF_SDK_PATH}. Run: git clone https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git")
endif()

# Create shared library
add_library(AmfWrapper SHARED
    AmfWrapper.cpp
    AmfWrapper.h
    # AMF common source files
    ${AMF_SDK_PATH}/common/AMFFactory.cpp
    ${AMF_SDK_PATH}/common/Thread.cpp
    ${AMF_SDK_PATH}/common/AMFSTL.cpp
    ${AMF_SDK_PATH}/common/TraceAdapter.cpp
)

# Include directories
target_include_directories(AmfWrapper PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/amf
    ${AMF_SDK_PATH}/include
    ${AMF_SDK_PATH}/common
)

# Windows-specific settings
if(WIN32)
    target_compile_definitions(AmfWrapper PRIVATE
        AMFWRAPPER_EXPORTS
        WIN32_LEAN_AND_MEAN
        NOMINMAX
        _CRT_SECURE_NO_WARNINGS
    )
    
    target_link_libraries(AmfWrapper PRIVATE
        d3d11
        dxgi
    )
endif()

# Output directory - copy to C# project
set(OUTPUT_DIR "${CMAKE_SOURCE_DIR}/../../bin/Release/net9.0-windows10.0.26100.0")
add_custom_command(TARGET AmfWrapper POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory ${OUTPUT_DIR}
    COMMAND ${CMAKE_COMMAND} -E copy $<TARGET_FILE:AmfWrapper> ${OUTPUT_DIR}
    COMMENT "Copying AmfWrapper.dll to ${OUTPUT_DIR}"
)

# Installation
install(TARGETS AmfWrapper
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
)
