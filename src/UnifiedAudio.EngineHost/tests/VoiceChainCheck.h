#pragma once
#include "audio/PluginChain.h"
#include <iostream>

inline int checkVoiceChain(const juce::String& root)
{
    juce::AudioProcessorGraph graph;
    using IO = juce::AudioProcessorGraph::AudioGraphIOProcessor;
    graph.setPlayConfigDetails(2, 2, 48000, 480);
    auto in = graph.addNode(std::make_unique<IO>(IO::audioInputNode));
    auto out = graph.addNode(std::make_unique<IO>(IO::audioOutputNode));
    graph.prepareToPlay(48000, 480);
    PluginChain chain(graph, in->nodeID, out->nodeID);
    juce::AudioPluginFormatManager formats;
    juce::addDefaultFormatsToManager(formats);
    juce::VST3PluginFormat scanner;
    for (const auto& relative : { "rnnoise.vst3", "Kilohearts/kHs Gate.vst3",
        "Kilohearts/kHs 3-Band EQ.vst3", "Kilohearts/kHs Compressor.vst3",
        "Kilohearts/kHs Gain.vst3", "Kilohearts/kHs Limiter.vst3" })
    {
        juce::OwnedArray<juce::PluginDescription> types;
        scanner.findAllTypesForFile(types, juce::File(root).getChildFile(relative).getFullPathName());
        juce::String error;
        if (types.isEmpty() || !chain.addPlugin(formats, *types[0], 48000, 480, error))
        { std::cerr << "Load failed: " << relative << " " << error << '\n'; return 1; }
        auto* processor = graph.getNodeForId(chain.entries().back().node)->getProcessor();
        for (auto* parameter : processor->getParameters())
        {
            if (parameter->getName(100).containsIgnoreCase("threshold")) parameter->setValueNotifyingHost(0);
        }
    }
    int failures = 0;
    for (bool bypass : {false, true, false})
    {
        chain.setBypass(0, bypass);
        chain.rebuildConnections();
        // Flush the same async graph update used by the host's message loop.
        juce::MessageManager::getInstance()->runDispatchLoopUntil(50);
        juce::AudioBuffer<float> buffer(2,480);
        juce::MidiBuffer midi;
        double leftEnergy = 0, rightEnergy = 0;
        float maximumDifference = 0;
        juce::Random random(12345);
        for (int block = 0; block < 400; ++block)
        {
            for (int sample = 0; sample < 480; ++sample)
            {
                const auto value = 0.2f * std::sin(float(block * 480 + sample) * 0.03f)
                    + 0.05f * (random.nextFloat() - 0.5f);
                buffer.setSample(0,sample,value);
                buffer.setSample(1,sample,value);
            }
            graph.processBlock(buffer,midi);
            for (int sample = 0; sample < 480; ++sample)
            {
                const auto l = buffer.getSample(0,sample), r = buffer.getSample(1,sample);
                leftEnergy += l*l; rightEnergy += r*r;
                maximumDifference = juce::jmax(maximumDifference,std::abs(l-r));
            }
        }
        const bool passed = leftEnergy > 0.000001 && rightEnergy > 0.000001 && maximumDifference < 0.00001;
        failures += passed ? 0 : 1;
        std::cout << "RNNoise bypass=" << bypass << " L=" << leftEnergy << " R=" << rightEnergy
                  << " difference=" << maximumDifference << " " << (passed ? "PASS" : "FAIL") << '\n';
    }
    graph.releaseResources();
    return failures == 0 ? 0 : 1;
}
