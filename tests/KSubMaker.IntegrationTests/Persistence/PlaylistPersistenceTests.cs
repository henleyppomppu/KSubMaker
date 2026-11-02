using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.Infrastructure.Persistence;
using KSubMaker.Infrastructure.Persistence.Repositories;
using KSubMaker.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.IntegrationTests.Persistence;

/// <summary>
/// 재생목록 on a real SQLite file: they survive a restart, and membership never outlives the job or
/// the playlist — including when the queue deletes jobs with ExecuteDelete, which skips EF's
/// change tracker and leaves the cascade to the database.
/// </summary>
public sealed class PlaylistPersistenceTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new("ksubmaker-playlists");

    private SqliteFileContextFactory _factory = null!;
    private JobRepository _jobs = null!;
    private PlaylistRepository _playlists = null!;

    public async Task InitializeAsync()
    {
        var paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        paths.EnsureCreated();

        _factory = new SqliteFileContextFactory(paths.DatabaseFile);
        await new DatabaseInitializer(_factory, paths, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        _jobs = new JobRepository(_factory, NullLogger<JobRepository>.Instance);
        _playlists = new PlaylistRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _workspace.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Job> AddJobAsync(string path)
    {
        var job = new Job { VideoPath = path, FileName = Path.GetFileName(path), Status = JobStatus.Pending };
        await _jobs.AddAsync(job);
        return job;
    }

    [Fact]
    public async Task A_playlist_and_its_jobs_come_back_after_a_restart()
    {
        var a = await AddJobAsync("/lectures/2021/a.mkv");
        var b = await AddJobAsync("/lectures/2022/b.mkv");

        var service = new PlaylistService(_playlists);
        await service.LoadAsync();
        var playlist = await service.CreateAsync("  재처리  ");
        await service.AddAsync(playlist.Id, [a.Id, b.Id, a.Id]);
        await service.RenameAsync(playlist.Id, "시즌 4 재처리");

        var reloaded = new PlaylistService(_playlists);
        await reloaded.LoadAsync();

        reloaded.Playlists.Should().ContainSingle().Which.Name.Should().Be("시즌 4 재처리");
        reloaded.JobIdsOf(playlist.Id).Should().BeEquivalentTo([a.Id, b.Id]);
    }

    [Fact]
    public async Task Removing_a_job_from_the_queue_takes_it_out_of_every_playlist()
    {
        var a = await AddJobAsync("/lectures/2021/a.mkv");
        var b = await AddJobAsync("/lectures/2021/b.mkv");

        var service = new PlaylistService(_playlists);
        await service.LoadAsync();
        var first = await service.CreateAsync("첫째");
        var second = await service.CreateAsync("둘째");
        await service.AddAsync(first.Id, [a.Id, b.Id]);
        await service.AddAsync(second.Id, [a.Id]);

        await _jobs.RemoveRangeAsync([a.Id]);

        var reloaded = new PlaylistService(_playlists);
        await reloaded.LoadAsync();
        reloaded.JobIdsOf(first.Id).Should().Equal(b.Id);
        reloaded.JobIdsOf(second.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_playlist_leaves_its_jobs_in_the_queue()
    {
        var a = await AddJobAsync("/lectures/2021/a.mkv");

        var service = new PlaylistService(_playlists);
        await service.LoadAsync();
        var playlist = await service.CreateAsync("지울 것");
        await service.AddAsync(playlist.Id, [a.Id]);

        await service.DeleteAsync(playlist.Id);

        (await _playlists.GetEntriesAsync()).Should().BeEmpty();
        (await _jobs.GetAllAsync()).Should().ContainSingle().Which.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task Taking_a_job_out_of_a_playlist_leaves_the_job_in_the_queue()
    {
        var a = await AddJobAsync("/lectures/2021/a.mkv");

        var service = new PlaylistService(_playlists);
        await service.LoadAsync();
        var playlist = await service.CreateAsync("버킷");
        await service.AddAsync(playlist.Id, [a.Id]);

        (await service.RemoveAsync(playlist.Id, [a.Id, "unknown"])).Should().Be(1);

        service.Count(playlist.Id).Should().Be(0);
        (await _jobs.GetAllAsync()).Should().ContainSingle();
    }
}
