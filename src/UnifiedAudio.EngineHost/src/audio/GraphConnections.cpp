#include "audio/GraphConnections.h"

std::vector<ChannelConnection> computeChainConnections (const std::vector<NodeChannels>& seq)
{
    std::vector<ChannelConnection> result;

    for (size_t hop = 0; hop + 1 < seq.size(); ++hop)
    {
        const int n = juce::jmin (seq[hop].numOuts, seq[hop + 1].numIns);
        for (int ch = 0; ch < n; ++ch)
            result.push_back ({ seq[hop].id, ch, seq[hop + 1].id, ch });
        // A mono effect (e.g. RNNoise) must feed both sides of the next
        // stereo effect/output, including after bypass or reordering.
        if (seq[hop].numOuts == 1 && seq[hop + 1].numIns == 2)
            result.push_back ({ seq[hop].id, 0, seq[hop + 1].id, 1 });
    }

    return result;
}
