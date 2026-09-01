using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace UnifiedAudio.Models;

public sealed class ProfileCard
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string DeviceSummary { get; init; }
    public required string StatusText { get; init; }
    public required string ActiveLabel { get; init; }
    public required string EditLabel { get; init; }
    public required string ActionLabel { get; init; }
    public required string DeleteLabel { get; init; }
    public required Brush CardBrush { get; init; }
    public required Brush BorderBrush { get; init; }
    public required Visibility ActiveVisibility { get; init; }
}
