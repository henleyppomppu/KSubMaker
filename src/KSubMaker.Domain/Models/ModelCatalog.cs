using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Models;

/// <summary>
/// Stable identifiers used in settings, the database and the worker protocol.
/// Model *names* live in the catalog; only these ids are allowed to appear in logic.
/// </summary>
public static class ModelIds
{
    public const string WhisperBase = "whisper-base";
    public const string WhisperSmall = "whisper-small";
    public const string WhisperMedium = "whisper-medium";
    public const string WhisperLargeV3 = "whisper-large-v3";
    public const string WhisperLargeV3Turbo = "whisper-large-v3-turbo";

    /// <summary>Japanese-only. Never recommended automatically — see the catalog entry.</summary>
    public const string WhisperKotobaV2 = "kotoba-whisper-v2.0";

    public const string TranslationNllb600M = "nllb-200-distilled-600M";
    public const string TranslationNllb13B = "nllb-200-distilled-1.3B";

    public const string LlmQwen3B = "qwen2.5-3b-instruct-q4km";
    public const string LlmQwen7B = "qwen2.5-7b-instruct-q4km";

    public const string LlmGemma3_4B = "gemma-3-4b-it-q4km";
    public const string LlmGemma3_12B = "gemma-3-12b-it-q4km";
}

/// <summary>How the inference stack consumes a downloaded model.</summary>
public enum ModelPayloadLayout
{
    /// <summary>CTranslate2 is handed the model directory and reads whatever is inside it.</summary>
    Directory,

    /// <summary>
    /// llama.cpp is handed one file. When the quantisation is split into shards that file must be the
    /// <b>first</b> shard; llama.cpp opens the rest by itself.
    /// </summary>
    EntryPointFile
}

/// <summary>
/// One downloadable model.
///
/// <para>The set of files is <b>discovered</b> from the repository listing using
/// <see cref="IncludePattern"/> / <see cref="ExcludePattern"/> — see <see cref="ModelFileSelector"/>
/// for why. <see cref="FallbackFiles"/> is only used when that listing cannot be fetched.</para>
/// </summary>
public sealed record ModelDescriptor
{
    public required string Id { get; init; }
    public required ModelKind Kind { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Hugging Face repository, e.g. <c>Systran/faster-whisper-large-v3</c>.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Regex (case-insensitive) matching the repository-relative paths that belong to this model.
    /// A repository holding exactly one model uses <see cref="ModelFileSelector.AnyFile"/>; a GGUF
    /// repository publishing a dozen quantisations names the one it wants, shards included.
    /// </summary>
    public required string IncludePattern { get; init; }

    /// <summary>Repository furniture to ignore. Overridable, but the default is right for every entry today.</summary>
    public string ExcludePattern { get; init; } = ModelFileSelector.DefaultExcludePattern;

    /// <summary>
    /// Regex that at least one selected file must match, otherwise the download is refused. Guards
    /// against a repository whose layout changed under us resolving to "a README and nothing else".
    /// </summary>
    public required string EssentialFilePattern { get; init; }

    /// <summary>
    /// Offline fallback file list, used only when the hub listing cannot be fetched. It is kept in step
    /// with the real repositories by <c>ModelCatalogFixtureTests</c> (offline, against checked-in
    /// listings) and by <c>ModelCatalogHubTests</c> (online).
    /// </summary>
    public required IReadOnlyList<string> FallbackFiles { get; init; }

    /// <summary>
    /// On-disk size of the selected files, measured from the repository listing. Drives the
    /// "예상 디스크 용량" column, which is shown before anything is downloaded.
    /// </summary>
    public required long ApproxSizeBytes { get; init; }

    /// <summary>VRAM estimate in GB keyed by compute type. Missing keys fall back to the float16 value.</summary>
    public required IReadOnlyDictionary<ComputeType, double> VramGbByComputeType { get; init; }

    public required string License { get; init; }
    public required string Description { get; init; }

    /// <summary>Whisper/NLLB models are consumed as a directory; GGUF LLM models as one entry-point file.</summary>
    public required ModelPayloadLayout Layout { get; init; }

    /// <summary>
    /// False when asking this model for word-level timestamps would break it.
    ///
    /// <para>faster-whisper builds word timings by reading the cross-attention of the decoder layers
    /// named in the model's <c>alignment_heads</c>. A distilled conversion that kept the teacher's
    /// list points at layers its own decoder does not have, and CTranslate2 reads past the end of
    /// the array: the worker dies with an ACCESS_VIOLATION (0xC0000005) that no <c>except</c> can
    /// catch, because the fault is in native code.</para>
    ///
    /// <para>Only meaningful for <see cref="ModelKind.Whisper"/>. Default true — this is a
    /// property of a specific broken conversion, not of distillation in general.</para>
    /// </summary>
    public bool SupportsWordTimestamps { get; init; } = true;
}

/// <summary>
/// The set of models the application knows how to install. Adding a model is a data change here,
/// never a code change in the pipeline — that is the "모델 이름을 하드코딩하지 말라" requirement.
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, ModelDescriptor> _byId;

    public ModelCatalog(IEnumerable<ModelDescriptor>? descriptors = null)
    {
        var list = descriptors?.ToList() ?? BuiltIn().ToList();
        _byId = list.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ModelDescriptor> All => _byId.Values;

    public IEnumerable<ModelDescriptor> OfKind(ModelKind kind) => _byId.Values.Where(m => m.Kind == kind);

    public ModelDescriptor? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var d) ? d : null;

    public ModelDescriptor Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"알 수 없는 모델 식별자입니다: {id}");

    /// <summary>
    /// Whether word-level timestamps are safe to request for <paramref name="modelId"/>.
    ///
    /// <para>An unknown id — including <c>"auto"</c> before it is resolved — answers true: the
    /// catalog cannot veto a model it has never heard of, and the built-in ones are all fine.</para>
    /// </summary>
    public bool SupportsWordTimestamps(string? modelId) =>
        Find(modelId)?.SupportsWordTimestamps ?? true;

    public double EstimatedVramGb(string modelId, ComputeType computeType)
    {
        var descriptor = Find(modelId);
        if (descriptor is null)
        {
            return 0d;
        }

        if (descriptor.VramGbByComputeType.TryGetValue(computeType, out var value))
        {
            return value;
        }

        return descriptor.VramGbByComputeType.TryGetValue(ComputeType.Float16, out var fallback)
            ? fallback
            : descriptor.VramGbByComputeType.Values.DefaultIfEmpty(0d).Max();
    }

    /// <summary>
    /// CTranslate2 conversions published by Systran (Whisper), the NLLB conversions and the Qwen GGUF
    /// builds.
    ///
    /// <para><b>The file lists below are a fallback, not the source of truth.</b> The downloader asks
    /// <c>huggingface.co/api/models/{repo}/tree/main</c> and applies <see cref="ModelDescriptor.IncludePattern"/>
    /// to whatever comes back; these lists are only used when that call fails. Every one of them, and
    /// every <see cref="ModelDescriptor.ApproxSizeBytes"/>, was measured against the real listing on
    /// 2026-08-02 and is pinned by <c>ModelCatalogFixtureTests</c>.</para>
    /// </summary>
    public static IEnumerable<ModelDescriptor> BuiltIn()
    {
        // Two CT2 layouts exist in the wild: the older Whisper conversions ship vocabulary.txt, the
        // large-v3 generation ships vocabulary.json plus preprocessor_config.json. Assuming the first
        // shape for all five is exactly the bug this catalog shipped with.
        static IReadOnlyList<string> WhisperLegacyFiles() =>
        [
            "config.json",
            "model.bin",
            "tokenizer.json",
            "vocabulary.txt"
        ];

        static IReadOnlyList<string> WhisperV3Files() =>
        [
            "config.json",
            "model.bin",
            "preprocessor_config.json",
            "tokenizer.json",
            "vocabulary.json"
        ];

        static IReadOnlyList<string> NllbFiles() =>
        [
            "config.json",
            "model.bin",
            "sentencepiece.bpe.model",
            "shared_vocabulary.json",
            "special_tokens_map.json",
            "tokenizer.json",
            "tokenizer_config.json"
        ];

        yield return new ModelDescriptor
        {
            Id = ModelIds.WhisperBase,
            Kind = ModelKind.Whisper,
            DisplayName = "Whisper base (CTranslate2)",
            RepositoryId = "Systran/faster-whisper-base",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = WhisperLegacyFiles(),
            ApproxSizeBytes = 147_882_941L, // 141 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 0.7,
                [ComputeType.Int8Float16] = 0.5,
                [ComputeType.Int8] = 0.4
            },
            License = "MIT (모델 가중치: OpenAI Whisper, MIT)",
            Description = "가장 가벼운 모델. 정확도는 낮지만 저사양 GPU와 CPU에서도 동작합니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.WhisperSmall,
            Kind = ModelKind.Whisper,
            DisplayName = "Whisper small (CTranslate2)",
            RepositoryId = "Systran/faster-whisper-small",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = WhisperLegacyFiles(),
            ApproxSizeBytes = 486_212_372L, // 464 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 1.6,
                [ComputeType.Int8Float16] = 1.0,
                [ComputeType.Int8] = 0.9
            },
            License = "MIT (모델 가중치: OpenAI Whisper, MIT)",
            Description = "VRAM 4GB 이하 환경 권장. 속도와 정확도의 균형이 좋습니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.WhisperMedium,
            Kind = ModelKind.Whisper,
            DisplayName = "Whisper medium (CTranslate2)",
            RepositoryId = "Systran/faster-whisper-medium",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = WhisperLegacyFiles(),
            ApproxSizeBytes = 1_530_571_735L, // 1,460 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 4.3,
                [ComputeType.Int8Float16] = 2.6,
                [ComputeType.Int8] = 2.2
            },
            License = "MIT (모델 가중치: OpenAI Whisper, MIT)",
            Description = "VRAM 6GB 환경 권장. int8_float16으로 실행하면 안정적입니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.WhisperLargeV3,
            Kind = ModelKind.Whisper,
            DisplayName = "Whisper large-v3 (CTranslate2)",
            RepositoryId = "Systran/faster-whisper-large-v3",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = WhisperV3Files(),
            ApproxSizeBytes = 3_090_835_702L, // 2,948 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 5.5,
                [ComputeType.Int8Float16] = 3.4,
                [ComputeType.Int8] = 3.1
            },
            License = "MIT (모델 가중치: OpenAI Whisper, MIT)",
            Description = "최고 품질. VRAM 12GB 이상에서 float16으로 실행하는 것을 권장합니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.WhisperLargeV3Turbo,
            Kind = ModelKind.Whisper,
            DisplayName = "Whisper large-v3-turbo (CTranslate2)",
            RepositoryId = "deepdml/faster-whisper-large-v3-turbo-ct2",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = WhisperV3Files(),
            ApproxSizeBytes = 1_621_665_983L, // 1,547 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 3.1,
                [ComputeType.Int8Float16] = 2.0,
                [ComputeType.Int8] = 1.8
            },
            License = "MIT (모델 가중치: OpenAI Whisper, MIT)",
            Description = "large-v3에 가까운 정확도에 약 4배 빠른 속도. VRAM 8GB 환경의 기본값입니다."
        };

        // Japanese-only, and deliberately not part of SelectWhisper. The recommendation runs before
        // anything has been transcribed, so it cannot know the source language; steering a user
        // there automatically would hand them a model that is *worse* on every other language.
        // It is an explicit choice for someone who knows their folder is Japanese.
        //
        // The file set is the same five names as the large-v3 generation, so it needs no new
        // layout handling — the whole entry is data.
        yield return new ModelDescriptor
        {
            Id = ModelIds.WhisperKotobaV2,
            Kind = ModelKind.Whisper,
            DisplayName = "Kotoba-Whisper v2.0 — 일본어 전용 (CTranslate2)",
            RepositoryId = "kotoba-tech/kotoba-whisper-v2.0-faster",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = WhisperV3Files(),
            ApproxSizeBytes = 1_516_480_096L, // 1,446 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 3.0,
                [ComputeType.Int8Float16] = 1.9,
                [ComputeType.Int8] = 1.8
            },
            // Measured, not precautionary: selecting this model with word timestamps on killed the
            // worker with 0xC0000005 mid-transcription. Its config.json carries large-v3's
            // alignment_heads verbatim — layers 7,10,12,13,16,17,19,21,24,25 — while the distilled
            // decoder has **two** layers. CTranslate2 indexes past the end and the process dies in
            // native code, so nothing in the worker can catch it. The only safe move is to never
            // ask this model for word timings.
            SupportsWordTimestamps = false,
            License = "MIT (모델 가중치: Kotoba Technologies, MIT)",
            Description =
                "일본어 음성에 특화된 Whisper 파인튜닝. large-v3의 절반 크기로 더 빠릅니다. " +
                "일본어 영상에만 쓰세요 — 다른 언어에서는 정확도가 떨어집니다. " +
                "이 모델은 단어 단위 타임스탬프를 지원하지 않아 자동으로 꺼집니다(자막 줄 나누기가 " +
                "조금 거칠어집니다)."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.TranslationNllb600M,
            Kind = ModelKind.Translation,
            DisplayName = "NLLB-200 distilled 600M (CTranslate2)",
            // "600B" was a typo that made every download of the default translation model fail with
            // 401/404 — the repository simply does not exist.
            RepositoryId = "entai2965/nllb-200-distilled-600M-ctranslate2",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = NllbFiles(),
            ApproxSizeBytes = 2_492_620_107L, // 2,377 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 1.6,
                [ComputeType.Int8Float16] = 1.0,
                [ComputeType.Int8] = 0.9
            },
            License = "CC-BY-NC-4.0 (비상업적 사용)",
            Description = "기본 번역 모델. 200개 언어 → 한국어. 빠르고 VRAM 사용량이 적습니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.TranslationNllb13B,
            Kind = ModelKind.Translation,
            DisplayName = "NLLB-200 distilled 1.3B (CTranslate2)",
            RepositoryId = "entai2965/nllb-200-distilled-1.3B-ctranslate2",
            IncludePattern = ModelFileSelector.AnyFile,
            EssentialFilePattern = ModelFileSelector.Ct2EssentialFile,
            Layout = ModelPayloadLayout.Directory,
            FallbackFiles = NllbFiles(),
            ApproxSizeBytes = 5_514_899_583L, // 5,259 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 3.2,
                [ComputeType.Int8Float16] = 1.9,
                [ComputeType.Int8] = 1.7
            },
            License = "CC-BY-NC-4.0 (비상업적 사용)",
            Description = "600M보다 문맥 처리가 좋습니다. VRAM 12GB 이상에서 권장합니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.LlmQwen3B,
            Kind = ModelKind.Llm,
            DisplayName = "Qwen2.5 3B Instruct (GGUF Q4_K_M)",
            RepositoryId = "Qwen/Qwen2.5-3B-Instruct-GGUF",
            // The repository publishes ten quantisations; the shard suffix is optional because Qwen
            // splits a build the moment it crosses ~4 GB and does so without warning.
            IncludePattern = @"^qwen2\.5-3b-instruct-q4_k_m(?:-\d+-of-\d+)?\.gguf$",
            EssentialFilePattern = ModelFileSelector.GgufEssentialFile,
            Layout = ModelPayloadLayout.EntryPointFile,
            FallbackFiles = ["qwen2.5-3b-instruct-q4_k_m.gguf"],
            ApproxSizeBytes = 2_104_932_768L, // 2,007 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 3.0,
                [ComputeType.Int8Float16] = 2.6,
                [ComputeType.Int8] = 2.4
            },
            License = "Apache-2.0",
            Description = "가벼운 로컬 LLM. 문체 지시를 따르는 번역이 필요할 때 사용합니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.LlmQwen7B,
            Kind = ModelKind.Llm,
            DisplayName = "Qwen2.5 7B Instruct (GGUF Q4_K_M)",
            RepositoryId = "Qwen/Qwen2.5-7B-Instruct-GGUF",
            // q4_k_m of the 7B build *is* split in two: the unsharded name the catalog used to ask for
            // has never existed in this repository.
            IncludePattern = @"^qwen2\.5-7b-instruct-q4_k_m(?:-\d+-of-\d+)?\.gguf$",
            EssentialFilePattern = ModelFileSelector.GgufEssentialFile,
            Layout = ModelPayloadLayout.EntryPointFile,
            FallbackFiles =
            [
                "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf",
                "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf"
            ],
            ApproxSizeBytes = 4_683_073_632L, // 4,466 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 6.2,
                [ComputeType.Int8Float16] = 5.6,
                [ComputeType.Int8] = 5.2
            },
            License = "Apache-2.0",
            Description = "Qwen 계열 중 큰 쪽. 중국어로 새는 경향이 있어 일→한 자막에는 권장하지 않습니다."
        };

        // Gemma 3 is the LLM engine's recommended family for this app, and the reason is measured.
        // Qwen2.5 7B produced Chinese for 41% of the lines of a Japanese file (측정 표본 B: 113 of 273
        // output lines were Han-only 간체자, e.g. "请多关照", "翻开教科书第39页") while another 15%
        // fell through untranslated — 57% of the file was not Korean. Qwen is a Chinese-centric
        // family and drifts there when the source is not English; a Korean instruction in the system
        // prompt did not hold it.
        //
        // EXAONE was the obvious "strong Korean" answer and is **wrong for this job**: LG publishes
        // it as a bilingual English/Korean model and its card does not mention Japanese at all. The
        // source language here is Japanese, so a model that cannot read it is worse than Qwen.
        //
        // Google's own gemma-3-*-qat-*-gguf repositories are `gated: "manual"` — the downloader has
        // no token, so those would 401 at download time. The unsloth mirrors publish the same
        // quantisations ungated, which is the same reasoning that put the deepdml and entai2965
        // conversions in this catalog.
        yield return new ModelDescriptor
        {
            Id = ModelIds.LlmGemma3_4B,
            Kind = ModelKind.Llm,
            DisplayName = "Gemma 3 4B Instruct (GGUF Q4_K_M)",
            RepositoryId = "unsloth/gemma-3-4b-it-GGUF",
            // The repository also publishes an mmproj vision file and a dozen other quantisations;
            // the pattern names exactly one. The shard suffix is optional for the same reason as
            // Qwen: a build crossing ~4GB gets split without warning.
            IncludePattern = @"^gemma-3-4b-it-q4_k_m(?:-\d+-of-\d+)?\.gguf$",
            EssentialFilePattern = ModelFileSelector.GgufEssentialFile,
            Layout = ModelPayloadLayout.EntryPointFile,
            FallbackFiles = ["gemma-3-4b-it-Q4_K_M.gguf"],
            ApproxSizeBytes = 2_489_894_016L, // 2,374 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 3.6,
                [ComputeType.Int8Float16] = 3.2,
                [ComputeType.Int8] = 3.0
            },
            License = "Gemma Terms of Use",
            Description = "일→한 자막용 기본 LLM. 다국어 모델이라 중국어로 새지 않고, VRAM 12GB에서 음성 인식 모델과 함께 올라갑니다."
        };

        yield return new ModelDescriptor
        {
            Id = ModelIds.LlmGemma3_12B,
            Kind = ModelKind.Llm,
            DisplayName = "Gemma 3 12B Instruct (GGUF Q4_K_M)",
            RepositoryId = "unsloth/gemma-3-12b-it-GGUF",
            IncludePattern = @"^gemma-3-12b-it-q4_k_m(?:-\d+-of-\d+)?\.gguf$",
            EssentialFilePattern = ModelFileSelector.GgufEssentialFile,
            Layout = ModelPayloadLayout.EntryPointFile,
            FallbackFiles = ["gemma-3-12b-it-Q4_K_M.gguf"],
            ApproxSizeBytes = 7_300_778_336L, // 6,963 MiB
            VramGbByComputeType = new Dictionary<ComputeType, double>
            {
                [ComputeType.Float16] = 8.6,
                [ComputeType.Int8Float16] = 8.0,
                [ComputeType.Int8] = 7.6
            },
            License = "Gemma Terms of Use",
            Description = "품질 우선. 12GB에서는 음성 인식 모델과 동시에 올라가지 않아 처리 방식 B로 내려갑니다. VRAM 16GB 이상 권장."
        };
    }
}
