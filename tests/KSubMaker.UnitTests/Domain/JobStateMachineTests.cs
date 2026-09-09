using FluentAssertions;
using KSubMaker.Domain.Jobs;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The complete legal/illegal transition matrix.
///
/// The expected table is written out here independently of the production dictionary on purpose: a
/// test that re-derived it from <c>JobStateMachine</c> would pass no matter what the production code
/// said.
/// </summary>
public sealed class JobStateMachineTests
{
    private static readonly JobStatus[] AllStatuses = Enum.GetValues<JobStatus>();

    /// <summary>
    /// from → the set of *other* statuses that may legally follow.
    ///
    /// Three rules produce this table, and the theories below pin every one of the 100 cells:
    /// <list type="number">
    /// <item>Among the active stages movement is <b>forward only</b>. A job resuming from a
    /// checkpoint goes straight to the stage the checkpoint recorded, so Probing → Translating is
    /// ordinary; Translating → Probing is still a bug and stays rejected.</item>
    /// <item><b>Completed is reachable from WritingSubtitle alone.</b> A job is finished when its
    /// subtitle has been written and at no other moment.</item>
    /// <item><b>Pending is reachable from every non-terminal status</b>, because requeueing is always
    /// a legal request — the automatic single retry does exactly that from whatever stage failed.</item>
    /// </list>
    /// </summary>
    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> Expected =
        new Dictionary<JobStatus, JobStatus[]>
        {
            [JobStatus.Pending] =
            [
                JobStatus.Probing, JobStatus.ExtractingAudio, JobStatus.Transcribing, JobStatus.Translating,
                JobStatus.WritingSubtitle, JobStatus.Skipped, JobStatus.Failed, JobStatus.Paused
            ],
            [JobStatus.Probing] =
            [
                JobStatus.ExtractingAudio, JobStatus.Transcribing, JobStatus.Translating,
                JobStatus.WritingSubtitle, JobStatus.Pending, JobStatus.Failed, JobStatus.Cancelled,
                JobStatus.Paused
            ],
            [JobStatus.ExtractingAudio] =
            [
                JobStatus.Transcribing, JobStatus.Translating, JobStatus.WritingSubtitle,
                JobStatus.Pending, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Paused
            ],
            [JobStatus.Transcribing] =
            [
                JobStatus.Translating, JobStatus.WritingSubtitle, JobStatus.Pending, JobStatus.Failed,
                JobStatus.Cancelled, JobStatus.Paused
            ],
            [JobStatus.Translating] =
            [
                JobStatus.WritingSubtitle, JobStatus.Pending, JobStatus.Failed, JobStatus.Cancelled,
                JobStatus.Paused
            ],
            [JobStatus.WritingSubtitle] =
            [
                JobStatus.Completed, JobStatus.Pending, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Paused
            ],
            [JobStatus.Completed] = [JobStatus.Pending],
            [JobStatus.Failed] = [JobStatus.Pending, JobStatus.Cancelled],
            [JobStatus.Cancelled] = [JobStatus.Pending],
            [JobStatus.Skipped] = [JobStatus.Pending],
            [JobStatus.Paused] =
            [
                JobStatus.Pending, JobStatus.Probing, JobStatus.ExtractingAudio, JobStatus.Transcribing,
                JobStatus.Translating, JobStatus.WritingSubtitle, JobStatus.Cancelled
            ]
        };

    /// <summary>The active stages in pipeline order; index is what "forward" means.</summary>
    private static readonly JobStatus[] ActiveOrder =
    [
        JobStatus.Probing, JobStatus.ExtractingAudio, JobStatus.Transcribing, JobStatus.Translating,
        JobStatus.WritingSubtitle
    ];

    public static TheoryData<JobStatus, JobStatus, bool> FullMatrix()
    {
        var data = new TheoryData<JobStatus, JobStatus, bool>();

        foreach (var from in AllStatuses)
        {
            foreach (var to in AllStatuses)
            {
                var legal = from == to || Expected[from].Contains(to);
                data.Add(from, to, legal);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FullMatrix))]
    public void CanTransition_matches_the_specified_matrix(JobStatus from, JobStatus to, bool legal)
    {
        JobStateMachine.CanTransition(from, to).Should().Be(legal);
    }

    [Theory]
    [MemberData(nameof(FullMatrix))]
    public void TryTransition_agrees_with_CanTransition_and_only_explains_failures(
        JobStatus from,
        JobStatus to,
        bool legal)
    {
        var ok = JobStateMachine.TryTransition(from, to, out var error);

        ok.Should().Be(legal);

        if (legal)
        {
            error.Should().BeNull();
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();
            error.Should().Contain(from.ToString()).And.Contain(to.ToString());
            error.Should().Contain("전환할 수 없습니다");
        }
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.ExtractingAudio)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.Translating)]
    [InlineData(JobStatus.WritingSubtitle)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Skipped)]
    [InlineData(JobStatus.Paused)]
    public void A_transition_to_the_same_status_is_always_a_no_op(JobStatus status)
    {
        JobStateMachine.CanTransition(status, status).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Cancelled, true)]
    [InlineData(JobStatus.Skipped, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Paused, false)]
    [InlineData(JobStatus.Probing, false)]
    [InlineData(JobStatus.ExtractingAudio, false)]
    [InlineData(JobStatus.Transcribing, false)]
    [InlineData(JobStatus.Translating, false)]
    [InlineData(JobStatus.WritingSubtitle, false)]
    public void IsTerminal_marks_only_completed_failed_cancelled_and_skipped(JobStatus status, bool expected)
    {
        JobStateMachine.IsTerminal(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(JobStatus.Probing, true)]
    [InlineData(JobStatus.ExtractingAudio, true)]
    [InlineData(JobStatus.Transcribing, true)]
    [InlineData(JobStatus.Translating, true)]
    [InlineData(JobStatus.WritingSubtitle, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Paused, false)]
    [InlineData(JobStatus.Completed, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Cancelled, false)]
    [InlineData(JobStatus.Skipped, false)]
    public void IsActive_marks_only_the_in_flight_statuses(JobStatus status, bool expected)
    {
        JobStateMachine.IsActive(status).Should().Be(expected);
    }

    [Fact]
    public void No_status_is_both_terminal_and_active()
    {
        AllStatuses
            .Where(s => JobStateMachine.IsTerminal(s) && JobStateMachine.IsActive(s))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobStage.None, JobStatus.Pending)]
    [InlineData(JobStage.Probing, JobStatus.Probing)]
    [InlineData(JobStage.ExtractingAudio, JobStatus.ExtractingAudio)]
    [InlineData(JobStage.Transcribing, JobStatus.Transcribing)]
    [InlineData(JobStage.Translating, JobStatus.Translating)]
    [InlineData(JobStage.WritingSubtitle, JobStatus.WritingSubtitle)]
    [InlineData(JobStage.Done, JobStatus.Completed)]
    public void StatusForStage_maps_every_stage(JobStage stage, JobStatus expected)
    {
        JobStateMachine.StatusForStage(stage).Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // The three rules behind the matrix, asserted directly rather than only cell by cell.
    // -----------------------------------------------------------------------

    public static TheoryData<JobStatus, JobStatus> ForwardActivePairs()
    {
        var data = new TheoryData<JobStatus, JobStatus>();

        for (var from = 0; from < ActiveOrder.Length; from++)
        {
            for (var to = from + 1; to < ActiveOrder.Length; to++)
            {
                data.Add(ActiveOrder[from], ActiveOrder[to]);
            }
        }

        return data;
    }

    /// <summary>
    /// A job resuming from a checkpoint jumps straight to the stage the checkpoint recorded — the
    /// worker logs "체크포인트에서 이어서 진행합니다: translating" and then reports translating
    /// progress without ever having extracted audio. A strictly linear table rejected that.
    /// </summary>
    [Theory]
    [MemberData(nameof(ForwardActivePairs))]
    public void Forward_movement_between_active_stages_is_legal(JobStatus from, JobStatus to)
    {
        JobStateMachine.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(ForwardActivePairs))]
    public void The_same_pair_backwards_is_rejected(JobStatus earlier, JobStatus later)
    {
        JobStateMachine.CanTransition(later, earlier).Should().BeFalse(
            "a job never un-runs a stage; a backward report is a bug, not a resume");
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Paused)]
    public void A_queued_or_paused_job_may_enter_any_active_stage(JobStatus from)
    {
        foreach (var stage in ActiveOrder)
        {
            JobStateMachine.CanTransition(from, stage).Should().BeTrue(
                $"{from} → {stage} is how a checkpoint resume re-enters the pipeline");
        }
    }

    /// <summary>
    /// Requeueing is always a legal request. This is the edge whose absence broke every automatic
    /// retry: the queue put a failed job back on Pending from whatever stage it died in, the
    /// transition threw, and the generic handler relabelled the job UNKNOWN.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.ExtractingAudio)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.Translating)]
    [InlineData(JobStatus.WritingSubtitle)]
    [InlineData(JobStatus.Paused)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Skipped)]
    public void Every_status_can_be_put_back_in_the_queue(JobStatus from)
    {
        JobStateMachine.CanTransition(from, JobStatus.Pending).Should().BeTrue();
    }

    /// <summary>
    /// Completion has exactly one door. Allowing 검사 중 → 완료 is what let the success path skip
    /// 자막 저장 중 without anyone noticing.
    /// </summary>
    [Theory]
    [InlineData(JobStatus.WritingSubtitle, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Probing, false)]
    [InlineData(JobStatus.ExtractingAudio, false)]
    [InlineData(JobStatus.Transcribing, false)]
    [InlineData(JobStatus.Translating, false)]
    [InlineData(JobStatus.Paused, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Cancelled, false)]
    [InlineData(JobStatus.Skipped, false)]
    public void Completed_is_only_reachable_from_writing_the_subtitle(JobStatus from, bool expected)
    {
        JobStateMachine.CanTransition(from, JobStatus.Completed).Should().Be(expected);
    }

    /// <summary>Terminal states stay sealed: nothing but an explicit requeue (or retry) gets out.</summary>
    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Skipped)]
    public void A_terminal_status_never_falls_back_into_an_active_stage(JobStatus terminal)
    {
        foreach (var stage in ActiveOrder)
        {
            JobStateMachine.CanTransition(terminal, stage).Should().BeFalse(
                $"{terminal} → {stage} would restart work the user or the pipeline has already ended");
        }
    }

    [Fact]
    public void Every_status_is_reachable_from_pending_by_some_legal_path()
    {
        var reachable = new HashSet<JobStatus> { JobStatus.Pending };
        var frontier = new Queue<JobStatus>([JobStatus.Pending]);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();

            foreach (var next in AllStatuses.Where(s => s != current && JobStateMachine.CanTransition(current, s)))
            {
                if (reachable.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        reachable.Should().BeEquivalentTo(AllStatuses);
    }

    /// <summary>
    /// 건너뜀 means "never ran" — claiming that for a job that was active, or paused with progress
    /// already made, would misrepresent work that was actually abandoned as work that was never
    /// attempted. Only a still-<see cref="JobStatus.Pending"/> job may become Skipped.
    /// </summary>
    [Fact]
    public void Only_a_pending_job_can_become_skipped()
    {
        JobStateMachine.CanTransition(JobStatus.Pending, JobStatus.Skipped).Should().BeTrue();

        foreach (var status in AllStatuses.Where(s => s is not (JobStatus.Pending or JobStatus.Skipped)))
        {
            JobStateMachine.CanTransition(status, JobStatus.Skipped).Should().BeFalse(
                $"{status} → Skipped would claim work in progress was never started");
        }
    }
}
