cmake_minimum_required(VERSION 3.22)
project(UnifiedAudioEngineHost VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(MSVC)
    add_compile_options(/utf-8 /W4)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

include(FetchContent)
FetchContent_Declare(
    JUCE
    GIT_REPOSITORY https://github.com/juce-framework/JUCE.git
    GIT_TAG        8.0.13
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(JUCE)

add_library(UnifiedAudioEngineCore STATIC
    src/audio/AsyncAudioBridge.cpp
    src/audio/FeedbackLoopPolicy.cpp
    src/audio/Metering.cpp
    src/audio/GraphConnections.cpp
    src/audio/ScanCoordinator.cpp
    src/state/Persistence.cpp
    src/state/PluginScanCache.cpp
)
target_compile_features(UnifiedAudioEngineCore PUBLIC cxx_std_20)
target_include_directories(UnifiedAudioEngineCore PUBLIC src)
target_link_libraries(UnifiedAudioEngineCore PUBLIC
    juce::juce_audio_basics
    juce::juce_audio_processors
    juce::juce_core
    juce::juce_cryptography
    juce::juce_data_structures
)

juce_add_gui_app(UnifiedAudioEngineHost
    PRODUCT_NAME "UnifiedAudio Engine Host"
    COMPANY_NAME "UnifiedAudio"
)
target_sources(UnifiedAudioEngineHost PRIVATE
    src/HostMain.cpp
    src/audio/AudioEngine.cpp
    src/audio/FeedbackLoopDetector.cpp
    src/audio/PluginChain.cpp
    src/audio/SystemLoopbackCapture.cpp
    src/audio/ProcessLoopbackCapture.cpp
    src/audio/AudioSessionCatalog.cpp
    src/audio/SpectrumAnalyzer.cpp
)
target_compile_definitions(UnifiedAudioEngineHost PRIVATE
    JUCE_WASAPI=1
    JUCE_DIRECTSOUND=0
    JUCE_ASIO=0
    JUCE_PLUGINHOST_VST3=1
    JUCE_USE_CURL=0
    JUCE_WEB_BROWSER=0
)
target_link_libraries(UnifiedAudioEngineHost PRIVATE
    UnifiedAudioEngineCore
    juce::juce_audio_devices
    juce::juce_audio_utils
    juce::juce_dsp
    juce::juce_gui_basics
    juce::juce_gui_extra
    juce::juce_recommended_config_flags
    juce::juce_recommended_warning_flags
    advapi32
    ole32
    propsys
    mmdevapi
)

juce_add_console_app(UnifiedAudioEngineTests PRODUCT_NAME "UnifiedAudioEngineTests")
target_sources(UnifiedAudioEngineTests PRIVATE tests/EngineTests.cpp)
target_compile_definitions(UnifiedAudioEngineTests PRIVATE JUCE_PLUGINHOST_VST3=1)
target_link_libraries(UnifiedAudioEngineTests PRIVATE
    UnifiedAudioEngineCore
    juce::juce_audio_processors
    juce::juce_core
    juce::juce_recommended_config_flags
    juce::juce_recommended_warning_flags
)
