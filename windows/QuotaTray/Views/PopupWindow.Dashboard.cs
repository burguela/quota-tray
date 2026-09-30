using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using QuotaTray.Engine;
using QuotaTray.Services;

namespace QuotaTray.Views;

// The dashboard screen: Total Spend, then a header and grouped card per provider (the Mac
// DashboardContentView, WidgetGroupedListView, and WidgetRowView layouts).
public partial class PopupWindow
{
    private const double RowInset = 14;
    private const double BarRowPadding = 10;
    private const double TextRowPadding = 6;
    private const double CondensedTextRowTop = 2;
    private const double RowInnerSpacing = 4;
    private const double LabelSize = 13;
    private const double SupportingSize = 12;

    private void BuildDashboard(StackPanel body, Theme theme)
    {
        if (_engineError != null)
        {
            body.Children.Add(EngineNotice(_engineError, theme));
        }
        if (_dashboard == null)
        {
            if (_engineError == null)
            {
                body.Children.Add(Text("Loading your usage…", theme.TextSecondary, 13,
                    margin: new Thickness(16, 24, 16, 24), horizontalAlignment: HorizontalAlignment.Center));
            }
            return;
        }

        if (UiSettings.ShowTotalSpend && _dashboard.TotalSpend is { } spend)
        {
            body.Children.Add(BuildTotalSpend(spend, theme));
        }

        var enabled = _dashboard.Providers.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            body.Children.Add(NoProviders(theme));
            return;
        }
        for (var index = 0; index < enabled.Count; index++)
        {
            var section = BuildProvider(enabled[index], theme);
            if (index < enabled.Count - 1)
            {
                section.Margin = new Thickness(0, 0, 0, SectionSpacing);
            }
            body.Children.Add(section);
        }
    }

    /// <summary>
    /// The engine couldn't run or answer: an orange-flagged card above everything else, with a Try Again
    /// button so there's no need to wait for the next automatic update.
    /// </summary>
    private UIElement EngineNotice(string message, Theme theme)
    {
        var row = new DockPanel();
        var icon = Glyph(Glyphs.Warning, theme.Orange, 13, margin: new Thickness(0, 1, 8, 0));
        icon.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(icon);
        var content = new StackPanel();
        content.Children.Add(Text(message, theme.TextPrimary, SupportingSize, wrap: true));
        FrameworkElement retry = _refreshing
            ? Text("Updating…", theme.TextSecondary, SupportingSize)
            : SmallButton(Text("Try Again", theme.TextPrimary, SupportingSize, FontWeights.Medium), theme, _actions.RefreshNow, "Try Again");
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        retry.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(retry);
        row.Children.Add(content);
        var card = Card(row, theme, new Thickness(RowInset, 10, RowInset, 10));
        card.Margin = new Thickness(0, 0, 0, SectionSpacing);
        return card;
    }

    /// <summary>Every provider is off: say so, with a button straight to the switches in Settings.</summary>
    private UIElement NoProviders(Theme theme)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 24, 16, 24), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(Text("No providers are turned on.", theme.TextSecondary, 13,
            horizontalAlignment: HorizontalAlignment.Center, wrap: true));
        var open = SmallButton(Text("Open Settings", theme.TextPrimary, SupportingSize, FontWeights.Medium), theme,
            () => ShowScreen(settings: true), "Open Settings");
        open.HorizontalAlignment = HorizontalAlignment.Center;
        open.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(open);
        return panel;
    }

    // MARK: - Total Spend

    private UIElement BuildTotalSpend(TotalSpendInfo spend, Theme theme)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, SectionSpacing) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        header.Children.Add(Text("Cost", theme.TextPrimary, 14, FontWeights.SemiBold, verticalAlignment: VerticalAlignment.Center));
        var info = Glyph(Glyphs.Info, theme.TextSecondary, 11, stroke: 1.1, margin: new Thickness(5, 1, 0, 0));
        info.ToolTip = $"Only includes {JoinWithAnd(spend.Providers)}.";
        header.Children.Add(info);
        section.Children.Add(header);

        var selectedId = UiSettings.SpendPeriod;
        var period = spend.Periods.FirstOrDefault(p => p.Id == selectedId) ?? spend.Periods.FirstOrDefault();
        var content = new StackPanel();
        content.Children.Add(PeriodPicker(spend.Periods, period?.Id, theme));
        if (period == null || period.Slices.Count == 0)
        {
            content.Children.Add(Text("No cost data for this period", theme.TextSecondary, SupportingSize,
                margin: new Thickness(0, 30, 0, 18), horizontalAlignment: HorizontalAlignment.Center));
        }
        else
        {
            var ringContent = SpendRingContent(period, theme);
            ringContent.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(ringContent);
        }
        var card = Card(content, theme, new Thickness(RowInset, 12, RowInset, 12));
        card.Margin = new Thickness(0, HeaderToCard, 0, 0);
        section.Children.Add(card);
        return section;
    }

    /// <summary>Today / Yesterday / 30 Days, as the Mac's capsule segmented picker.</summary>
    private UIElement PeriodPicker(IReadOnlyList<SpendPeriod> periods, string? selectedId, Theme theme)
    {
        var grid = new UniformGrid { Rows = 1, Columns = periods.Count };
        foreach (var period in periods)
        {
            var selected = period.Id == selectedId;
            var label = Text(period.Label, selected ? theme.TextPrimary : theme.TextSecondary, 11,
                selected ? FontWeights.SemiBold : FontWeights.Medium, horizontalAlignment: HorizontalAlignment.Center);
            var segment = new Pressable
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 4, 12, 5),
                Margin = new Thickness(1, 0, 1, 0),
                Background = selected ? theme.SegmentSelected : Brushes.Transparent,
                Child = label,
            };
            if (selected)
            {
                segment.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.12, Direction = 270 };
            }
            else
            {
                // Unselected periods brighten under the pointer, like the Mac picker's segments.
                segment.MouseEnter += (_, _) => label.Foreground = theme.TextPrimary;
                segment.MouseLeave += (_, _) => label.Foreground = theme.TextSecondary;
            }
            var id = period.Id;
            grid.Children.Add(Clickable(segment, () =>
            {
                UiSettings.SpendPeriod = id;
                Render();
            }, name: period.Label));
        }
        return new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = theme.Segmented,
            Padding = new Thickness(3),
            Child = grid,
        };
    }

    /// <summary>The donut (with the total in its hole) beside the ranked legend.</summary>
    private static FrameworkElement SpendRingContent(SpendPeriod period, Theme theme)
    {
        const double diameter = 104;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(diameter) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ring = new Grid { Width = diameter, Height = diameter };
        ring.Children.Add(new SpendRing(period.Slices.Select(s => (s.Amount, theme.SpendColor(s.ProviderId))).ToList(), diameter));
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var total = Text(period.Center, theme.TextPrimary, 13, FontWeights.SemiBold, horizontalAlignment: HorizontalAlignment.Center);
        total.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        center.Children.Add(total);
        center.Children.Add(Text(period.Unit, theme.TextTertiary, 9, FontWeights.Medium, horizontalAlignment: HorizontalAlignment.Center));
        ring.Children.Add(center);
        grid.Children.Add(ring);

        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        for (var index = 0; index < period.Slices.Count; index++)
        {
            var slice = period.Slices[index];
            var row = new DockPanel { Margin = new Thickness(0, index == 0 ? 0 : 7, 0, 0) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = theme.SpendColor(slice.ProviderId),
                Margin = new Thickness(0, 1, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var value = Text(slice.Value, theme.TextSecondary, SupportingSize, FontWeights.Medium, margin: new Thickness(8, 0, 0, 0));
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(Text(slice.DisplayName, theme.TextPrimary, SupportingSize, trim: true));
            legend.Children.Add(row);
        }
        Grid.SetColumn(legend, 2);
        grid.Children.Add(legend);
        return grid;
    }

    // MARK: - Providers

    private StackPanel BuildProvider(ProviderInfo provider, Theme theme)
    {
        var section = new StackPanel();
        section.Children.Add(ProviderHeader(provider, theme));

        var expanded = _expandedProviders.Contains(provider.Id);
        var always = provider.Rows.Where(r => !r.OnDemand).ToList();
        var onDemand = provider.Rows.Where(r => r.OnDemand).ToList();
        var hasExpandedContent = onDemand.Count > 0 || provider.Links.Count > 0;

        var rows = new StackPanel();
        AddRows(rows, always, theme);
        if (hasExpandedContent)
        {
            rows.Children.Add(ExpandToggle(provider, expanded, theme));
        }
        if (expanded)
        {
            AddRows(rows, onDemand, theme);
            if (provider.Links.Count > 0)
            {
                rows.Children.Add(LinksRow(provider.Links, theme));
            }
        }
        if (rows.Children.Count > 0)
        {
            var card = Card(rows, theme);
            card.Margin = new Thickness(0, HeaderToCard, 0, 0);
            section.Children.Add(card);
        }
        return section;
    }

    /// <summary>Mark, name, and plan on one baseline, then the stale tag and the warning triangle.</summary>
    private static UIElement ProviderHeader(ProviderInfo provider, Theme theme)
    {
        var header = new DockPanel { Margin = new Thickness(10, 2, 12, 2) };
        if (MarkIcon(provider.Id, theme.TextSecondary, 16, new Thickness(0, 0, 5, 0)) is { } mark)
        {
            header.Children.Add(mark);
        }
        if (provider.Notice != null)
        {
            var warning = Glyph(Glyphs.Warning, theme.Orange, 11, margin: new Thickness(5, 1, 0, 0));
            warning.ToolTip = provider.Notice;
            warning.Cursor = System.Windows.Input.Cursors.Help;
            DockPanel.SetDock(warning, Dock.Right);
            header.Children.Add(warning);
        }
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Run(provider.DisplayName) { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = theme.TextPrimary });
        if (!string.IsNullOrEmpty(provider.Plan))
        {
            title.Inlines.Add(new Run("  " + provider.Plan) { FontSize = 11, Foreground = theme.TextSecondary });
        }
        if (provider.Staleness != null && !provider.IsError)
        {
            title.Inlines.Add(new Run("  Outdated") { FontSize = 11, Foreground = theme.TextTertiary, ToolTip = provider.Staleness });
        }
        header.Children.Add(title);
        return header;
    }

    private void AddRows(StackPanel rows, IReadOnlyList<RowInfo> items, Theme theme)
    {
        RowInfo? previous = null;
        foreach (var row in items)
        {
            var condensed = previous != null && IsTextRow(previous) && IsTextRow(row) && row.Kind == "value";
            rows.Children.Add(BuildRow(row, condensed, theme));
            previous = row;
        }
    }

    private static bool IsTextRow(RowInfo row) => row.Kind != "meter";

    private UIElement BuildRow(RowInfo row, bool condensedTop, Theme theme)
    {
        if (row.Kind == "meter")
        {
            return MeterRow(row, theme);
        }
        var top = condensedTop ? CondensedTextRowTop : TextRowPadding;
        var padding = new Thickness(RowInset, top, RowInset, TextRowPadding);
        if (row.Kind == "chart" && row.HasData && row.Chart is { Count: > 0 } points)
        {
            var chart = new DockPanel { Margin = padding };
            var bars = new TrendBars(points.Select(p => p.Value).ToArray());
            var latest = points[^1];
            bars.ToolTip = row.Note == null ? $"{latest.Label}: {latest.Readout}" : $"{latest.Label}: {latest.Readout}\n{row.Note}";
            DockPanel.SetDock(bars, Dock.Right);
            chart.Children.Add(bars);
            chart.Children.Add(Text(row.Title, theme.TextPrimary, SupportingSize, FontWeights.SemiBold,
                verticalAlignment: VerticalAlignment.Center, trim: true));
            return chart;
        }

        var line = new DockPanel { Margin = padding };
        var value = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        var reading = Text(row.Headline, theme.TextPrimary, SupportingSize, horizontalAlignment: HorizontalAlignment.Right);
        if (row.Note != null)
        {
            reading.ToolTip = row.Note;
        }
        value.Children.Add(reading);
        if (row.Subtitle != null)
        {
            value.Children.Add(Text(row.Subtitle, theme.TextSecondary, 10, horizontalAlignment: HorizontalAlignment.Right,
                margin: new Thickness(0, 2, 0, 0)));
        }
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        line.Children.Add(Text(row.Title, theme.TextPrimary, SupportingSize, FontWeights.SemiBold,
            verticalAlignment: VerticalAlignment.Center, trim: true));
        return line;
    }

    /// <summary>Label (with the pace note on the right), the meter, then "58% left" and the reset.</summary>
    private UIElement MeterRow(RowInfo row, Theme theme)
    {
        var panel = new StackPanel { Margin = new Thickness(RowInset, BarRowPadding, RowInset, BarRowPadding) };

        var label = new DockPanel();
        if (row.PaceNote != null)
        {
            var pace = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            if (row.PaceFlame)
            {
                pace.Children.Add(Glyph(Glyphs.Flame, theme.Severity(row.Severity), 10, margin: new Thickness(0, 0, 3, 0)));
            }
            pace.Children.Add(Text(row.PaceNote, theme.TextSecondary, SupportingSize, verticalAlignment: VerticalAlignment.Center));
            pace.ToolTip = row.PaceTooltip;
            DockPanel.SetDock(pace, Dock.Right);
            label.Children.Add(pace);
        }
        label.Children.Add(Text(row.Title, theme.TextPrimary, LabelSize, FontWeights.SemiBold, trim: true));
        panel.Children.Add(label);

        panel.Children.Add(new MeterBar(row.Fraction ?? 0, row.HasData, row.PaceTick, theme.Severity(row.Severity))
        {
            Margin = new Thickness(0, RowInnerSpacing + 1, 0, RowInnerSpacing),
            ToolTip = row.PaceTooltip,
        });

        var reading = new DockPanel();
        if (row.Detail != null)
        {
            var detail = Text(row.Detail, theme.TextSecondary, SupportingSize, margin: new Thickness(8, 0, 0, 0));
            if (row.Note != null)
            {
                detail.ToolTip = row.Note;
            }
            DockPanel.SetDock(detail, Dock.Right);
            reading.Children.Add(detail);
        }
        var headline = Text(row.Headline, theme.TextPrimary, SupportingSize, trim: true);
        if (row.HasData && _dashboard != null)
        {
            // Like the Mac headline, a click flips the global Used/Left meter style.
            var showRemaining = _dashboard.MeterStyle == "used";
            headline.Cursor = System.Windows.Input.Cursors.Hand;
            headline.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _actions.SetMeterStyle(showRemaining);
            };
        }
        reading.Children.Add(headline);
        panel.Children.Add(reading);
        return panel;
    }

    /// <summary>The centered caret that shows or hides On Demand rows and the provider's links.</summary>
    private UIElement ExpandToggle(ProviderInfo provider, bool expanded, Theme theme)
    {
        var chevron = (System.Windows.Shapes.Path)Glyph(expanded ? Glyphs.ChevronUp : Glyphs.ChevronDown, theme.TextSecondary, 10, stroke: 1.7);
        var caret = new Pressable
        {
            Padding = new Thickness(0, 5, 0, 5),
            Margin = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Child = chevron,
        };
        // The chevron darkens under the pointer, so the whole strip reads as the button it is.
        caret.MouseEnter += (_, _) => chevron.Stroke = theme.TextPrimary;
        caret.MouseLeave += (_, _) => chevron.Stroke = theme.TextSecondary;
        var id = provider.Id;
        // A stable id (the name flips between More and Less) so keyboard focus survives the re-render.
        System.Windows.Automation.AutomationProperties.SetAutomationId(caret, $"expand-{id}");
        return Clickable(caret, () =>
        {
            if (!_expandedProviders.Remove(id))
            {
                _expandedProviders.Add(id);
            }
            Render();
        }, name: $"{(expanded ? "Show Less" : "Show More")} for {provider.DisplayName}");
    }

    /// <summary>Quick links as small bordered buttons, up to three per row.</summary>
    private static UIElement LinksRow(IReadOnlyList<LinkInfo> links, Theme theme)
    {
        var grid = new UniformGrid
        {
            Columns = System.Math.Min(3, links.Count),
            Margin = new Thickness(RowInset - 3, TextRowPadding - 3, RowInset - 3, TextRowPadding + 2),
        };
        foreach (var link in links)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Text(link.Label, theme.TextPrimary, SupportingSize, FontWeights.Medium));
            content.Children.Add(Glyph(Glyphs.ArrowUpRight, theme.TextSecondary, 8, stroke: 1.3, margin: new Thickness(4, 1, 0, 0)));
            var url = link.Url;
            var button = SmallButton(content, theme, () => OpenUrl(url), link.Label);
            button.Margin = new Thickness(3);
            grid.Children.Add(button);
        }
        return grid;
    }
}
