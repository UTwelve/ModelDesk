using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ModelDesk.Models;
using ModelDesk.Services;
using Windows.ApplicationModel.DataTransfer;

namespace ModelDesk;

public sealed partial class MainWindow : Window
{
    private AppSettings settings;
    private readonly ModelService service;
    private readonly GpuMonitor gpu = new();
    private TrayIcon? tray;
    private FloatingWindow? floating;
    private Telemetry? telemetry;
    private readonly MonitorDisplayState monitorDisplay = new();
    private bool exiting, actionPending, dialogOpen;
    private readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private ScrollViewer? logScroller;
    private int logDirty;
    private bool logPointerDown, replacingLogText, logAutoScrolling;
    public MainWindow(bool validationOnly = false)
    {
        InitializeComponent();
        // Gallery MainWindow.SetWindowProperties：原生 TitleBar 与导航分属两行。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ShellTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        RootGrid.ActualThemeChanged += (_, _) => GalleryTitleBarHelper.ApplySystemThemeToCaptionButtons(this, RootGrid.ActualTheme);
        AppWindow.Changed += (_, _) => AdjustNavigationViewMargin();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1160, 860));
        settings = AppSettings.Load(out string? error);
        service = new ModelService(settings);
        FillSettings();
        AppWindow.Closing += (_, e) =>
        {
            if (exiting || validationOnly) return;
            e.Cancel = true;
            if (tray?.Available == true) AppWindow.Hide();
            else ShowNotice("无法收起", "系统托盘不可用。请使用任务栏窗口；模型不会被自动停止。", InfoBarSeverity.Warning);
        };
        Closed += (_, _) => { logTimer.Stop(); floating?.Close(); tray?.Dispose(); gpu.Dispose(); service.Dispose(); };
        LogBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(LogPointerPressed), true);
        LogBox.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(LogPointerReleased), true);
        LogBox.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(LogPointerReleased), true);
        LogBox.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(LogPointerReleased), true);
        logTimer.Tick += (_, _) => RefreshLogs();
        if (validationOnly) return;
        try
        {
            tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), Restore, ShowFloating,
                () => DispatcherQueue.TryEnqueue(() => _ = RequestExit()),
                () => DispatcherQueue.TryEnqueue(() => { Restore(); Start_Click(this, new RoutedEventArgs()); }),
                () => DispatcherQueue.TryEnqueue(() => { Restore(); Stop_Click(this, new RoutedEventArgs()); }),
                () => (!actionPending && !dialogOpen && !service.HasOwnedProcess && !service.IsStarting && telemetry?.External != true,
                    !actionPending && !dialogOpen && service.HasOwnedProcess));
            if (!tray.Available) ShowNotice("托盘不可用", "关闭时会保留主窗口，避免后台失联。", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ShowNotice("托盘初始化失败", ex.Message, InfoBarSeverity.Warning); }
        service.Updated += sample => DispatcherQueue.TryEnqueue(() => Render(sample));
        service.LogAdded += _ => System.Threading.Interlocked.Exchange(ref logDirty, 1);
        logTimer.Start();
        gpu.Updated += sample => DispatcherQueue.TryEnqueue(() => Render(sample));
        if (error != null) ShowNotice("配置提示", error, InfoBarSeverity.Warning);
        service.StartPolling();
        gpu.Start();
    }
    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        GalleryTitleBarHelper.ApplySystemThemeToCaptionButtons(this, RootGrid.ActualTheme);
        AdjustNavigationViewMargin();
        Navigation.SelectedItem ??= OverviewNavigationItem;
    }
    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
        => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    private void AdjustNavigationViewMargin()
    {
        // Gallery 对 WinUI #9934 的修复：消除标题栏与导航顶部间隙。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            Navigation.Margin = presenter.State == OverlappedPresenterState.Maximized
                ? new Thickness(0, -1, 0, 0) : new Thickness(0, -2, 0, 0);
    }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (OverviewPage == null) return;
        string page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "overview";
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.Visibility = page == "logs" ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page == "settings" ? "设置" : page == "logs" ? "任务与日志" : "概览";
        PageDescription.Text = page == "settings"
            ? "设置在下次启动前生效；不修改原 BAT。运行期间禁止修改，避免监控目标漂移。"
            : page == "logs" ? "查看当前任务和本地日志；可暂停跟随、选择与复制。"
            : "管理本地模型，查看真实推理与硬件状态。";
        if (page == "logs") RefreshLogs();
    }
    private void LogBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (logScroller != null) return;
        logScroller = FindLogScroller(LogBox);
        if (logScroller != null) logScroller.ViewChanged += LogScroller_ViewChanged;
        System.Threading.Interlocked.Exchange(ref logDirty, 1);
        RefreshLogs();
    }
    private static ScrollViewer? FindLogScroller(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindLogScroller(child) is { } nested) return nested;
        }
        return null;
    }
    private void LogScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!logAutoScrolling && logScroller != null && logScroller.ScrollableHeight - logScroller.VerticalOffset > 24)
            FollowLogsToggle.IsOn = false;
    }
    private void LogPointerPressed(object sender, PointerRoutedEventArgs e) => logPointerDown = true;
    private void LogPointerReleased(object sender, PointerRoutedEventArgs e) => logPointerDown = false;
    private void LogBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!replacingLogText && LogBox.SelectionLength > 0) FollowLogsToggle.IsOn = false;
    }
    private void FollowLogs_Toggled(object sender, RoutedEventArgs e)
    {
        if (LogBox != null && FollowLogsToggle.IsOn) ResumeLogFollow();
    }
    private void JumpLatest_Click(object sender, RoutedEventArgs e)
    {
        if (!FollowLogsToggle.IsOn) FollowLogsToggle.IsOn = true;
        else ResumeLogFollow();
    }
    private void ResumeLogFollow()
    {
        logPointerDown = false;
        LogBox.Select(LogBox.Text.Length, 0);
        System.Threading.Interlocked.Exchange(ref logDirty, 1);
        RefreshLogs();
    }
    private void RefreshLogs()
    {
        if (LogsPage.Visibility != Visibility.Visible || !FollowLogsToggle.IsOn || logPointerDown || LogBox.SelectionLength > 0 || logAutoScrolling) return;
        if (System.Threading.Interlocked.Exchange(ref logDirty, 0) == 0) return;
        logAutoScrolling = true;
        replacingLogText = true;
        try
        {
            string text = service.LogText;
            if (LogBox.Text != text) LogBox.Text = text;
            LogBox.Select(LogBox.Text.Length, 0);
        }
        finally { replacingLogText = false; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (FollowLogsToggle.IsOn && !logPointerDown && LogBox.SelectionLength == 0)
                {
                    LogBox.UpdateLayout();
                    logScroller?.ChangeView(null, logScroller.ScrollableHeight, null, true);
                }
            }
            finally { logAutoScrolling = false; }
        });
    }
    private void FillSettings()
    {
        foreach (var number in new[] { PortBox, ContextBox, PredictBox }) number.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsGrouped = false };
        ServerPathBox.Text = settings.ServerPath; ModelPathBox.Text = settings.ModelPath; MmprojPathBox.Text = settings.MmprojPath;
        HostBox.Text = settings.Host; PortBox.Value = settings.Port; ContextBox.Value = settings.ContextSize; PredictBox.Value = settings.PredictTokens;
        ArgumentsBox.Text = settings.ExtraArguments; FreeComfyToggle.IsOn = settings.FreeComfyMemory;
        ModelText.Text = System.IO.Path.GetFileNameWithoutExtension(settings.ModelPath);
        SettingsPathText.Text = "配置文件：" + AppSettings.FilePath;
    }
    private void Render(Telemetry sample)
    {
        telemetry = sample;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool newSample = monitorDisplay.Update(sample, now);
        StateText.Text = monitorDisplay.StateText;
        DetailText.Text = monitorDisplay.SummaryText;
        ToolTipService.SetToolTip(DetailText, monitorDisplay.SummaryText);
        GenerationText.Text = monitorDisplay.GenerationText;
        UpdatedText.Text = monitorDisplay.UpdatedText;
        PromptStageText.Text = monitorDisplay.PromptStageText;
        PromptStageBar.Visibility = monitorDisplay.ShowPromptStage ? Visibility.Visible : Visibility.Collapsed;
        SlotsText.Text = monitorDisplay.ActiveText;
        ConnectionText.Text = monitorDisplay.ConnectionText;
        TasksStatusText.Text = monitorDisplay.TasksText;
        MetricsText.Text = monitorDisplay.MetricsText;
        EndpointsText.Text = $"/health {sample.Health}\n/slots {sample.Slots}\n/metrics {sample.Metrics}";
        string? progressText = sample.Progress is { } progress ? MonitorDisplayState.FormatPromptProgress(progress, now) : null;
        DiagnosticText.Text = sample.Detail;
        TaskText.Text = progressText == null ? sample.Detail : progressText + "\n" + sample.Detail;
        LoadingRing.IsActive = sample.Owned && sample.Busy;
        if (newSample) MainChart.Add(monitorDisplay.GenerationSampleValue, monitorDisplay.GenerationSampleAt);
        floating?.Update(monitorDisplay, newSample);
        UpdateActions();
    }
    private void Render(GpuSnapshot sample)
    {
        monitorDisplay.Update(sample);
        GpuSummaryText.Text = monitorDisplay.GpuSummaryText;
        GpuDetailText.Text = monitorDisplay.GpuDetailText;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MainChart.AddGpu(sample.Utilization, now);
        floating?.UpdateGpu(monitorDisplay, sample.Utilization, now);
    }
    private void UpdateActions()
    {
        StartButton.IsEnabled = !actionPending && !service.HasOwnedProcess && !service.IsStarting && telemetry?.External != true;
        StopButton.IsEnabled = !actionPending && service.HasOwnedProcess;
        bool settingsEnabled = !actionPending && !service.HasOwnedProcess && !service.IsStarting;
        foreach (var control in new Control[] { ServerPathBox, ModelPathBox, MmprojPathBox, HostBox, PortBox, ContextBox, PredictBox, ArgumentsBox, FreeComfyToggle }) control.IsEnabled = settingsEnabled;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (actionPending || dialogOpen) return;
        actionPending = true; UpdateActions();
        try { await service.StartAsync(); }
        catch (Exception ex) { ShowNotice("启动失败", ex.Message, InfoBarSeverity.Error); }
        finally { actionPending = false; UpdateActions(); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (actionPending || dialogOpen || !service.HasOwnedProcess) return;
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "停止自有模型？", Content = "仅停止由 ModelDesk 启动的 llama-server 进程树。进行中的推理请求会中断；外部服务不会被操作。", PrimaryButtonText = "停止模型", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception ex) { ShowNotice("对话框不可用", ex.Message, InfoBarSeverity.Error); return; }
        finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary) return;
        actionPending = true; UpdateActions();
        try { await service.StopAsync(); }
        catch (Exception ex) { ShowNotice("停止失败", ex.Message, InfoBarSeverity.Error); }
        finally { actionPending = false; UpdateActions(); }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (service.HasOwnedProcess || service.IsStarting) throw new InvalidOperationException("运行期间不能修改设置。");
            int Integer(double value, string label)
            {
                if (!double.IsFinite(value) || value != Math.Truncate(value) || value > int.MaxValue) throw new ArgumentException(label + "必须为整数。");
                return checked((int)value);
            }
            var next = new AppSettings { ServerPath = ServerPathBox.Text.Trim(), ModelPath = ModelPathBox.Text.Trim(), MmprojPath = MmprojPathBox.Text.Trim(), Host = HostBox.Text.Trim(), Port = Integer(PortBox.Value, "端口"), ContextSize = Integer(ContextBox.Value, "上下文"), PredictTokens = Integer(PredictBox.Value, "最大生成 token"), ExtraArguments = ArgumentsBox.Text, FreeComfyMemory = FreeComfyToggle.IsOn };
            next.Save(); settings = next; service.UpdateSettings(next); FillSettings();
            ShowNotice("设置已保存", "下次手动启动模型时生效。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("无法保存", ex.Message, InfoBarSeverity.Error); }
    }
    private void Float_Click(object sender, RoutedEventArgs e) => ShowFloating();
    private async void Exit_Click(object sender, RoutedEventArgs e) => await RequestExit();
    private void ShowFloating()
    {
        if (floating != null && floating.Visible)
        {
            floating.AppWindow.Hide(); return;
        }
        if (floating == null)
        {
            floating = new FloatingWindow(Restore);
            floating.Closed += (_, _) => floating = null;
            floating.Update(monitorDisplay, false);
            floating.UpdateGpu(monitorDisplay, null, null);
            floating.SyncChartFrom(MainChart, monitorDisplay.GenerationSampleAt);
        }
        floating.AppWindow.Show();
        floating.Activate();
    }
    private void Restore()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
    }
    private async Task RequestExit()
    {
        Restore();
        if (dialogOpen) return;
        if (service.HasOwnedProcess || service.IsStarting || actionPending)
        {
            ShowNotice("当前不能退出", "请先确认停止自有模型，再从托盘退出；直接退出可能中断模型日志管道。外部服务不受此限制。", InfoBarSeverity.Warning);
            return;
        }
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "退出 ModelDesk？", Content = "退出后停止监控；不会停止任何外部模型。", PrimaryButtonText = "退出", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception ex) { ShowNotice("对话框不可用", ex.Message, InfoBarSeverity.Error); return; }
        finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary) return;
        exiting = true; Close();
    }
    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage(); package.SetText(service.LogText); Clipboard.SetContent(package);
            ShowNotice("日志已复制", "剪贴板包含当前内存日志。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("复制失败", ex.Message, InfoBarSeverity.Error); }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(service.LogDirectory); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { service.LogDirectory }, UseShellExecute = true }); }
        catch (Exception ex) { ShowNotice("无法打开日志目录", ex.Message, InfoBarSeverity.Error); }
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity) { Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
}
