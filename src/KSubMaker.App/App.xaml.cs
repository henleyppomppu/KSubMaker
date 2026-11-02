using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.App.ViewModels;
using KSubMaker.App.Views;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Infrastructure;
using KSubMaker.Infrastructure.Logging;
using KSubMaker.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace KSubMaker.App;

/// <summary>
/// Composition root and process lifetime.
///
/// Startup order is deliberate and each step depends on the previous one: single-instance guard →
/// host (so <see cref="IAppPaths"/> exists) → Serilog (needs the log directory) → database migration
/// → settings (they may relocate the cache/model/log directories) → queue restore → window.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// Global namespace so a second instance is refused across terminal-server sessions too, which is
    /// what actually matters here: two processes would fight over the same SQLite file and the same
    /// Python worker port.
    /// </summary>
    private const string SingleInstanceMutexName = @"Global\KSubMaker";

    private readonly LoggingLevelSwitch _levelSwitch = new();

    private IHost? _host;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private ILogger<App>? _logger;
    private bool _servicesDisposed;
    private bool _errorDialogVisible;

    /// <summary>
    /// Menus and drop-downs open to the right of what was clicked, as on an ordinary desktop.
    ///
    /// Windows' pen/touch handedness setting (태블릿 PC 설정 → 오른손잡이) flips every WPF menu to
    /// open leftward — on the user's PC the 도구 menu appeared to the left of its own header. WPF
    /// reads the setting once into a private static field with no public setter; clearing that field
    /// is the standard workaround. Best effort: if a future WPF renames it, menus stay as Windows says.
    /// </summary>
    private static void OpenMenusRightward()
    {
        if (!SystemParameters.MenuDropAlignment)
        {
            return;
        }

        var field = typeof(SystemParameters).GetField(
            "_menuDropAlignment",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field?.SetValue(null, false);
    }

    /// <summary>
    /// <see cref="async void"/> is unavoidable for a framework override; every path is wrapped so an
    /// exception here can never reach the CLR's unhandled-exception handler.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        OpenMenusRightward();

        if (!TryAcquireSingleInstance())
        {
            MessageBox.Show(
                Strings.SingleInstanceMessage,
                Strings.SingleInstanceTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        try
        {
            _host = BuildHost();
            await _host.StartAsync().ConfigureAwait(true);

            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("KSubMaker를 시작합니다.");

            await InitializeServicesAsync().ConfigureAwait(true);

            var window = _host.Services.GetRequiredService<Views.MainWindow>();
            MainWindow = window;
            window.Show();

            // After Show(): hardware detection shells out to nvidia-smi and would otherwise delay the
            // first paint by several seconds on a cold start.
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "시작 중 오류가 발생했습니다.");

            MessageBox.Show(
                $"{Strings.StartupFailedMessage}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                Strings.StartupFailedTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            await ShutdownServicesAsync().ConfigureAwait(true);
            Shutdown(2);
        }
    }

    /// <summary>
    /// Last-resort teardown. The normal path runs from <see cref="MainWindow"/>'s closing handler and
    /// has already finished by the time this is reached; this only covers the abnormal exits (startup
    /// failure, <c>Shutdown()</c> called directly).
    ///
    /// The dispatcher loop is over at this point, so there is no UI thread left to keep responsive —
    /// and the Python worker's process tree must be gone before this method returns.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (!_servicesDisposed)
            {
                Task.Run(ShutdownServicesAsync).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "종료 처리 중 오류가 발생했습니다.");
        }
        finally
        {
            ReleaseSingleInstance();
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Stops the host and disposes it asynchronously.
    ///
    /// <c>IAsyncDisposable</c> is not optional here: <c>JobQueueService</c> and the worker client only
    /// implement the async form, and a synchronous <c>Dispose()</c> on the container would throw
    /// rather than shutting the Python process down.
    /// </summary>
    internal async Task ShutdownServicesAsync()
    {
        if (_servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;

        var host = Interlocked.Exchange(ref _host, null);
        if (host is null)
        {
            return;
        }

        try
        {
            host.Services.GetRequiredService<SettingsService>().SettingsChanged -= OnSettingsChanged;
        }
        catch (Exception)
        {
            // The container may already be past the point where it can resolve; nothing to detach.
        }

        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "호스트를 정지하는 중 오류가 발생했습니다.");
        }

        try
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
        catch (TimeoutException)
        {
            // The Job Object attached to the worker kills the process tree when this process dies, so
            // a stuck disposal cannot leave a stray Python behind.
            _logger?.LogWarning("서비스 정리가 시간 내에 끝나지 않아 강제로 종료합니다.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "서비스를 정리하는 중 오류가 발생했습니다.");
        }
    }

    // -----------------------------------------------------------------------
    // Host
    // -----------------------------------------------------------------------

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // Registered before the infrastructure module so its TryAddSingleton is a no-op: the Serilog
        // sink is built against this exact instance, and a second IAppPaths would let a later
        // ApplyOverrides move the log directory out from under the open file handle.
        var paths = new Infrastructure.Paths.AppPaths();
        paths.EnsureCreated();
        builder.Services.AddSingleton<IAppPaths>(paths);

        builder.Services.AddKSubMakerInfrastructure();
        builder.Services.AddKSubMakerWorker();

        ConfigureLogging(builder, paths);

        // ---- shell services -------------------------------------------------
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IShellService, ShellService>();
        builder.Services.AddSingleton<IFileActionService, FileActionService>();
        builder.Services.AddSingleton<IWindowService, WindowService>();
        builder.Services.AddSingleton<ISystemPowerService, SystemPowerService>();

        // ---- view models ----------------------------------------------------
        // MainViewModel is a singleton because it owns the live projection of the queue; the dialog
        // view models are transient so a reopened window always starts from persisted state.
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<ModelsViewModel>();
        builder.Services.AddTransient<LogViewModel>();

        // ---- windows --------------------------------------------------------
        builder.Services.AddSingleton<Views.MainWindow>();
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddTransient<ModelsWindow>();
        builder.Services.AddTransient<LogWindow>();

        return builder.Build();
    }

    /// <summary>
    /// Replaces the default console providers with the Serilog file sink, driven by a level switch so
    /// the persisted 로그 수준 can be applied later without recreating the logger.
    /// </summary>
    private void ConfigureLogging(HostApplicationBuilder builder, IAppPaths paths)
    {
        _levelSwitch.MinimumLevel = SerilogSetup.ParseLevel("Information");

        builder.Logging.ClearProviders();
        builder.Logging.AddKSubMakerFileLogging(paths, _levelSwitch, maskPaths: false);
    }

    private async Task InitializeServicesAsync()
    {
        if (_host is null)
        {
            return;
        }

        var services = _host.Services;

        await services.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync()
            .ConfigureAwait(true);

        var settingsService = services.GetRequiredService<SettingsService>();
        var settings = await settingsService.LoadAsync().ConfigureAwait(true);

        // Applying the persisted level here (rather than rebuilding the logger) keeps the open file
        // handle and everything buffered in it. The same switch is nudged on every later save, so
        // changing 로그 수준 in the settings screen takes effect without a restart.
        _levelSwitch.MinimumLevel = SerilogSetup.ParseLevel(settings.LogLevel);
        settingsService.SettingsChanged += OnSettingsChanged;

        var queue = services.GetRequiredService<JobQueueService>();
        await queue.LoadAsync().ConfigureAwait(true);

        // After the queue: a playlist's entries refer to jobs.
        await services.GetRequiredService<PlaylistService>().LoadAsync().ConfigureAwait(true);

        // After LoadAsync so the "known job" set is complete; a sweep against an empty queue would
        // delete the checkpoints of every job waiting to be resumed.
        StartOrphanedCacheCleanup(queue);
    }

    /// <summary>
    /// Sweeps the cache folders and <c>*.tmp</c> files a hard kill left behind
    /// ("처리 중 앱 강제 종료 후 임시 파일 복구").
    ///
    /// Detached and off the UI thread on purpose: it walks the whole cache tree, which can take
    /// seconds on a slow or networked drive, and nothing about it is a precondition for using the
    /// application. <see cref="JobQueueService.CleanupOrphanedCacheAsync"/> already swallows its own
    /// failures; the catch here is the belt-and-braces guard that keeps a background exception off
    /// the unobserved-task path during start-up.
    /// </summary>
    private void StartOrphanedCacheCleanup(JobQueueService queue)
    {
        var logger = _logger;

        _ = Task.Run(async () =>
        {
            try
            {
                await queue.CleanupOrphanedCacheAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "시작 시 캐시 정리에 실패했습니다.");
            }
        });
    }

    private void OnSettingsChanged(object? sender, Domain.Settings.AppSettings settings) =>
        _levelSwitch.MinimumLevel = SerilogSetup.ParseLevel(settings.LogLevel);

    // -----------------------------------------------------------------------
    // Single instance
    // -----------------------------------------------------------------------

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            _ownsMutex = createdNew;
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // A locked-down session can deny access to the Global namespace. Refusing to start would
            // be worse than allowing a second instance, so the guard fails open.
            _singleInstanceMutex = null;
            _ownsMutex = false;
            return true;
        }
        catch (Exception)
        {
            _singleInstanceMutex = null;
            _ownsMutex = false;
            return true;
        }
    }

    private void ReleaseSingleInstance()
    {
        var mutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            if (_ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // Not the owning thread (only possible after an abnormal teardown); disposal still frees it.
        }
        finally
        {
            mutex.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Global exception handling
    // -----------------------------------------------------------------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "UI 스레드에서 처리되지 않은 예외가 발생했습니다.");

        // Handled = true keeps the window alive. The state may be imperfect, but losing a queue of
        // half-processed files to a crash is strictly worse.
        e.Handled = true;
        ShowFriendlyError(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "관찰되지 않은 백그라운드 작업 예외가 발생했습니다.");

        // Observing it stops the finalizer thread from tearing the process down.
        e.SetObserved();

        var dispatcher = Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var exception = e.Exception;
        _ = dispatcher.InvokeAsync(() => ShowFriendlyError(exception));
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger?.LogCritical(exception, "치명적인 예외로 프로그램이 종료됩니다.");
        }
        else
        {
            _logger?.LogCritical("치명적인 예외로 프로그램이 종료됩니다.");
        }
    }

    /// <summary>
    /// Shows one dialog at a time. A failing render pass can raise the same exception on every frame,
    /// and a modal box per frame would make the application impossible to close.
    /// </summary>
    private void ShowFriendlyError(Exception? exception)
    {
        if (_errorDialogVisible)
        {
            return;
        }

        _errorDialogVisible = true;

        try
        {
            var detail = exception?.Message ?? string.Empty;
            var body = string.IsNullOrWhiteSpace(detail)
                ? Strings.UnexpectedErrorMessage
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{Strings.UnexpectedErrorMessage}{Environment.NewLine}{Environment.NewLine}{detail}");

            MessageBox.Show(body, Strings.UnexpectedErrorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "오류 대화 상자를 표시하지 못했습니다.");
        }
        finally
        {
            _errorDialogVisible = false;
        }
    }
}
