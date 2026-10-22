# Generated Cmake Pico project file

cmake_minimum_required(VERSION 3.13)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Initialise pico_sdk from installed location
# (note this can come from environment, CMake cache etc)

# == DO NOT EDIT THE FOLLOWING LINES for the Raspberry Pi Pico VS Code Extension to work ==
if(WIN32)
    set(USERHOME $ENV{USERPROFILE})
else()
    set(USERHOME $ENV{HOME})
endif()
set(sdkVersion 2.3.1)
set(toolchainVersion 15_2_Rel1)
set(picotoolVersion 2.3.1)
set(picoVscode ${USERHOME}/.pico-sdk/cmake/pico-vscode.cmake)
if (EXISTS ${picoVscode})
    include(${picoVscode})
endif()
# ====================================================================================
set(PICO_BOARD pico2 CACHE STRING "Board type")

# Pull in Raspberry Pi Pico SDK (must be before project)
include(pico_sdk_import.cmake)

file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/../VERSION" UAB_VERSION LIMIT_COUNT 1)
project(SoundConvertBoard VERSION ${UAB_VERSION} LANGUAGES C CXX ASM)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/../VERSION")

# Initialise the Raspberry Pi Pico SDK
set(PICO_TINYUSB_PATH "D:/GithubClone/tinyusb" CACHE PATH "TinyUSB UAC1 source")
set(PICO_FLASH_SIZE_BYTES 2097152 CACHE STRING "W5500-EVB-Pico2 physical flash" FORCE)
pico_sdk_init()

file(STRINGS "${PICO_TINYUSB_PATH}/src/class/audio/audio_device.h" UAB_TINYUSB_DIAGNOSTIC_API REGEX "tud_audio_n_get_feedback_state")
if(NOT UAB_TINYUSB_DIAGNOSTIC_API)
    message(FATAL_ERROR "Apply vendor/tinyusb-feedback-diagnostics.patch to the pinned TinyUSB checkout (tools/Apply-TinyUsbDiagnostics.ps1) before building")
endif()

# Add executable. Default name is the project name, version 0.1

add_executable(SoundConvertBoard SoundConvertBoard.c uab_indicator.c uab_boot.c uab_network.c uab_clock_observation.c uab_audio_packet.c uab_audio_tx.c uab_audio_fec.c uab_crypto.c uab_config_store.c uab_watchdog.c
        usb/usb_control_notify.c usb/usb_audio.c usb/usb_microphone.c usb/usb_descriptors.c usb/usb_host_control.c
        vendor/ioLibrary_Driver/Ethernet/wizchip_conf.c
        vendor/ioLibrary_Driver/Ethernet/socket.c
        vendor/ioLibrary_Driver/Ethernet/W5500/w5500.c
        uab_dhcp.c
        vendor/cJSON/cJSON.c
        ${PICO_SDK_PATH}/lib/mbedtls/library/sha256.c
        ${PICO_SDK_PATH}/lib/mbedtls/library/constant_time.c
        ${PICO_SDK_PATH}/lib/mbedtls/library/platform_util.c)
pico_add_linker_script_override_path(SoundConvertBoard ${CMAKE_CURRENT_LIST_DIR}/linker FILES pico_flash_region.ld)

pico_set_program_name(SoundConvertBoard "SoundConvertBoard")
pico_set_program_version(SoundConvertBoard "${PROJECT_VERSION}")

# Modify the below lines to enable/disable output over UART/USB
pico_enable_stdio_uart(SoundConvertBoard 1)
pico_enable_stdio_usb(SoundConvertBoard 0)

# Add the standard library to the build
target_link_libraries(SoundConvertBoard
        pico_stdlib hardware_pwm)

# Add the standard include files to the build
target_include_directories(SoundConvertBoard PRIVATE
        ${CMAKE_CURRENT_LIST_DIR}
        ${CMAKE_CURRENT_LIST_DIR}/usb
        ${CMAKE_CURRENT_LIST_DIR}/vendor/ioLibrary_Driver/Ethernet
        ${CMAKE_CURRENT_LIST_DIR}/vendor/ioLibrary_Driver/Internet/DHCP
        ${CMAKE_CURRENT_LIST_DIR}/vendor/cJSON
        ${PICO_SDK_PATH}/lib/mbedtls/include
        ${PICO_SDK_PATH}/lib/mbedtls/library
)
target_compile_definitions(SoundConvertBoard PRIVATE _WIZCHIP_=5500
        UAB_FIRMWARE_VERSION="V${PROJECT_VERSION}" UAB_VERSION_MAJOR=${PROJECT_VERSION_MAJOR}
        UAB_VERSION_MINOR=${PROJECT_VERSION_MINOR} UAB_VERSION_PATCH=${PROJECT_VERSION_PATCH}
        PICO_CORE1_STACK_SIZE=2048 PICO_STACK_SIZE=4096 PICO_FLASH_SIZE_BYTES=2097152
        MBEDTLS_CONFIG_FILE="uab_mbedtls_config.h" CJSON_NESTING_LIMIT=16)

# Add any user requested libraries
target_link_libraries(SoundConvertBoard 
        hardware_spi
        hardware_dma
        pico_multicore pico_unique_id pico_rand pico_flash hardware_flash hardware_watchdog tinyusb_device m
        )

pico_add_extra_outputs(SoundConvertBoard)





option(UAB_USB_CONTROL_NOTIFY "Experimental UAC1 status interrupt; OFF until Host acceptance" OFF)
target_compile_definitions(SoundConvertBoard PRIVATE UAB_USB_CONTROL_NOTIFY=$<BOOL:${UAB_USB_CONTROL_NOTIFY}>)
