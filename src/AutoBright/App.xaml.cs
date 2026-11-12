namespace AutoBright;

public partial class App : Application
{
    // Single-instance enforcement happens earlier, in the WinUI App constructor
    // (Platforms/Windows/App.xaml.cs), before the MAUI host starts. See SingleInstance.
    public App()
    {
        InitializeComponent();
        SystemAccent.Initialize(this);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        CommandLineOptions options = CommandLineOptions.Parse(Environment.GetCommandLineArgs());
        return new Window(new MainPage(options)) { Title = "AutoBright" };
    }
}
