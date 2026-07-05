using System.Threading;

namespace AutoBright;

public partial class App : Application
{
    // A second instance would spin up its own worker thread driving the same
    // monitors via DDC/CI and saving the same settings.json, so only one may
    // run at a time. The mutex is held for the process lifetime; never
    // disposed, so it's released automatically when the process exits.
    private static readonly Mutex SingleInstanceMutex;
    private static readonly bool IsFirstInstance;

    static App()
    {
        SingleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\AutoBright_SingleInstance", out bool createdNew);
        IsFirstInstance = createdNew;
    }

    public App()
    {
        if (!IsFirstInstance)
        {
            Environment.Exit(0);
        }

        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        CommandLineOptions options = CommandLineOptions.Parse(Environment.GetCommandLineArgs());
        return new Window(new MainPage(options)) { Title = "AutoBright" };
    }
}
