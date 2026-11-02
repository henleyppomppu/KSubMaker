namespace KSubMaker.Domain.Playlists;

/// <summary>
/// A named bucket of jobs the user put together by hand — "2021년 강의", "시즌 4 재처리" — drawn from
/// any number of folders. It holds references only: deleting a playlist, or taking a job out of one,
/// never touches the job, its cache or its subtitle.
/// </summary>
public sealed class Playlist
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>One job's membership of one playlist.</summary>
public sealed class PlaylistEntry
{
    public required string PlaylistId { get; init; }

    public required string JobId { get; init; }

    public DateTime AddedAtUtc { get; init; }
}
