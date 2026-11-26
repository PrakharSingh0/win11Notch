using System.Windows;
using System.Windows.Controls;
using Win11Notch.Services;

namespace Win11Notch.Views.Controls;

/// <summary>
/// Selects the adaptive Bento widget DataTemplate corresponding to each HomeSlotItem.WidgetKind.
/// </summary>
public sealed class HomeWidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DateClockTemplate          { get; set; }
    public DataTemplate? MusicPlayerTemplate        { get; set; }
    public DataTemplate? UpcomingAgendaTemplate     { get; set; }
    public DataTemplate? PowerAndAudioTemplate      { get; set; }
    public DataTemplate? SystemResourcesTemplate    { get; set; }
    public DataTemplate? TimersAndWaterTemplate     { get; set; }
    public DataTemplate? ShelfAndClipboardTemplate  { get; set; }
    public DataTemplate? ToolsAndBrightnessTemplate { get; set; }
    public DataTemplate? QuickActionsTemplate       { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not HomeSlotItem slot) return base.SelectTemplate(item, container);

        return slot.WidgetKind switch
        {
            HomeWidgetKind.DateClock          => DateClockTemplate,
            HomeWidgetKind.MusicPlayer        => MusicPlayerTemplate,
            HomeWidgetKind.UpcomingAgenda     => UpcomingAgendaTemplate,
            HomeWidgetKind.PowerAndAudio      => PowerAndAudioTemplate,
            HomeWidgetKind.SystemResources    => SystemResourcesTemplate,
            HomeWidgetKind.TimersAndWater     => TimersAndWaterTemplate,
            HomeWidgetKind.ShelfAndClipboard  => ShelfAndClipboardTemplate,
            HomeWidgetKind.ToolsAndBrightness => ToolsAndBrightnessTemplate,
            HomeWidgetKind.QuickActions       => QuickActionsTemplate,
            _                                 => base.SelectTemplate(item, container)
        };
    }
}
