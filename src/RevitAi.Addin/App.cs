using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitAi.Addin.Context;
using RevitAi.Addin.Dispatch;
using RevitAi.Addin.Infrastructure;
using RevitAi.Addin.Tools;
using RevitAi.Addin.UI;
using RevitAi.Core.Ai;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Tools;

namespace RevitAi.Addin;

public sealed class App : IExternalApplication
{
    public static readonly DockablePaneId PaneId = new(new Guid("5FC579AF-E1F6-41C1-9669-6782ECE9B5FE"));

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitAi");

    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAi", "logs");

    // One HttpClient for the add-in's lifetime; the timeout is set from settings in OnStartup.
    private static readonly HttpClient Http = new();

    private FileLog? _log;
    private AssistantViewModel? _viewModel;

    public Result OnStartup(UIControlledApplication application)
    {
        _log = new FileLog(LogDir);
        try
        {
            string settingsPath = Path.Combine(AppDataDir, "settings.json");
            AddinSettings settings = AddinSettings.Load(settingsPath, out string? problem);
            if (problem is not null)
            {
                _log.Warning(problem);
            }

            // Must be created here, inside Revit's API context (ADR-023).
            var dispatcher = new RevitDispatcher(TimeSpan.FromSeconds(settings.DispatcherTimeoutSeconds), _log);
            Http.Timeout = TimeSpan.FromSeconds(settings.AiRequestTimeoutSeconds);
            var keyStore = new ApiKeyStore(Path.Combine(AppDataDir, "openai.key"));
            var ai = new OpenAiClient(Http, settings.OpenAiModel, keyStore.TryLoad);
            var orchestrator = new Orchestrator(ai, CreateToolRegistry(dispatcher), _log, settings.MaxAiSteps);
            _viewModel = new AssistantViewModel(dispatcher, orchestrator, keyStore, settings, settingsPath, _log);

            // Dockable panes can only be registered during startup.
            application.RegisterDockablePane(PaneId, "Revit AI", new AssistantPaneProvider(new AssistantPane(_viewModel)));

            CreateRibbon(application);

            // Revit raises these events inside its API context, so reading the model directly is allowed here.
            application.ViewActivated += (_, e) => _viewModel.UpdateContext(ContextReader.Read(e.Document));
            application.SelectionChanged += OnSelectionChanged;
            // If another document is still open, its ViewActivated event follows and restores the context.
            application.ControlledApplication.DocumentClosed += (_, _) => _viewModel.UpdateContext(ModelContext.NoDocument);

            _log.Info($"Revit AI started in Revit {application.ControlledApplication.VersionNumber} (model {settings.OpenAiModel}).");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            _log.Error("Revit AI failed to start.", ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.SelectionChanged -= OnSelectionChanged;
        _log?.Info("Revit AI shut down.");
        return Result.Succeeded;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel?.UpdateContext(ContextReader.Read(e.GetDocument()));
    }

    private static ToolRegistry CreateToolRegistry(RevitDispatcher dispatcher)
    {
        var registry = new ToolRegistry();
        registry.Register(new GetProjectInfoTool(dispatcher));
        registry.Register(new GetActiveViewTool(dispatcher));
        registry.Register(new GetActiveLevelTool(dispatcher));
        registry.Register(new GetSelectedElementsTool(dispatcher));
        registry.Register(new GetElementTool(dispatcher));
        registry.Register(new FindElementsTool(dispatcher));
        registry.Register(new GetElementParametersTool(dispatcher));
        return registry;
    }

    private static void CreateRibbon(UIControlledApplication application)
    {
        RibbonPanel panel = application.CreateRibbonPanel("Revit AI");
        var button = new PushButtonData(
            name: "RevitAi.ToggleAssistant",
            text: "Assistant",
            assemblyName: typeof(App).Assembly.Location,
            className: typeof(ToggleAssistantCommand).FullName)
        {
            ToolTip = "Show or hide the Revit AI assistant panel.",
            AvailabilityClassName = typeof(ToggleAssistantCommand).FullName,
            Image = LoadIcon("assistant-16.png"),
            LargeImage = LoadIcon("assistant-32.png"),
        };
        panel.AddItem(button);
    }

    private static ImageSource LoadIcon(string fileName)
    {
        using Stream stream = typeof(App).Assembly.GetManifestResourceStream($"RevitAi.Addin.Resources.{fileName}")
            ?? throw new InvalidOperationException($"Missing embedded icon {fileName}.");
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
