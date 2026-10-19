using Microsoft.UI.Xaml;
namespace ModelDesk;
public partial class App : Application
{
    private Window? window;
    private readonly bool validateUi;
    public App() : this(false) { }
    public App(bool validationOnly)
    {
        validateUi = validationOnly;
        UnhandledException += (_, e) =>
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Models.AppSettings.DataDirectory, "startup-error.txt"), DateTimeOffset.Now + "\n" + e.Exception + "\n"); } catch { }
        };
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (validateUi)
        {
            try
            {
                window = new MainWindow(true);
                var floating = new FloatingWindow(() => { });
                ValidateLayout(window, 1200, 900);
                ValidateLayout(floating, 420, 480);
                floating.Close();
                await Task.Delay(250);
                ValidateLayout(window, 1000, 760);
                var secondFloating = new FloatingWindow(() => { });
                ValidateLayout(secondFloating, 440, 560);
                secondFloating.Close();
                await Task.Delay(250);
                SelfTest.Save(new { success = true, mode = "validate-ui", mainWindow = window.GetType().FullName, floatingWindow = floating.GetType().FullName, layoutMeasured = true, floatingCloseCycles = 2, dispatchAfterClose = true, guiShown = false, modelStarted = false, pollingStarted = false, limitation = "Offline layout and close lifecycle tested; live caption click alignment and backdrop appearance require visual review." });
            }
            catch (Exception ex) { Environment.ExitCode = 1; SelfTest.Save(new { success = false, mode = "validate-ui", error = ex.ToString(), guiShown = false, modelStarted = false }); }
            Exit();
            return;
        }
        window = new MainWindow();
        window.Activate();
    }
    private static void ValidateLayout(Window target, double width, double height)
    {
        if (target.Content is not FrameworkElement root) throw new InvalidOperationException("Window content is missing.");
        root.Measure(new Windows.Foundation.Size(width, height));
        root.Arrange(new Windows.Foundation.Rect(0, 0, width, height));
        root.UpdateLayout();
    }
}
