cmake_minimum_required(VERSION 3.20)
project(forge_cpp_worker LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(PkgConfig REQUIRED)
find_package(nlohmann_json 3.11 REQUIRED)
pkg_check_modules(LIBCURL REQUIRED IMPORTED_TARGET libcurl)
pkg_check_modules(RABBITMQ REQUIRED IMPORTED_TARGET librabbitmq)

add_library(forge_worker_api src/api_client.cpp)
target_include_directories(forge_worker_api
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
)
target_link_libraries(forge_worker_api
    PUBLIC nlohmann_json::nlohmann_json PkgConfig::LIBCURL
)
target_compile_options(forge_worker_api
    PRIVATE -Wall -Wextra -Wpedantic
)

add_library(forge_worker_core
    src/log_analysis.cpp
    src/job_handler.cpp
)

target_include_directories(forge_worker_core
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_options(forge_worker_core
    PRIVATE -Wall -Wextra -Wpedantic
)

add_executable(log_analysis_tests
    tests/log_analysis_tests.cpp
)

target_link_libraries(log_analysis_tests
    PRIVATE forge_worker_core
)

enable_testing()
add_test(NAME log_analysis_tests COMMAND log_analysis_tests)


add_executable(forge_cpp_worker
    src/main.cpp
)
target_link_libraries(forge_cpp_worker
    PRIVATE
        forge_worker_api
        forge_worker_core
        PkgConfig::RABBITMQ
)
target_compile_options(forge_cpp_worker
    PRIVATE -Wall -Wextra -Wpedantic
)


add_executable(job_handler_tests
    tests/job_handler_tests.cpp
)
target_link_libraries(job_handler_tests
    PRIVATE forge_worker_core
)
target_compile_options(job_handler_tests
    PRIVATE -Wall -Wextra -Wpedantic
)
add_test(NAME job_handler_tests COMMAND job_handler_tests)
