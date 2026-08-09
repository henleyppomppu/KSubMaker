using FluentAssertions;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// Pins every catalog entry to a captured hub listing: the offline fallback list, the essential
/// file, the entry point of a split model and the "예상 디스크 용량" number.
///
/// <para>This is the offline half of the guard. <c>ModelCatalogHubTests</c> runs the same questions
/// against the live API and skips when there is no network; this one runs everywhere and is what
/// makes a wrong file list a red test instead of a failed user download.</para>
/// </summary>
public sealed class ModelCatalogFixtureTests
{
    public static TheoryData<string> BuiltInModelIds()
    {
        var data = new TheoryData<string>();

        foreach (var descriptor in ModelCatalog.BuiltIn())
        {
            data.Add(descriptor.Id);
        }

        return data;
    }

    private static ModelDescriptor Get(string modelId) =>
        ModelCatalog.BuiltIn().Single(descriptor => descriptor.Id == modelId);

    [Theory]
    [MemberData(nameof(BuiltInModelIds))]
    public void Every_model_has_a_captured_listing(string modelId)
    {
        var descriptor = Get(modelId);

        HuggingFaceListings.Exists(descriptor.RepositoryId).Should().BeTrue(
            "adding a model without capturing its listing leaves the file list unverifiable offline; " +
            "see tests/fixtures/huggingface/README.txt");
    }

    [Theory]
    [MemberData(nameof(BuiltInModelIds))]
    public void The_selection_rule_resolves_to_a_usable_set(string modelId)
    {
        var descriptor = Get(modelId);

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(descriptor.RepositoryId));

        selected.Should().NotBeEmpty();
        ModelFileSelector.DescribeProblem(descriptor, selected).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(BuiltInModelIds))]
    public void The_offline_fallback_matches_what_the_repository_publishes(string modelId)
    {
        var descriptor = Get(modelId);

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(descriptor.RepositoryId));

        // The fallback is only used when the hub is unreachable, which is exactly when a wrong entry
        // is hardest to diagnose. It has to be the same set the discovery path would produce.
        descriptor.FallbackFiles.Should().BeEquivalentTo(selected);
    }

    [Theory]
    [MemberData(nameof(BuiltInModelIds))]
    public void Every_fallback_file_exists_in_the_repository(string modelId)
    {
        var descriptor = Get(modelId);
        var published = HuggingFaceListings.Paths(descriptor.RepositoryId);

        descriptor.FallbackFiles.Should().BeSubsetOf(published);
    }

    [Theory]
    [MemberData(nameof(BuiltInModelIds))]
    public void The_estimated_size_is_the_measured_size(string modelId)
    {
        var descriptor = Get(modelId);

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(descriptor.RepositoryId));
        var measured = HuggingFaceListings.TotalBytes(descriptor.RepositoryId, selected);

        // The column is called "예상 디스크 용량" and users plan a download around it; an estimate that
        // was typed rather than measured is how the 7B entry ended up 4.68 GB short of two shards.
        descriptor.ApproxSizeBytes.Should().Be(measured);
    }

    [Theory]
    [MemberData(nameof(BuiltInModelIds))]
    public void A_single_file_model_points_at_its_first_shard(string modelId)
    {
        var descriptor = Get(modelId);

        if (descriptor.Layout != ModelPayloadLayout.EntryPointFile)
        {
            return;
        }

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(descriptor.RepositoryId));

        ModelFileSelector.EntryPointFile(descriptor, selected).Should().Be(selected[0]);
        selected.Should().OnlyContain(file => file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_the_conversion_that_crashes_declines_word_timestamps()
    {
        // The flag exists for one measured failure, not as a general distillation caveat:
        // kotoba-whisper-v2.0 ships large-v3's alignment_heads (decoder layers up to 25) on a
        // two-layer decoder, so asking it for word timings takes the worker down with an
        // ACCESS_VIOLATION. Anything else claiming the same exemption is almost certainly a typo.
        var declining = ModelCatalog.BuiltIn()
            .Where(descriptor => !descriptor.SupportsWordTimestamps)
            .Select(descriptor => descriptor.Id);

        declining.Should().Equal(ModelIds.WhisperKotobaV2);
    }

    [Fact]
    public void Every_model_that_is_not_whisper_leaves_the_flag_alone()
    {
        // Word timestamps are an ASR concept. A translation model or an LLM setting the flag would
        // be meaningless and would read as though it meant something.
        ModelCatalog.BuiltIn()
            .Where(descriptor => descriptor.Kind != ModelKind.Whisper)
            .Should().OnlyContain(descriptor => descriptor.SupportsWordTimestamps);
    }

    [Fact]
    public void The_split_seven_b_model_needs_both_shards()
    {
        var descriptor = Get(ModelIds.LlmQwen7B);

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(descriptor.RepositoryId));

        selected.Should().HaveCount(2, "q4_k_m of Qwen2.5-7B is published as two shards");
        ModelFileSelector.EntryPointFile(descriptor, selected)
            .Should().EndWith("-00001-of-00002.gguf");
    }

    [Fact]
    public void The_default_translation_repository_is_the_600M_one()
    {
        // "600B" does not exist; that typo made the default translation model impossible to install.
        Get(ModelIds.TranslationNllb600M).RepositoryId
            .Should().Be("entai2965/nllb-200-distilled-600M-ctranslate2");
    }

    [Fact]
    public void Model_ids_are_unique()
    {
        var ids = ModelCatalog.BuiltIn().Select(descriptor => descriptor.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }
}
