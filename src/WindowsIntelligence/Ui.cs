using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsIntelligence;

internal static class Ui
{
    public static readonly Brush Ink = Brush("#ECF0FE"), Muted = Brush("#A4AEC8"), Dim = Brush("#7685A6"), Line = Brush("#29344F"), Accent = Brush("#91B9FF");
    public static readonly Brush Night = Brush("#0D1220"), Panel = Brush("#151E31"), Field = Brush("#10182A");
    public static readonly FontFamily Body = new("SF Pro Text, Segoe UI Variable Text, Microsoft YaHei UI, Microsoft JhengHei UI");
    public static readonly FontFamily Display = new("SF Pro Display, SF Pro Text, Segoe UI Variable Text, Microsoft YaHei UI, Microsoft JhengHei UI");
    public static readonly FontFamily Mono = new("SF Mono, Cascadia Mono, Consolas");

    public static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    public static TextBlock Text(string value, double font = 14, Brush? color = null, bool bold = false) => new()
    {
        Text = L10n.T(value), FontSize = font, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    public static Button Button(string title, Action action, bool primary = false)
    {
        var button = new Button { Content = L10n.T(title), Foreground = primary ? Night : Ink, Background = primary ? Accent : Panel,
            Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0), MinHeight = 32,
            BorderBrush = primary ? Accent : Line, BorderThickness = new Thickness(1), FontWeight = FontWeights.SemiBold };
        var container = new FrameworkElementFactory(typeof(Border));
        container.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        container.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        container.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        container.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        container.AppendChild(label);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = container };
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45));
        template.Triggers.Add(disabled);
        button.Template = template;
        button.MouseEnter += (_, _) => button.Opacity = .82;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(.5, .5);
        button.PreviewMouseLeftButtonDown += (_, _) => GlassMotion.Scale(scale, .96, 85);
        button.PreviewMouseLeftButtonUp += (_, _) => GlassMotion.Scale(scale, 1, 170);
        button.LostMouseCapture += (_, _) => GlassMotion.Scale(scale, 1, 150);
        button.Click += (_, _) => action();
        return button;
    }

    public static StackPanel Row(params UIElement[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    public static Border Card(UIElement child, double padding = 16) => new()
    {
        Child = child, Background = Panel, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), BorderBrush = Line,
        Padding = new Thickness(padding), Margin = new Thickness(0, 0, 0, 12)
    };

    public static void Observe(Task task, Action<Exception> report) => _ = ReportAsync(task, report);
    private static async Task ReportAsync(Task task, Action<Exception> report)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { report(ex); }
    }
}

internal static class GlassMotion
{
    public static bool Enabled(bool reduced) => !reduced && SystemParameters.ClientAreaAnimation;

    public static void Reveal(FrameworkElement item, bool reduced, double distance, int milliseconds)
    {
        if (!Enabled(reduced)) return;
        var y = new TranslateTransform();
        item.RenderTransform = y;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        y.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.35, 1, duration) { FillBehavior = FillBehavior.Stop });
    }

    public static void Scale(ScaleTransform scale, double goal, int duration)
    {
        var x = scale.ScaleX;
        scale.SetCurrentValue(ScaleTransform.ScaleXProperty, goal);
        scale.SetCurrentValue(ScaleTransform.ScaleYProperty, goal);
        if (!Enabled(false)) return;
        var motion = new DoubleAnimation(x, goal, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
    }
}
