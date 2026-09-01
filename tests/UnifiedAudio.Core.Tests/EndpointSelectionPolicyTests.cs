using UnifiedAudio.Core.Audio;
using UnifiedAudio.Core.Models;

namespace UnifiedAudio.Core.Tests;

public sealed class EndpointSelectionPolicyTests
{
    [Theory]
    [InlineData("Micro USB (KT USB Audio)", EndpointKind.Physical)]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", EndpointKind.VirtualCableRender)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", EndpointKind.VirtualCableCapture)]
    [InlineData("CABLE-A Input (VB-Audio Cable A)", EndpointKind.VirtualCableRender)]
    [InlineData("CABLE-B Output (VB-Audio Cable B)", EndpointKind.VirtualCableCapture)]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", EndpointKind.VirtualCableRender)]
    [InlineData("VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)", EndpointKind.VirtualCableCapture)]
    [InlineData("Line 1 (Virtual Audio Cable)", EndpointKind.VirtualCableCapture)]
    [InlineData("", EndpointKind.Unknown)]
    [InlineData("   ", EndpointKind.Unknown)]
    public void ClassifiesPhysicalAndVirtualEndpoints(string name, EndpointKind expected)
    {
        var flow = expected is EndpointKind.VirtualCableCapture
            ? EndpointFlow.Capture
            : EndpointFlow.Render;
        if (expected == EndpointKind.Physical)
            flow = EndpointFlow.Capture;

        var endpoint = Endpoint(name, flow);

        Assert.Equal(expected, EndpointSelectionPolicy.Classify(endpoint));
    }

    [Fact]
    public void PhysicalCaptureFilterExcludesCableOutputAndUnavailableEndpoints()
    {
        var endpoints = new[]
        {
            Endpoint("CABLE Output (VB-Audio Virtual Cable)", EndpointFlow.Capture),
            Endpoint("Microphone USB", EndpointFlow.Capture),
            Endpoint("Disconnected Mic", EndpointFlow.Capture, EndpointAvailability.Disconnected),
            Endpoint("Speakers", EndpointFlow.Render),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render),
            Endpoint("", EndpointFlow.Capture)
        };

        var result = EndpointSelectionPolicy.GetPhysicalCaptureInputs(endpoints);

        var selected = Assert.Single(result);
        Assert.Equal("Microphone USB", selected.Name);
        Assert.True(EndpointSelectionPolicy.IsPhysicalCapture(selected));
        Assert.False(EndpointSelectionPolicy.IsPhysicalCapture(endpoints[0]));
    }

    [Fact]
    public void PhysicalCaptureFilterPreservesInventoryOrderAndDoesNotChooseImplicitly()
    {
        var endpoints = new[]
        {
            Endpoint("Second Mic", EndpointFlow.Capture),
            Endpoint("First Mic", EndpointFlow.Capture)
        };

        var result = EndpointSelectionPolicy.GetPhysicalCaptureInputs(endpoints);

        Assert.Equal(["Second Mic", "First Mic"], result.Select(endpoint => endpoint.Name));
    }

    [Fact]
    public void NewFinalRenderSelectionPrefersExactStandardCableInput()
    {
        var endpoints = new[]
        {
            Endpoint("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", EndpointFlow.Render),
            Endpoint("CABLE-B Input (VB-Audio Cable B)", EndpointFlow.Render),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render),
            Endpoint("Line 1 (Virtual Audio Cable)", EndpointFlow.Render)
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints);

        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", result.Selected?.Name);
        Assert.True(result.SuggestedNewSelection);
        Assert.False(result.PreservedExisting);
        Assert.False(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void RuntimePhysicalRenderWithoutSavedSelectionDoesNotBlockStandardCableSuggestion()
    {
        var endpoints = new[]
        {
            Endpoint("Speakers (default)", EndpointFlow.Render, id: "runtime-default"),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, id: "standard-cable")
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved: null);

        Assert.Equal("standard-cable", result.Selected?.Id);
        Assert.True(result.SuggestedNewSelection);
        Assert.False(result.PreservedExisting);
    }

    [Fact]
    public void NewFinalRenderSelectionUsesCablesWithoutAssumingAOrB()
    {
        var endpoints = new[]
        {
            Endpoint("Line 1 (Virtual Audio Cable)", EndpointFlow.Render),
            Endpoint("VoiceMeeter Input", EndpointFlow.Render),
            Endpoint("VB-Audio Hi-Fi Cable", EndpointFlow.Render),
            Endpoint("CABLE-B Input (VB-Audio Cable B)", EndpointFlow.Render)
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints);

        Assert.Equal("CABLE-B Input (VB-Audio Cable B)", result.Selected?.Name);
    }

    [Fact]
    public void NewFinalRenderSelectionUsesVbAudioBeforeVoicemeeterAndVac()
    {
        var endpoints = new[]
        {
            Endpoint("Line 1 (Virtual Audio Cable)", EndpointFlow.Render),
            Endpoint("VoiceMeeter Input", EndpointFlow.Render),
            Endpoint("VB-Audio Hi-Fi Cable", EndpointFlow.Render)
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints);

        Assert.Equal("VB-Audio Hi-Fi Cable", result.Selected?.Name);
    }

    [Fact]
    public void NewFinalRenderSelectionUsesVoicemeeterBeforeGenericVirtualAudioCable()
    {
        var endpoints = new[]
        {
            Endpoint("Line 1 (Virtual Audio Cable)", EndpointFlow.Render),
            Endpoint("VoiceMeeter Input", EndpointFlow.Render)
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints);

        Assert.Equal("VoiceMeeter Input", result.Selected?.Name);
    }

    [Fact]
    public void NewFinalRenderSelectionReturnsNoSelectionWhenNoCableExists()
    {
        var endpoints = new[]
        {
            Endpoint("Speakers", EndpointFlow.Render),
            Endpoint("Microphone", EndpointFlow.Capture)
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints);

        Assert.False(result.HasSelection);
        Assert.False(result.SuggestedNewSelection);
        Assert.False(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void ExistingRenderIdIsPreservedWhenFriendlyNameChanges()
    {
        var endpoints = new[]
        {
            Endpoint("Renamed final cable", EndpointFlow.Render, id: "final-id"),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, id: "new-id")
        };
        var saved = new EndpointReference
        {
            Id = "FINAL-ID",
            Name = "Old final cable",
            Flow = EndpointFlow.Render
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.Equal("final-id", result.Selected?.Id);
        Assert.True(result.PreservedExisting);
        Assert.False(result.SuggestedNewSelection);
        Assert.False(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void ExistingNameOnlySelectionIsPreservedWhenItIsAvailable()
    {
        var endpoints = new[]
        {
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render)
        };
        var saved = new EndpointReference
        {
            Name = " CABLE Input (VB-Audio Virtual Cable) ",
            Flow = EndpointFlow.Render
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", result.Selected?.Name);
        Assert.True(result.PreservedExisting);
    }

    [Fact]
    public void SavedNameOnlySelectionIsNotGuessedWhenFriendlyNameIsDuplicated()
    {
        var endpoints = new[]
        {
            Endpoint("Renamed cable", EndpointFlow.Render, id: "first-id"),
            Endpoint("Renamed cable", EndpointFlow.Render, id: "second-id")
        };
        var saved = new EndpointReference { Name = "Renamed cable", Flow = EndpointFlow.Render };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.False(result.HasSelection);
        Assert.True(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void SavedIdIsNotGuessedWhenStableIdIsDuplicated()
    {
        var endpoints = new[]
        {
            Endpoint("First cable", EndpointFlow.Render, id: "duplicate-id"),
            Endpoint("Second cable", EndpointFlow.Render, id: "duplicate-id")
        };
        var saved = new EndpointReference { Id = "duplicate-id", Flow = EndpointFlow.Render };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.False(result.HasSelection);
        Assert.True(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void StaleExistingSelectionIsNotReplacedByAnotherCable()
    {
        var endpoints = new[]
        {
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, id: "standard-id"),
            Endpoint("CABLE-B Input (VB-Audio Cable B)", EndpointFlow.Render, id: "cable-b-id")
        };
        var saved = new EndpointReference
        {
            Id = "disconnected-id",
            Name = "Previously selected cable",
            Flow = EndpointFlow.Render
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.False(result.HasSelection);
        Assert.True(result.ExistingSelectionUnavailable);
        Assert.False(result.SuggestedNewSelection);
    }

    [Fact]
    public void DisconnectedSavedIdIsNotReplacedByAWorkingCable()
    {
        var endpoints = new[]
        {
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, id: "saved-id", availability: EndpointAvailability.Disconnected),
            Endpoint("CABLE-B Input (VB-Audio Cable B)", EndpointFlow.Render, id: "other-id")
        };
        var saved = new EndpointReference { Id = "saved-id", Flow = EndpointFlow.Render };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.False(result.HasSelection);
        Assert.True(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void ExistingPhysicalRenderIsPreservedRatherThanSilentlyReplaced()
    {
        var endpoints = new[]
        {
            Endpoint("Speakers", EndpointFlow.Render, id: "speakers-id"),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, id: "cable-id")
        };
        var saved = new EndpointReference { Id = "speakers-id", Flow = EndpointFlow.Render };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.Equal("speakers-id", result.Selected?.Id);
        Assert.True(result.PreservedExisting);
    }

    [Fact]
    public void SavedCaptureEndpointCannotResolveAsFinalRender()
    {
        var endpoints = new[]
        {
            Endpoint("CABLE Output (VB-Audio Virtual Cable)", EndpointFlow.Capture, id: "saved-id"),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, id: "render-id")
        };
        var saved = new EndpointReference { Id = "saved-id", Flow = EndpointFlow.Capture };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints, saved);

        Assert.False(result.HasSelection);
        Assert.True(result.ExistingSelectionUnavailable);
    }

    [Fact]
    public void RenderSideCABLEOutputLabelIsNotSuggestedAsFinalRender()
    {
        var endpoints = new[]
        {
            Endpoint("CABLE Output (VB-Audio Virtual Cable)", EndpointFlow.Render),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render)
        };

        var result = EndpointSelectionPolicy.ResolveFinalVirtualRender(endpoints);

        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", result.Selected?.Name);
    }

    [Fact]
    public void FinalRenderSelectionIsDeterministicForEqualPriorityCandidates()
    {
        var firstOrder = new[]
        {
            Endpoint("CABLE-B Input", EndpointFlow.Render, id: "b-id"),
            Endpoint("CABLE-A Input", EndpointFlow.Render, id: "a-id")
        };
        var secondOrder = firstOrder.Reverse().ToArray();

        var first = EndpointSelectionPolicy.ResolveFinalVirtualRender(firstOrder);
        var second = EndpointSelectionPolicy.ResolveFinalVirtualRender(secondOrder);

        Assert.Equal(first.Selected?.Id, second.Selected?.Id);
        Assert.Equal("a-id", first.Selected?.Id);
    }

    private static AudioEndpointDescriptor Endpoint(
        string name,
        EndpointFlow flow,
        EndpointAvailability availability = EndpointAvailability.Available,
        string? id = null) => new()
        {
            Id = id ?? name,
            Name = name,
            Flow = flow,
            Availability = availability
        };
}
