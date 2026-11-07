using FluentAssertions;
using KSubMaker.Domain.Jobs;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers "진행률 계산": monotonicity, clamping and the ETA extrapolation guard.</summary>
public sealed class ProgressCalculatorTests
{
    private static readonly JobStage[] PipelineOrder =
    [
        JobStage.Probing,
        JobStage.ExtractingAudio,
        JobStage.Transcribing,
        JobStage.Translating,
        JobStage.WritingSubtitle
    ];

    [Fact]
    public void Weights_sum_to_one()
    {
        ProgressCalculator.Weights.Values.Sum().Should().BeApproximately(1.0d, 1e-9);
    }

    [Fact]
    public void Weights_cover_every_working_stage_and_nothing_else()
    {
        ProgressCalculator.Weights.Keys.Should().BeEquivalentTo(PipelineOrder);
    }

    [Fact]
    public void Weights_are_all_strictly_positive()
    {
        ProgressCalculator.Weights.Values.Should().OnlyContain(w => w > 0d);
    }

    [Fact]
    public void Done_is_always_one_hundred()
    {
        ProgressCalculator.Overall(JobStage.Done, 0d).Should().Be(100d);
        ProgressCalculator.Overall(JobStage.Done, 100d).Should().Be(100d);
        ProgressCalculator.Overall(JobStage.Done, -999d).Should().Be(100d);
    }

    [Fact]
    public void None_is_always_zero()
    {
        ProgressCalculator.Overall(JobStage.None, 0d).Should().Be(0d);
        ProgressCalculator.Overall(JobStage.None, 100d).Should().Be(0d);
        ProgressCalculator.Overall(JobStage.None, 999d).Should().Be(0d);
    }

    [Fact]
    public void The_first_stage_starts_at_zero_and_the_last_stage_ends_at_one_hundred()
    {
        ProgressCalculator.Overall(JobStage.Probing, 0d).Should().Be(0d);
        ProgressCalculator.Overall(JobStage.WritingSubtitle, 100d).Should().Be(100d);
    }

    [Fact]
    public void Overall_is_monotonically_non_decreasing_across_the_whole_pipeline()
    {
        var previous = -1d;

        foreach (var stage in PipelineOrder)
        {
            for (var percent = 0; percent <= 100; percent++)
            {
                var value = ProgressCalculator.Overall(stage, percent);

                value.Should().BeGreaterThanOrEqualTo(previous,
                    $"progress must never go backwards ({stage} at {percent}%)");

                previous = value;
            }
        }

        previous.Should().Be(100d);
    }

    [Fact]
    public void Each_stage_starts_exactly_where_the_previous_one_finished()
    {
        for (var i = 1; i < PipelineOrder.Length; i++)
        {
            var endOfPrevious = ProgressCalculator.Overall(PipelineOrder[i - 1], 100d);
            var startOfCurrent = ProgressCalculator.Overall(PipelineOrder[i], 0d);

            startOfCurrent.Should().BeApproximately(endOfPrevious, 1e-9);
        }
    }

    [Theory]
    [InlineData(JobStage.Probing)]
    [InlineData(JobStage.ExtractingAudio)]
    [InlineData(JobStage.Transcribing)]
    [InlineData(JobStage.Translating)]
    [InlineData(JobStage.WritingSubtitle)]
    public void Stage_progress_below_zero_clamps_to_the_stage_start(JobStage stage)
    {
        ProgressCalculator.Overall(stage, -1_000d).Should().Be(ProgressCalculator.Overall(stage, 0d));
    }

    [Theory]
    [InlineData(JobStage.Probing)]
    [InlineData(JobStage.ExtractingAudio)]
    [InlineData(JobStage.Transcribing)]
    [InlineData(JobStage.Translating)]
    [InlineData(JobStage.WritingSubtitle)]
    public void Stage_progress_above_one_hundred_clamps_to_the_stage_end(JobStage stage)
    {
        ProgressCalculator.Overall(stage, 100_000d).Should().Be(ProgressCalculator.Overall(stage, 100d));
    }

    [Fact]
    public void Overall_never_leaves_the_zero_to_one_hundred_range()
    {
        var stages = Enum.GetValues<JobStage>();
        double[] percentages = [-1e9, -1d, 0d, 0.5d, 33.3d, 99.999d, 100d, 1e9, double.MaxValue];

        foreach (var stage in stages)
        {
            foreach (var percent in percentages)
            {
                ProgressCalculator.Overall(stage, percent).Should().BeInRange(0d, 100d);
            }
        }
    }

    [Fact]
    public void Transcribing_carries_the_largest_share_of_the_work()
    {
        var transcribing = ProgressCalculator.Weights[JobStage.Transcribing];

        ProgressCalculator.Weights
            .Where(w => w.Key != JobStage.Transcribing)
            .Should().OnlyContain(w => w.Value < transcribing);
    }

    // -----------------------------------------------------------------------
    // EstimateRemaining
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0d)]
    [InlineData(0.1d)]
    [InlineData(0.5d)]
    [InlineData(-5d)]
    public void EstimateRemaining_is_null_while_progress_is_too_small_to_extrapolate(double progress)
    {
        ProgressCalculator.EstimateRemaining(progress, TimeSpan.FromMinutes(1)).Should().BeNull();
    }

    [Fact]
    public void EstimateRemaining_is_null_once_the_job_is_finished()
    {
        ProgressCalculator.EstimateRemaining(100d, TimeSpan.FromMinutes(1)).Should().BeNull();
        ProgressCalculator.EstimateRemaining(150d, TimeSpan.FromMinutes(1)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void EstimateRemaining_is_null_without_a_positive_elapsed_time(int elapsedSeconds)
    {
        ProgressCalculator.EstimateRemaining(50d, TimeSpan.FromSeconds(elapsedSeconds)).Should().BeNull();
    }

    [Fact]
    public void EstimateRemaining_extrapolates_linearly_above_the_threshold()
    {
        // Half done after 60s => roughly another 60s to go.
        var remaining = ProgressCalculator.EstimateRemaining(50d, TimeSpan.FromSeconds(60));

        remaining.Should().NotBeNull();
        remaining!.Value.TotalSeconds.Should().BeApproximately(60d, 0.001d);
    }

    [Fact]
    public void EstimateRemaining_at_ten_percent_projects_nine_times_the_elapsed_time()
    {
        var remaining = ProgressCalculator.EstimateRemaining(10d, TimeSpan.FromSeconds(10));

        remaining.Should().NotBeNull();
        remaining!.Value.TotalSeconds.Should().BeApproximately(90d, 0.001d);
    }

    [Fact]
    public void EstimateRemaining_shrinks_as_progress_grows_for_a_fixed_pace()
    {
        // A constant 1 %/second pace: the estimate must fall as the job advances.
        TimeSpan? previous = null;

        for (var percent = 1; percent < 100; percent++)
        {
            var estimate = ProgressCalculator.EstimateRemaining(percent, TimeSpan.FromSeconds(percent));

            estimate.Should().NotBeNull();

            if (previous is not null)
            {
                estimate!.Value.Should().BeLessThan(previous.Value);
            }

            previous = estimate;
        }
    }

    [Fact]
    public void EstimateRemaining_never_returns_a_negative_span()
    {
        for (var percent = 0.6d; percent < 99d; percent += 0.7d)
        {
            var estimate = ProgressCalculator.EstimateRemaining(percent, TimeSpan.FromSeconds(3));
            estimate.Should().NotBeNull();
            estimate!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        }
    }

    // ---- stages a job does not run ------------------------------------------------------------

    /// <summary>Korean audio: no 번역, so the end of 음성 인식 is most of the way, not 65%.</summary>
    [Fact]
    public void Without_translation_the_other_stages_fill_the_whole_bar()
    {
        var skipped = new HashSet<JobStage> { JobStage.Translating };

        ProgressCalculator.Overall(JobStage.WritingSubtitle, 100d, skipped).Should().Be(100d);
        ProgressCalculator.Overall(JobStage.Transcribing, 100d, skipped)
            .Should().BeApproximately(65d / 68d * 100d, 0.01d);
    }

    /// <summary>From an embedded subtitle: no 음성 인식, so 번역 starts at 10 of 45, not at 65%.</summary>
    [Fact]
    public void Without_transcription_translation_starts_where_extraction_ended()
    {
        var skipped = new HashSet<JobStage> { JobStage.Transcribing };

        ProgressCalculator.Overall(JobStage.Translating, 0d, skipped)
            .Should().BeApproximately(10d / 45d * 100d, 0.01d);
    }

    /// <summary>Taking a stage the job has not reached out of the plan never moves the bar back.</summary>
    [Theory]
    [InlineData(JobStage.Probing, JobStage.Transcribing)]
    [InlineData(JobStage.Transcribing, JobStage.Translating)]
    [InlineData(JobStage.ExtractingAudio, JobStage.Translating)]
    public void Skipping_a_later_stage_only_moves_the_bar_forward(JobStage current, JobStage dropped)
    {
        for (var p = 0d; p <= 100d; p += 10d)
        {
            var before = ProgressCalculator.Overall(current, p, new HashSet<JobStage>());
            var after = ProgressCalculator.Overall(current, p, new HashSet<JobStage> { dropped });
            after.Should().BeGreaterThanOrEqualTo(before);
        }
    }

    [Fact]
    public void The_current_stage_always_counts_even_if_listed_as_skipped()
    {
        var skipped = new HashSet<JobStage> { JobStage.Transcribing };

        ProgressCalculator.Overall(JobStage.Transcribing, 50d, skipped)
            .Should().Be(ProgressCalculator.Overall(JobStage.Transcribing, 50d));
    }

    /// <summary>
    /// A job that resumed at 60% has done nothing yet this run. Measured from zero it looked nearly
    /// finished; measured from where it started, the estimate follows this run's actual pace.
    /// </summary>
    [Fact]
    public void Remaining_time_is_measured_from_where_this_run_started()
    {
        // 60% → 70% took 100 s, so the remaining 30% takes 300 s.
        ProgressCalculator.EstimateRemaining(70d, TimeSpan.FromSeconds(100), startProgress: 60d)
            .Should().Be(TimeSpan.FromSeconds(300));

        ProgressCalculator.EstimateRemaining(60.2d, TimeSpan.FromSeconds(5), startProgress: 60d)
            .Should().BeNull("too little progress this run to extrapolate from");
    }
}
