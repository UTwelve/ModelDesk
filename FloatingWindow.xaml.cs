using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ModelDesk;

public sealed partial class FloatingWindow : Window
{
    private readonly Action restore;
    private DateTimeOffset? lastGenerationSampleAt;
    private InputNonClientPointerSource? nonClientPointer;
    private Windows.Graphics.RectInt32 pinRegion;
    private AppWindow? captionWindow;
    private XamlRoot? captionRoot;
    private bool closing, closed, updatingTitleInset;
    public FloatingWindow(Action restoreMain)
    {
        restore = restoreMain;
        InitializeComponent();
        SystemBackdrop = new TransparentDesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderGrid);
        captionWindow = AppWindow;
        captionWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        captionWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        captionWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.SizeChanged += Caption_SizeChanged;
        PinButton.SizeChanged += Caption_SizeChanged;
        HeaderGrid.SizeChanged += Caption_SizeChanged;
        captionWindow.Changed += CaptionWindow_Changed;
        captionWindow.Closing += CaptionWindow_Closing;
        Closed += FloatingWindow_Closed;
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        double scale = dpi > 0 ? dpi / 96d : 1;
        captionWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(440 * scale), (int)Math.Round(500 * scale)));
        if (captionWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }
    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (closing || closed) return;
        var root = RootGrid.XamlRoot;
        if (root != captionRoot)
        {
            if (captionRoot != null) captionRoot.Changed -= CaptionRoot_Changed;
            captionRoot = root;
            if (captionRoot != null) captionRoot.Changed += CaptionRoot_Changed;
        }
        UpdateCaptionTheme();
        UpdateTitleInset();
    }
    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) => UpdateCaptionTheme();
    private void Caption_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleInset();
    private void CaptionRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleInset();
    private void CaptionWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args) => UpdateTitleInset();
    private void CaptionWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        closing = true;
        DetachCaptionEvents();
    }
    private void FloatingWindow_Closed(object sender, WindowEventArgs args)
    {
        closing = closed = true;
        DetachCaptionEvents();
        Closed -= FloatingWindow_Closed;
    }
    private void DetachCaptionEvents()
    {
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        RootGrid.Loaded -= RootGrid_Loaded;
        RootGrid.SizeChanged -= Caption_SizeChanged;
        PinButton.SizeChanged -= Caption_SizeChanged;
        HeaderGrid.SizeChanged -= Caption_SizeChanged;
        PinButton.Click -= Pin_Click;
        if (captionRoot != null) captionRoot.Changed -= CaptionRoot_Changed;
        captionRoot = null;
        var window = captionWindow;
        captionWindow = null;
        if (window != null)
        {
            window.Changed -= CaptionWindow_Changed;
            window.Closing -= CaptionWindow_Closing;
        }
        var pointer = nonClientPointer;
        nonClientPointer = null;
        pinRegion = default;
        try { pointer?.ClearRegionRects(NonClientRegionKind.Passthrough); }
        catch (ObjectDisposedException) when (closing || closed) { }
        catch (COMException ex) when (IsClosedWindow(ex)) { }
    }
    private static bool IsClosedWindow(COMException ex) => ex.HResult is
        unchecked((int)0x80000013) or unchecked((int)0x80010108) or
        unchecked((int)0x80070578) or unchecked((int)0x80070006);
    private void UpdateTitleInset()
    {
        if (closing || closed || updatingTitleInset) return;
        var root = RootGrid;
        var header = HeaderGrid;
        var pin = PinButton;
        var xamlRoot = root?.XamlRoot;
        var window = captionWindow;
        if (root == null || header == null || pin == null || xamlRoot == null || window == null) return;
        updatingTitleInset = true;
        try
        {
            var titleBar = window.TitleBar;
            if (titleBar == null) return;
            double scale = xamlRoot.RasterizationScale;
            if (scale <= 0) return;
            double captionHeight = titleBar.Height > 0 ? titleBar.Height / scale : 32;
            double rightInset = titleBar.RightInset / scale;
            if (CaptionRow.Height.Value != captionHeight) CaptionRow.Height = new GridLength(captionHeight);
            if (pin.Height != captionHeight) pin.Height = captionHeight;
            var headerMargin = new Thickness(16 + titleBar.LeftInset / scale, 0, rightInset + pin.Width, 0);
            var pinMargin = new Thickness(0, 0, rightInset, 0);
            if (!header.Margin.Equals(headerMargin)) header.Margin = headerMargin;
            if (!pin.Margin.Equals(pinMargin)) pin.Margin = pinMargin;
            if (root.ActualWidth <= rightInset + pin.Width) return;
            // 使用同一布局目标计算命中区，避免 Margin 更新后读取上一帧的控件坐标。
            nonClientPointer ??= InputNonClientPointerSource.GetForWindowId(window.Id);
            var region = new Windows.Graphics.RectInt32((int)Math.Floor((root.ActualWidth - rightInset - pin.Width) * scale), 0,
                (int)Math.Ceiling(pin.Width * scale), (int)Math.Ceiling(captionHeight * scale));
            if (pinRegion.Equals(region)) return;
            nonClientPointer.SetRegionRects(NonClientRegionKind.Passthrough, new[] { region });
            pinRegion = region;
        }
        catch (ObjectDisposedException) when (closing || closed) { }
        catch (COMException ex) when (IsClosedWindow(ex)) { }
        finally { updatingTitleInset = false; }
    }
    private void UpdateCaptionTheme()
    {
        if (closing || closed) return;
        var root = RootGrid;
        var window = captionWindow;
        if (root == null || window == null) return;
        try
        {
            var titleBar = window.TitleBar;
            if (titleBar == null) return;
            bool dark = root.ActualTheme == ElementTheme.Dark;
            titleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            titleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray;
        }
        catch (ObjectDisposedException) when (closing || closed) { }
        catch (COMException ex) when (IsClosedWindow(ex)) { }
    }
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (closing || closed || captionWindow?.Presenter is not OverlappedPresenter presenter) return;
        presenter.IsAlwaysOnTop = PinButton.IsChecked == true;
        bool pinned = presenter.IsAlwaysOnTop;
        PinButton.IsChecked = pinned;
        PinIcon.Glyph = pinned ? "\uE718" : "\uE77A";
        AutomationProperties.SetName(PinButton, pinned ? "取消窗口置顶" : "将窗口置顶");
        ToolTipService.SetToolTip(PinButton, pinned ? "已置顶 · 点击取消" : "未置顶 · 点击置顶");
    }
    public void Update(MonitorDisplayState display, bool newSample)
    {
        if (closing || closed) return;
        StateText.Text = display.StateText;
        SummaryText.Text = display.SummaryText;
        ToolTipService.SetToolTip(SummaryText, display.SummaryText);
        GenerationText.Text = display.GenerationText;
        UpdatedText.Text = display.UpdatedText;
        PromptStageText.Text = display.PromptStageText;
        PromptStageBar.Visibility = display.ShowPromptStage ? Visibility.Visible : Visibility.Collapsed;
        SlotsText.Text = display.ActiveText;
        if (newSample && display.GenerationSampleAt is { } observedAt && observedAt != lastGenerationSampleAt)
        {
            Chart.Add(display.GenerationSampleValue, observedAt);
            lastGenerationSampleAt = observedAt;
        }
    }
    public void SyncChartFrom(SpeedChart source, DateTimeOffset? lastSampleAt)
    {
        source.CopyHistoryTo(Chart);
        lastGenerationSampleAt = lastSampleAt;
    }

    public void UpdateGpu(MonitorDisplayState display, double? utilization, DateTimeOffset? observedAt)
    {
        if (closing || closed) return;
        GpuSummaryText.Text = display.GpuSummaryText;
        GpuDetailText.Text = display.GpuDetailText;
        if (observedAt.HasValue) Chart.AddGpu(utilization, observedAt);
    }
    private void Restore_Click(object sender, RoutedEventArgs e) => restore();
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

internal sealed class TransparentDesktopAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? controller;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;
        controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        ApplyTheme(xamlRoot);
        controller.AddSystemBackdropTarget(connectedTarget);
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (controller == null) return;
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
        ApplyTheme(xamlRoot);
    }
    private void ApplyTheme(XamlRoot? root)
    {
        if (controller == null) return;
        bool dark = (root?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        controller.TintColor = dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        controller.FallbackColor = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        controller.TintOpacity = .18f;
        controller.LuminosityOpacity = dark ? .30f : .40f;
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (controller != null)
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            controller.Dispose();
            controller = null;
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
