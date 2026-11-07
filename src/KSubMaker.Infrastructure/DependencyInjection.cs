using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Processing;
using KSubMaker.Application.Services;
using KSubMaker.Application.Testing;
using KSubMaker.Domain.Models;
using KSubMaker.Infrastructure.Checkpoints;
using KSubMaker.Infrastructure.Hardware;
using KSubMaker.Infrastructure.IO;
using KSubMaker.Infrastructure.Media;
using KSubMaker.Infrastructure.Models;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.Infrastructure.Persistence;
using KSubMaker.Infrastructure.Persistence.Repositories;
using KSubMaker.Infrastructure.Subtitles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure;

/// <summary>
/// Wires the infrastructure implementations into the host container.
///
/// Deliberately absent: <c>IToolLocator</c>, <c>IWorkerClient</c> and <c>IJobProcessorSelector</c>.
/// Those belong to the worker layer, which knows where the bundled Python runtime lives and how to
/// choose between the worker-backed and in-process pipelines. Resolving
/// <see cref="JobQueueService"/> therefore requires the worker layer to have registered its selector
/// as well — by design, so that neither half silently substitutes a stub for the other.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddKSubMakerInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ---- paths and file system -----------------------------------------
        // Singleton because ApplyOverrides mutates it from the settings screen and every consumer
        // must observe the new cache/model/log locations immediately.
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.TryAddSingleton<IFileSystem, PhysicalFileSystem>();

        // ---- persistence -----------------------------------------------------
        AddPersistence(services);

        // ---- platform services -----------------------------------------------
        services.TryAddSingleton<IHardwareDetector, WindowsHardwareDetector>();
        services.TryAddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.TryAddSingleton<IAudioExtractor, FfmpegAudioExtractor>();
        services.TryAddSingleton<ICheckpointStore, FileCheckpointStore>();
        services.TryAddSingleton<ISubtitleWriter, AtomicSubtitleWriter>();

        // ---- models ----------------------------------------------------------
        // The catalog is immutable data; one instance for the whole process.
        services.TryAddSingleton(new ModelCatalog());

        services.AddHttpClient(HttpModelManager.HttpClientName, client =>
        {
            // Hugging Face rejects requests without a User-Agent from some edge nodes.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KSubMaker/0.1 (+https://github.com/ksubmaker)");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            // Multi-gigabyte transfers cannot live under a wall-clock timeout; HttpModelManager runs
            // its own per-read stall watchdog instead.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Singleton: it tracks in-flight downloads so the models screen can show live progress.
        services.TryAddSingleton<IModelManager, HttpModelManager>();

        // ---- application services --------------------------------------------
        services.TryAddSingleton<HardwareService>();
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<VideoScanService>();
        services.TryAddSingleton<JobQueueService>();
        services.TryAddSingleton<PlaylistService>();

        AddInProcessPipeline(services);

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        // A factory rather than a scoped DbContext: the queue pump, the UI and the downloader all
        // hit the repositories concurrently, and one shared DbContext would be a data race.
        services.AddDbContextFactory<KSubMakerDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<IAppPaths>();
            Directory.CreateDirectory(paths.DatabaseDirectory);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabaseFile,
                // Microsoft.Data.Sqlite turns this into SQLite's busy_timeout, which is what makes a
                // concurrent writer wait instead of failing with SQLITE_BUSY.
                DefaultTimeout = 30,
                Pooling = true
            }.ToString();

            options.UseSqlite(connectionString);
        });

        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<ISettingsRepository, SettingsRepository>();
        services.TryAddSingleton<IModelRepository, ModelRepository>();
        services.TryAddSingleton<IPlaylistRepository, PlaylistRepository>();
        services.TryAddSingleton<IDatabaseInitializer, DatabaseInitializer>();
    }

    /// <summary>
    /// The in-process pipeline behind "Fake AI 모드" and the integration tests.
    ///
    /// The two AI stages are constructed explicitly rather than resolved from the container. If they
    /// were resolved, a worker layer that registered real <c>ITranscriber</c> /
    /// <c>ITranslationEngine</c> implementations would silently turn the fake mode into a real run —
    /// and the user would get unmarked output from a mode whose entire purpose is to be obviously
    /// fake. Everything else in this processor (extraction, checkpointing, validation, SRT writing)
    /// is the real code path.
    /// </summary>
    private static void AddInProcessPipeline(IServiceCollection services)
    {
        services.TryAddSingleton(provider => new InProcessJobProcessor(
            provider.GetRequiredService<IAudioExtractor>(),
            new FakeTranscriber(provider.GetRequiredService<IFileSystem>()),
            new FakeTranslationEngine(),
            provider.GetRequiredService<ISubtitleWriter>(),
            provider.GetRequiredService<ICheckpointStore>(),
            provider.GetRequiredService<IAppPaths>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<ILogger<InProcessJobProcessor>>()));
    }
}
