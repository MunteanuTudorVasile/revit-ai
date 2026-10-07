using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitAi.Addin.Context;
using RevitAi.Addin.Dispatch;
using RevitAi.Addin.Infrastructure;
using RevitAi.Addin.Planning;
using RevitAi.Addin.Tools;
using RevitAi.Addin.UI;
using RevitAi.Core.Ai;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Localization;
using RevitAi.Core.Planning;
using RevitAi.Core.Standards;
using RevitAi.Core.Tools;

namespace RevitAi.Addin;

public sealed class App : IExternalApplication
{
    public static readonly DockablePaneId PaneId = new(new Guid("5FC579AF-E1F6-41C1-9669-6782ECE9B5FE"));

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitAi");

    private static readonly string LocalDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAi");

    private static readonly string LogDir = Path.Combine(LocalDataDir, "logs");

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
            string standardsPath = Path.Combine(AppDataDir, "standards.json");
            ProjectStandards.Load(standardsPath, out string? standardsProblem); // creates the template on first start
            if (standardsProblem is not null)
            {
                _log.Warning(standardsProblem);
            }

            var text = new TextSource(new UiText(settings.Language));
            ToolRegistry registry = CreateToolRegistry(dispatcher, standardsPath, text);
            var orchestrator = new Orchestrator(ai, registry, _log, settings.MaxAiSteps);
            var history = new ActionHistory(Path.Combine(LocalDataDir, "history.jsonl"), _log);
            _viewModel = new AssistantViewModel(
                dispatcher, orchestrator, new PlanExecutor(registry, text), history, keyStore, text, settings, settingsPath, _log);

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

    private static ToolRegistry CreateToolRegistry(RevitDispatcher dispatcher, string standardsPath, TextSource text)
    {
        var registry = new ToolRegistry();
        registry.Register(new GetProjectInfoTool(dispatcher));
        registry.Register(new GetActiveViewTool(dispatcher));
        registry.Register(new GetActiveLevelTool(dispatcher));
        registry.Register(new GetSelectedElementsTool(dispatcher));
        registry.Register(new GetElementTool(dispatcher));
        registry.Register(new FindElementsTool(dispatcher));
        registry.Register(new GetElementParametersTool(dispatcher));
        registry.Register(new FindFamilyTypesTool(dispatcher, standardsPath));
        registry.Register(new GetProjectStandardTypesTool(dispatcher, standardsPath));
        registry.Register(new FindNearbyElementsTool(dispatcher));
        registry.Register(new GetElementRoomTool(dispatcher));
        registry.Register(new GetRoomBoundaryTool(dispatcher));
        registry.Register(new FindViewsTool(dispatcher));

        registry.Register(new CreateWallTool(dispatcher, text));
        registry.Register(new ModifyWallTool(dispatcher, text));
        registry.Register(new CreateRoomTool(dispatcher, text));
        registry.Register(new CreateDoorTool(dispatcher, text));
        registry.Register(new CreateWindowTool(dispatcher, text));
        registry.Register(new CreateFloorTool(dispatcher, text));

        registry.Register(new CreateViewTool(dispatcher, text));
        registry.Register(new CreateSheetTool(dispatcher, text));
        registry.Register(new CreateScheduleTool(dispatcher, text));
        registry.Register(new TagElementsTool(dispatcher, text));
        registry.Register(new CreateTextTool(dispatcher, text));
        registry.Register(new DimensionWallTool(dispatcher, text));
        registry.Register(new DimensionRoomTool(dispatcher, text));
        registry.Register(new CreateSectionTool(dispatcher, text));
        registry.Register(new CreateElevationsTool(dispatcher, text));
        registry.Register(new Create3DViewTool(dispatcher, text));
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
