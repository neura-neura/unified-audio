using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace UnifiedAudio.Helpers;

public static class UiLocalizer
{
    public static void Apply(DependencyObject root) => ApplyCore(root, []);

    private static void ApplyCore(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root)) return;
        LocalizeElement(root);

        if (root is Panel panel)
            foreach (var child in panel.Children) ApplyCore(child, visited);
        if (root is Border { Child: DependencyObject borderChild }) ApplyCore(borderChild, visited);
        if (root is ContentControl { Content: DependencyObject content }) ApplyCore(content, visited);
        if (root is ItemsControl itemsControl)
            foreach (var item in itemsControl.Items.OfType<DependencyObject>()) ApplyCore(item, visited);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            ApplyCore(VisualTreeHelper.GetChild(root, index), visited);
    }

    private static void LocalizeElement(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                text.Text = LiteralCatalog.Get(text.Text);
                break;
            case TextBox textBox:
                textBox.Header = LocalizeObject(textBox.Header);
                textBox.PlaceholderText = LiteralCatalog.Get(textBox.PlaceholderText);
                break;
            case NumberBox numberBox:
                numberBox.Header = LocalizeObject(numberBox.Header);
                numberBox.PlaceholderText = LiteralCatalog.Get(numberBox.PlaceholderText);
                break;
            case ComboBox comboBox:
                comboBox.Header = LocalizeObject(comboBox.Header);
                comboBox.PlaceholderText = LiteralCatalog.Get(comboBox.PlaceholderText);
                break;
            case Slider slider:
                slider.Header = LocalizeObject(slider.Header);
                break;
            case ToggleSwitch toggleSwitch:
                toggleSwitch.Header = LocalizeObject(toggleSwitch.Header);
                toggleSwitch.OnContent = LocalizeObject(toggleSwitch.OnContent);
                toggleSwitch.OffContent = LocalizeObject(toggleSwitch.OffContent);
                break;
            case Expander expander:
                expander.Header = LocalizeObject(expander.Header);
                break;
            case InfoBar infoBar:
                infoBar.Title = LiteralCatalog.Get(infoBar.Title);
                infoBar.Message = LiteralCatalog.Get(infoBar.Message);
                break;
            case ContentControl contentControl when contentControl.Content is string value:
                contentControl.Content = LiteralCatalog.Get(value);
                break;
        }

        var automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName))
            AutomationProperties.SetName(element, LiteralCatalog.Get(automationName));
    }

    private static object? LocalizeObject(object? value) =>
        value is string text ? LiteralCatalog.Get(text) : value;
}
