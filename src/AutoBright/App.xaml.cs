namespace AutoBright;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        CommandLineOptions options = CommandLineOptions.Parse(Environment.GetCommandLineArgs());
        return new Window(new MainPage(options)) { Title = "AutoBright" };
    }
}
