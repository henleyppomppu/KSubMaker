using System.Globalization;
using FluentAssertions;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers "GPU 사양별 권장 설정" across the whole VRAM ladder plus both no-GPU paths.</summary>
public sealed class HardwareRecommendationPolicyTests
{
    private static readonly ModelCatalog Catalog = new();

    private static HardwareProfile Gpu(double vramGb, bool cudaAvailable = true, int cores = 16, double ramGb = 32d) => new()
    {
        Gpus =
        [
            new GpuInfo
            {
                Name = $"NVIDIA Test {vramGb:0.#}GB",
                Index = 0,
                TotalVramBytes = (long)(vramGb * 1024 * 1024 * 1024),
                FreeVramBytes = (long)(vramGb * 1024 * 1024 * 1024)
            }
        ],
        CudaAvailable = cudaAvailable,
        CudaVersion = cudaAvailable ? "12.4" : null,
        CpuName = "Test CPU",
        LogicalCoreCount = cores,
        TotalRamBytes = (long)(ramGb * 1024 * 1024 * 1024)
    };

    private static bool ContainsHangul(string value) =>
        value.Any(c => c is >= '가' and <= '힣');

    // -----------------------------------------------------------------------
    // the VRAM ladder
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(2d, ModelIds.WhisperSmall, ComputeType.Int8, 1)]
    [InlineData(4d, ModelIds.WhisperSmall, ComputeType.Int8Float16, 3)]
    [InlineData(6d, ModelIds.WhisperMedium, ComputeType.Int8Float16, 5)]
    [InlineData(8d, ModelIds.WhisperLargeV3Turbo, ComputeType.Int8Float16, 5)]
    [InlineData(12d, ModelIds.WhisperLargeV3, ComputeType.Float16, 5)]
    [InlineData(16d, ModelIds.WhisperLargeV3, ComputeType.Float16, 5)]
    [InlineData(24d, ModelIds.WhisperLargeV3, ComputeType.Float16, 5)]
    public void The_whisper_tier_compute_type_and_beam_size_follow_the_vram_ladder(
        double vramGb,
        string expectedModel,
        ComputeType expectedCompute,
        int expectedBeam)
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog);

        recommendation.WhisperModelId.Should().Be(expectedModel);
        recommendation.ComputeType.Should().Be(expectedCompute);
        recommendation.BeamSize.Should().Be(expectedBeam);
        recommendation.UseGpu.Should().BeTrue();
    }

    [Theory]
    [InlineData(2d, ModelIds.TranslationNllb600M)]
    [InlineData(4d, ModelIds.TranslationNllb600M)]
    [InlineData(6d, ModelIds.TranslationNllb600M)]
    [InlineData(8d, ModelIds.TranslationNllb600M)]
    [InlineData(12d, ModelIds.TranslationNllb13B)]
    [InlineData(16d, ModelIds.TranslationNllb13B)]
    [InlineData(24d, ModelIds.TranslationNllb13B)]
    public void The_translation_model_upgrades_at_twelve_gigabytes(double vramGb, string expected)
    {
        HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog)
            .TranslationModelId.Should().Be(expected);
    }

    [Theory]
    [InlineData(2d, ModelIds.LlmGemma3_4B)]
    [InlineData(4d, ModelIds.LlmGemma3_4B)]
    [InlineData(6d, ModelIds.LlmGemma3_4B)]
    [InlineData(8d, ModelIds.LlmGemma3_4B)]
    [InlineData(12d, ModelIds.LlmGemma3_4B)]
    [InlineData(16d, ModelIds.LlmGemma3_12B)]
    [InlineData(24d, ModelIds.LlmGemma3_12B)]
    public void The_llm_upgrades_at_sixteen_gigabytes(double vramGb, string expected)
    {
        // 12B needs the card to itself next to whisper-large-v3, so it waits for 16GB. Below that
        // the 4B is the recommendation — dropping the whole run to 방식 B for an opt-in translation
        // engine is the wrong trade.
        HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog)
            .LlmModelId.Should().Be(expected);
    }

    [Theory]
    [InlineData(2d)]
    [InlineData(4d)]
    [InlineData(6d)]
    [InlineData(8d)]
    [InlineData(12d)]
    [InlineData(16d)]
    [InlineData(24d)]
    public void The_japanese_only_model_is_never_recommended(double vramGb)
    {
        // The recommendation runs before anything has been transcribed, so it cannot know the
        // source language. Steering someone there automatically would hand them a model that is
        // worse on every language but one. It stays an explicit choice.
        HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog)
            .WhisperModelId.Should().NotBe(ModelIds.WhisperKotobaV2);
    }

    [Fact]
    public void The_japanese_only_model_is_not_the_cpu_fallback_either()
    {
        var profile = new HardwareProfile
        {
            Gpus = [],
            CudaAvailable = false,
            LogicalCoreCount = 8,
            TotalRamBytes = 8L * 1024 * 1024 * 1024
        };

        HardwareRecommendationPolicy.Recommend(profile, Catalog)
            .WhisperModelId.Should().NotBe(ModelIds.WhisperKotobaV2);
    }

    [Theory]
    [InlineData(2d)]
    [InlineData(12d)]
    [InlineData(24d)]
    public void Qwen_is_never_recommended_for_the_llm_engine(double vramGb)
    {
        // Measured, not preference: Qwen2.5 7B answered 41% of a Japanese file in Chinese
        // (측정 표본 B, 113 of 273 output lines Han-only) and left 15% untranslated. The entries stay
        // installable — pulling a downloaded model out from under someone is worse — but nothing
        // steers a new user at them.
        var llm = HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog).LlmModelId;

        llm.Should().NotBe(ModelIds.LlmQwen3B);
        llm.Should().NotBe(ModelIds.LlmQwen7B);
    }

    [Theory]
    [InlineData(2d, false, ProcessingStrategy.TranscribeAllThenTranslate)]  // B: models cannot co-reside
    [InlineData(4d, true, ProcessingStrategy.SequentialPerFile)]            // A: they fit together
    [InlineData(6d, true, ProcessingStrategy.SequentialPerFile)]
    [InlineData(8d, true, ProcessingStrategy.SequentialPerFile)]
    [InlineData(12d, true, ProcessingStrategy.SequentialPerFile)]
    [InlineData(16d, true, ProcessingStrategy.PipelinedParallel)]           // C: only on a roomy card
    [InlineData(24d, true, ProcessingStrategy.PipelinedParallel)]
    public void The_strategy_follows_whether_both_models_fit_in_vram(
        double vramGb,
        bool expectedCoResidency,
        ProcessingStrategy expectedStrategy)
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog);

        recommendation.CanCoResideModels.Should().Be(expectedCoResidency);
        recommendation.Strategy.Should().Be(expectedStrategy);
    }

    [Theory]
    [InlineData(2d)]
    [InlineData(4d)]
    [InlineData(6d)]
    [InlineData(8d)]
    [InlineData(12d)]
    public void The_pipelined_strategy_is_reserved_for_sixteen_gigabytes_and_up(double vramGb)
    {
        HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog)
            .Strategy.Should().NotBe(ProcessingStrategy.PipelinedParallel);
    }

    [Theory]
    [InlineData(2d)]
    [InlineData(4d)]
    [InlineData(6d)]
    [InlineData(8d)]
    [InlineData(12d)]
    [InlineData(16d)]
    [InlineData(24d)]
    public void The_recommended_models_actually_fit_the_card(double vramGb)
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog);

        var whisperVram = Catalog.EstimatedVramGb(recommendation.WhisperModelId, recommendation.ComputeType);

        whisperVram.Should().BeLessThan(vramGb,
            "an over-recommended model that dies with CUDA OOM is worse than a smaller one that finishes");
    }

    [Theory]
    [InlineData(2d)]
    [InlineData(4d)]
    [InlineData(6d)]
    [InlineData(8d)]
    [InlineData(12d)]
    [InlineData(16d)]
    [InlineData(24d)]
    public void Every_recommendation_carries_a_non_empty_korean_rationale(double vramGb)
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog);

        recommendation.Rationale.Should().NotBeNullOrWhiteSpace();
        ContainsHangul(recommendation.Rationale).Should().BeTrue();
        recommendation.Rationale.Should().Contain(recommendation.WhisperModelId);
        recommendation.Rationale.Should().Contain(recommendation.TranslationModelId);
    }

    [Theory]
    [InlineData(2d, "방식 B")]
    [InlineData(8d, "방식 A")]
    public void The_rationale_names_the_chosen_processing_mode(double vramGb, string expectedFragment)
    {
        HardwareRecommendationPolicy.Recommend(Gpu(vramGb), Catalog)
            .Rationale.Should().Contain(expectedFragment);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(16, 8)]
    [InlineData(64, 8)]
    public void MaxParallelCpuTasks_is_half_the_cores_capped_at_eight_on_a_gpu_machine(int cores, int expected)
    {
        HardwareRecommendationPolicy.Recommend(Gpu(12d, cores: cores), Catalog)
            .MaxParallelCpuTasks.Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // no usable GPU
    // -----------------------------------------------------------------------

    [Fact]
    public void With_no_gpu_at_all_the_cpu_fallback_is_used()
    {
        var profile = new HardwareProfile
        {
            Gpus = [],
            CudaAvailable = false,
            LogicalCoreCount = 8,
            TotalRamBytes = 8L * 1024 * 1024 * 1024
        };

        var recommendation = HardwareRecommendationPolicy.Recommend(profile, Catalog);

        recommendation.UseGpu.Should().BeFalse();
        recommendation.WhisperModelId.Should().Be(ModelIds.WhisperSmall);
        recommendation.ComputeType.Should().Be(ComputeType.Int8);
        recommendation.BeamSize.Should().Be(1);
        recommendation.CanCoResideModels.Should().BeFalse();
        recommendation.Strategy.Should().Be(ProcessingStrategy.TranscribeAllThenTranslate);
        recommendation.TranslationModelId.Should().Be(ModelIds.TranslationNllb600M);
        recommendation.LlmModelId.Should().Be(ModelIds.LlmGemma3_4B);
        recommendation.Rationale.Should().Contain("NVIDIA GPU가 감지되지 않았습니다.");
        ContainsHangul(recommendation.Rationale).Should().BeTrue();
    }

    [Fact]
    public void A_gpu_without_a_usable_cuda_runtime_also_falls_back_to_the_cpu()
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(Gpu(24d, cudaAvailable: false), Catalog);

        recommendation.UseGpu.Should().BeFalse();
        recommendation.ComputeType.Should().Be(ComputeType.Int8);
        recommendation.Strategy.Should().Be(ProcessingStrategy.TranscribeAllThenTranslate);
        recommendation.Rationale.Should().Contain("NVIDIA GPU는 감지되었으나 CUDA를 사용할 수 없습니다.");
    }

    /// <summary>
    /// The reported machine: a 12 GB RTX 3080 Ti whose driver is perfectly healthy but which has no
    /// cuBLAS 12. Before the fix the profile said <c>CudaAvailable = true</c> and this method
    /// recommended whisper-large-v3 on float16 — a configuration that could never load.
    /// </summary>
    [Fact]
    public void A_gpu_whose_support_libraries_are_missing_falls_back_and_says_why()
    {
        var profile = Gpu(12d, cudaAvailable: false) with
        {
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = false,
            MissingCudaLibraries = ["cublas64_12.dll"]
        };

        var recommendation = HardwareRecommendationPolicy.Recommend(profile, Catalog);

        recommendation.UseGpu.Should().BeFalse();
        recommendation.WhisperModelId.Should().Be(ModelIds.WhisperMedium, "32GB of RAM, CPU tier");
        recommendation.ComputeType.Should().Be(ComputeType.Int8);

        recommendation.Rationale.Should().Contain("cublas64_12.dll");
        recommendation.Rationale.Should().Contain("build-worker.ps1");
        recommendation.Rationale.Should().NotContain(
            "NVIDIA GPU는 감지되었으나 CUDA를 사용할 수 없습니다.",
            "that sentence sends the user to the driver page, and the driver is the one part that works");
        ContainsHangul(recommendation.Rationale).Should().BeTrue();
    }

    [Fact]
    public void A_missing_library_rationale_still_reads_when_no_file_name_was_reported()
    {
        var profile = Gpu(12d, cudaAvailable: false) with
        {
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = false,
            MissingCudaLibraries = []
        };

        var rationale = HardwareRecommendationPolicy.Recommend(profile, Catalog).Rationale;

        rationale.Should().Contain("CUDA 지원 라이브러리").And.NotContain("()");
    }

    [Theory]
    [InlineData(8d, ModelIds.WhisperSmall)]
    [InlineData(16d, ModelIds.WhisperMedium)]
    [InlineData(64d, ModelIds.WhisperMedium)]
    public void The_cpu_fallback_picks_its_model_from_system_ram(double ramGb, string expected)
    {
        var profile = new HardwareProfile
        {
            Gpus = [],
            CudaAvailable = false,
            LogicalCoreCount = 8,
            TotalRamBytes = (long)(ramGb * 1024 * 1024 * 1024)
        };

        HardwareRecommendationPolicy.Recommend(profile, Catalog).WhisperModelId.Should().Be(expected);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(8, 4)]
    [InlineData(32, 4)]
    public void The_cpu_fallback_caps_parallel_tasks_at_four(int cores, int expected)
    {
        var profile = new HardwareProfile
        {
            Gpus = [],
            CudaAvailable = false,
            LogicalCoreCount = cores,
            TotalRamBytes = 16L * 1024 * 1024 * 1024
        };

        HardwareRecommendationPolicy.Recommend(profile, Catalog).MaxParallelCpuTasks.Should().Be(expected);
    }

    [Fact]
    public void Recommend_rejects_null_arguments()
    {
        var nullProfile = () => HardwareRecommendationPolicy.Recommend(null!, Catalog);
        var nullCatalog = () => HardwareRecommendationPolicy.Recommend(HardwareProfile.Unknown, null!);

        nullProfile.Should().Throw<ArgumentNullException>();
        nullCatalog.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_unknown_profile_is_treated_as_a_cpu_only_machine()
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(HardwareProfile.Unknown, Catalog);

        recommendation.UseGpu.Should().BeFalse();
        recommendation.Rationale.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_primary_gpu_is_the_one_with_the_most_vram()
    {
        var profile = new HardwareProfile
        {
            Gpus =
            [
                new GpuInfo { Name = "small", Index = 0, TotalVramBytes = 4L * 1024 * 1024 * 1024 },
                new GpuInfo { Name = "big", Index = 1, TotalVramBytes = 24L * 1024 * 1024 * 1024 }
            ],
            CudaAvailable = true,
            LogicalCoreCount = 16,
            TotalRamBytes = 32L * 1024 * 1024 * 1024
        };

        var recommendation = HardwareRecommendationPolicy.Recommend(profile, Catalog);

        recommendation.WhisperModelId.Should().Be(ModelIds.WhisperLargeV3);
        recommendation.Rationale.Should().Contain("big");
    }

    // -----------------------------------------------------------------------
    // OOM ladders
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ComputeType.Float32, ComputeType.Float16)]
    [InlineData(ComputeType.BFloat16, ComputeType.Float16)]
    [InlineData(ComputeType.Float16, ComputeType.Int8Float16)]
    [InlineData(ComputeType.Int8Float16, ComputeType.Int8)]
    public void Downgrade_moves_one_step_down_the_precision_ladder(ComputeType from, ComputeType to)
    {
        HardwareRecommendationPolicy.Downgrade(from).Should().Be(to);
    }

    [Fact]
    public void Downgrade_returns_null_at_the_cheapest_setting()
    {
        HardwareRecommendationPolicy.Downgrade(ComputeType.Int8).Should().BeNull();
    }

    [Theory]
    [InlineData(ComputeType.Float32)]
    [InlineData(ComputeType.Float16)]
    [InlineData(ComputeType.BFloat16)]
    [InlineData(ComputeType.Int8Float16)]
    [InlineData(ComputeType.Int8)]
    public void The_downgrade_ladder_always_terminates(ComputeType start)
    {
        var seen = new List<ComputeType> { start };
        ComputeType? current = start;

        for (var step = 0; step < 20 && current is not null; step++)
        {
            current = HardwareRecommendationPolicy.Downgrade(current.Value);

            if (current is not null)
            {
                seen.Should().NotContain(current.Value, "the ladder must never loop");
                seen.Add(current.Value);
            }
        }

        current.Should().BeNull();
        seen[^1].Should().Be(ComputeType.Int8);
    }

    [Theory]
    [InlineData(ModelIds.WhisperLargeV3, ModelIds.WhisperLargeV3Turbo)]
    [InlineData(ModelIds.WhisperLargeV3Turbo, ModelIds.WhisperMedium)]
    [InlineData(ModelIds.WhisperMedium, ModelIds.WhisperSmall)]
    [InlineData(ModelIds.WhisperSmall, ModelIds.WhisperBase)]
    public void DowngradeWhisper_moves_one_step_down_the_size_ladder(string from, string to)
    {
        HardwareRecommendationPolicy.DowngradeWhisper(from).Should().Be(to);
    }

    [Theory]
    [InlineData(ModelIds.WhisperBase)]
    [InlineData("some-unknown-model")]
    public void DowngradeWhisper_returns_null_when_there_is_nowhere_left_to_go(string modelId)
    {
        HardwareRecommendationPolicy.DowngradeWhisper(modelId).Should().BeNull();
    }

    [Fact]
    public void The_whisper_downgrade_ladder_always_terminates_at_base()
    {
        var seen = new List<string> { ModelIds.WhisperLargeV3 };
        var current = HardwareRecommendationPolicy.DowngradeWhisper(ModelIds.WhisperLargeV3);

        for (var step = 0; step < 20 && current is not null; step++)
        {
            seen.Should().NotContain(current);
            seen.Add(current);
            current = HardwareRecommendationPolicy.DowngradeWhisper(current);
        }

        current.Should().BeNull();
        seen[^1].Should().Be(ModelIds.WhisperBase);
        seen.Should().OnlyContain(id => Catalog.Find(id) != null);
    }

    // -----------------------------------------------------------------------
    // compute-type wire names
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ComputeType.Float32, "float32")]
    [InlineData(ComputeType.Float16, "float16")]
    [InlineData(ComputeType.BFloat16, "bfloat16")]
    [InlineData(ComputeType.Int8Float16, "int8_float16")]
    [InlineData(ComputeType.Int8, "int8")]
    public void Describe_produces_the_ctranslate2_wire_name(ComputeType computeType, string expected)
    {
        HardwareRecommendationPolicy.Describe(computeType).Should().Be(expected);
    }

    [Theory]
    [InlineData("float32", ComputeType.Float32)]
    [InlineData("FP32", ComputeType.Float32)]
    [InlineData("float16", ComputeType.Float16)]
    [InlineData("fp16", ComputeType.Float16)]
    [InlineData("bfloat16", ComputeType.BFloat16)]
    [InlineData("BF16", ComputeType.BFloat16)]
    [InlineData("int8_float16", ComputeType.Int8Float16)]
    [InlineData("int8", ComputeType.Int8)]
    [InlineData("nonsense", ComputeType.Int8)]
    [InlineData(null, ComputeType.Int8)]
    public void Parse_reads_the_wire_name_back(string? value, ComputeType expected)
    {
        HardwareRecommendationPolicy.Parse(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(ComputeType.Float32)]
    [InlineData(ComputeType.Float16)]
    [InlineData(ComputeType.BFloat16)]
    [InlineData(ComputeType.Int8Float16)]
    [InlineData(ComputeType.Int8)]
    public void Describe_and_Parse_round_trip(ComputeType computeType)
    {
        HardwareRecommendationPolicy.Parse(HardwareRecommendationPolicy.Describe(computeType))
            .Should().Be(computeType);
    }

    [Fact]
    public void Vram_estimates_are_available_for_every_catalog_model_and_compute_type()
    {
        foreach (var model in Catalog.All)
        {
            foreach (var computeType in Enum.GetValues<ComputeType>())
            {
                Catalog.EstimatedVramGb(model.Id, computeType)
                    .Should().BeGreaterThan(0d, $"{model.Id} / {computeType.ToString()}");
            }
        }
    }

    [Fact]
    public void An_unknown_model_has_no_vram_estimate_rather_than_throwing()
    {
        Catalog.EstimatedVramGb("does-not-exist", ComputeType.Float16)
            .Should().Be(0d);
    }

    [Fact]
    public void Recommendations_are_pure_and_repeatable()
    {
        var first = HardwareRecommendationPolicy.Recommend(Gpu(12d), Catalog);
        var second = HardwareRecommendationPolicy.Recommend(Gpu(12d), Catalog);

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void The_rationale_reports_the_detected_vram()
    {
        var recommendation = HardwareRecommendationPolicy.Recommend(Gpu(12d), Catalog);

        recommendation.Rationale.Should().Contain(12d.ToString("0.#", CultureInfo.CurrentCulture) + "GB");
    }
}
