using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.WebAPI;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.MiniMax;
using VideoStages.Architectures.Wan;

namespace VideoStages.Tests;

internal static class TestResolvedVideoModel
{
    internal static ResolvedVideoModel Create(
        string modelName,
        ModelProfileId modelProfileId,
        VideoArchitectureDescriptor architecture,
        string modelClassId = null,
        string compatibilityClassId = null,
        IReadOnlyList<FrameReferencePosition> referencePositions = null,
        bool lorasTargetTextEncoder = true) =>
        new(
            modelName,
            modelProfileId,
            architecture,
            modelClassId ?? modelName,
            compatibilityClassId ?? architecture.Id.Value,
            referencePositions ?? [],
            lorasTargetTextEncoder);
}

internal static class UnitTestStubs
{
    public static WorkflowGenerator StubGenerator(JObject workflow) =>
        new()
        {
            UserInput = new T2IParamInput(null),
            Workflow = workflow
        };
}

internal sealed class SwarmUiTestContext : IDisposable
{
    private readonly Dictionary<string, T2IModelHandler> _priorModelSets;
    private readonly bool _priorIncludeHash;
    private readonly List<WorkflowGenerator.WorkflowGenStep> _priorModelGenSteps;
    private readonly ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>> _priorExtraModelProviders;
    private readonly ConcurrentDictionary<string, string> _priorClipModelsValid;
    private readonly ConcurrentDictionary<string, string> _priorVisionModelsValid;

    public SwarmUiTestContext(
        bool disableImageMetadataModelHash = true,
        bool resetExtraModelProviders = true,
        bool clearModelGenSteps = true)
    {
        // Tests replace handlers in the shared dictionary.
        _priorModelSets = new Dictionary<string, T2IModelHandler>(Program.T2IModelSets);
        _priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        _priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        _priorExtraModelProviders = ModelsAPI.ExtraModelProviders;
        // Isolate model caches so filtered tests resolve the same models as full-suite runs.
        _priorClipModelsValid = WorkflowGenerator.ClipModelsValid;
        _priorVisionModelsValid = WorkflowGenerator.VisionModelsValid;
        WorkflowGenerator.ClipModelsValid = new(_priorClipModelsValid);
        WorkflowGenerator.VisionModelsValid = new(_priorVisionModelsValid);

        if (disableImageMetadataModelHash)
        {
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = false;
        }
        if (resetExtraModelProviders)
        {
            ModelsAPI.ExtraModelProviders = new ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>>(
                [
                    new KeyValuePair<string, Func<string, Dictionary<string, JObject>>>("unit_test", _ => [])
                ]);
        }
        if (clearModelGenSteps)
        {
            WorkflowGenerator.ModelGenSteps = [];
        }
    }

    public void Dispose()
    {
        WorkflowGenerator.VisionModelsValid = _priorVisionModelsValid;
        WorkflowGenerator.ClipModelsValid = _priorClipModelsValid;
        WorkflowGenerator.ModelGenSteps = _priorModelGenSteps;
        ModelsAPI.ExtraModelProviders = _priorExtraModelProviders;
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>(_priorModelSets);
        Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = _priorIncludeHash;
    }
}

internal sealed record TestModelBundle(
    T2IModel BaseModel,
    T2IModel VideoModel,
    T2IModel GemmaModel = null,
    T2IModel VisionModel = null);

internal sealed record MixedVideoModelBundle(
    T2IModel BaseModel,
    T2IModel LtxVideoModel,
    T2IModel WanVideoModel,
    T2IModel GemmaModel);

internal static class TestModelFactory
{
    public static TestModelBundle CreateBaseAndVideoModels()
    {
        return CreateBaseAndVideoModels(T2IModelClassSorter.CompatSvd, "unit-video", "Unit Video");
    }

    public static TestModelBundle CreateBaseAndUnsupportedVideoModels()
    {
        return CreateBaseAndVideoModels(
            new T2IModelCompatClass { ID = "unit-unsupported" },
            "unit-unsupported",
            "Unit Unsupported");
    }

    public static TestModelBundle CreateBaseAndLtxv2VideoModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatLtxv2,
            "unit-video-ltxv2",
            "Unit Video LTXV2");
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = "lightricks-ltx-video-2-3",
            Name = "LTX Video 2.3",
        };
        return models;
    }

    public static TestModelBundle CreateBaseAndMiniMaxH3Models()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatMiniMaxH3,
            MiniMaxArchitectureModule.ModelClassId,
            "MiniMax H3");
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            StandardWidth = 1344,
            StandardHeight = 768,
        };
        InstallMiniMaxSupportModels();
        return models;
    }

    public static void InstallMiniMaxSupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors");
        Install("VAE", CommonModels.Known["minimax-h3-video-vae"].FileName);
        Install("VAE", CommonModels.Known["minimax-h3-video-int8-vae"].FileName);
        Install("VAE", CommonModels.Known["minimax-h3-audio-vae"].FileName);
    }

    // Core downloads support models when the fixture misses an exact filename.
    public static void InstallLtx2SupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "gemma_3_12B_it.safetensors");
        Install("Clip", "ltx-2.3_text_projection_bf16.safetensors");
        Install("VAE", CommonModels.Known["ltx2-3-video-vae"].FileName);
        Install("VAE", CommonModels.Known["ltx2-3-audio-vae"].FileName);
    }

    public const string Hunyuan15ClipVisionFileName = "sigclip_vision_patch14_384.safetensors";

    // Name the vision model in the request to keep RequireVisionModel from downloading it.
    public static T2IModel InstallHunyuan15SupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("ClipVision");
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "qwen_2.5_vl_7b_fp8_scaled.safetensors");
        Install("Clip", "byt5_small_glyphxl_fp16.safetensors");
        Install("VAE", CommonModels.Known["hunyuan-video-1_5-vae"].FileName);
        return TestStubModel.Install(
            Program.T2IModelSets["ClipVision"],
            Hunyuan15ClipVisionFileName);
    }

    public static void InstallMochiSupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "t5xxl_enconly.safetensors");
        Install("VAE", CommonModels.Known["mochi-vae"].FileName);
    }

    private static void EnsureModelSet(string modelType)
    {
        if (!Program.T2IModelSets.ContainsKey(modelType))
        {
            Program.T2IModelSets[modelType] = new() { ModelType = modelType };
        }
    }

    public static TestModelBundle CreateBaseAndWan22ImageToVideoModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatWan21_14b,
            WanArchitectureModule.ImageToVideoModelClassId,
            "Wan 2.2 Image2Video 14B");
        return models with { VisionModel = InstallWanSupportModels() };
    }

    public static TestModelBundle CreateBaseAndWan22Ti2v5bModels()
    {
        TestModelBundle models = CreateBaseAndVideoModels(
            T2IModelClassSorter.CompatWan22_5b,
            WanArchitectureModule.Ti2v5bModelClassId,
            "Wan 2.2 Text/Image2Video 5B");
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            StandardWidth = 960,
            StandardHeight = 960,
        };
        return models with { VisionModel = InstallWanSupportModels() };
    }

    public static MixedVideoModelBundle CreateBaseLtxv2AndWan22ImageToVideoModels()
    {
        TestModelBundle ltx = CreateBaseAndLtxv2VideoModels();
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel wan = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, "UnitTest_Wan22.safetensors"), "UnitTest_Wan22.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = WanArchitectureModule.ImageToVideoModelClassId,
                Name = "Wan 2.2 Image2Video 14B",
                CompatClass = T2IModelClassSorter.CompatWan21_14b,
                StandardWidth = 1024,
                StandardHeight = 576,
            },
        };
        handler.Models[wan.Name] = wan;
        InstallWanSupportModels();
        return new(ltx.BaseModel, ltx.VideoModel, wan, ltx.GemmaModel);
    }

    public static (
        T2IModel BaseModel,
        T2IModel LtxVideoModel,
        T2IModel MiniMaxVideoModel) CreateBaseLtxv2AndMiniMaxH3Models()
    {
        TestModelBundle ltx = CreateBaseAndLtxv2VideoModels();
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel miniMax = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, "UnitTest_MiniMaxH3.safetensors"), "UnitTest_MiniMaxH3.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = MiniMaxArchitectureModule.ModelClassId,
                Name = "MiniMax H3",
                CompatClass = T2IModelClassSorter.CompatMiniMaxH3,
                StandardWidth = 1344,
                StandardHeight = 768,
            },
        };
        handler.Models[miniMax.Name] = miniMax;
        InstallMiniMaxSupportModels();
        return (ltx.BaseModel, ltx.VideoModel, miniMax);
    }

    public const string WanClipVisionFileName = "clip_vision_h.safetensors";

    // Name the vision model in the request to keep RequireVisionModel from downloading it.
    public static T2IModel InstallWanSupportModels()
    {
        EnsureModelSet("Stable-Diffusion");
        EnsureModelSet("Clip");
        EnsureModelSet("ClipVision");
        EnsureModelSet("VAE");
        if (CommonModels.Known.IsEmpty)
        {
            CommonModels.RegisterCoreSet();
        }
        Install("Clip", "umt5_xxl_fp8_e4m3fn_scaled.safetensors");
        Install("VAE", CommonModels.Known["wan21-vae"].FileName);
        Install("VAE", CommonModels.Known["wan22-vae"].FileName);
        return TestStubModel.Install(
            Program.T2IModelSets["ClipVision"],
            WanClipVisionFileName);
    }

    private static void Install(string modelType, string fileName)
    {
        TestStubModel.Install(Program.T2IModelSets[modelType], fileName);
    }

    private static TestModelBundle CreateBaseAndVideoModelsWithClass(T2IModelClass videoClass)
    {
        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler clipHandler = new() { ModelType = "Clip" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["Clip"] = clipHandler
        };

        T2IModelCompatClass baseCompat = new() { ID = "unit-base-compat", ShortCode = "UB" };
        T2IModelClass baseClass = new()
        {
            ID = "unit-base",
            Name = "Unit Base",
            CompatClass = baseCompat,
            StandardWidth = 1024,
            StandardHeight = 1024
        };

        T2IModel baseModel = new(sdHandler, TestStubModel.Folder(sdHandler), TestStubModel.File(sdHandler, "UnitTest_Base.safetensors"), "UnitTest_Base.safetensors")
        {
            ModelClass = baseClass
        };
        T2IModel videoModel = new(sdHandler, TestStubModel.Folder(sdHandler), TestStubModel.File(sdHandler, "UnitTest_Video.safetensors"), "UnitTest_Video.safetensors")
        {
            ModelClass = videoClass
        };
        T2IModel gemmaModel = new(clipHandler, TestStubModel.Folder(clipHandler), TestStubModel.File(clipHandler, "gemma_3_12B_it.safetensors"), "gemma_3_12B_it.safetensors")
        {
            ModelClass = new T2IModelClass
            {
                ID = "unit-gemma",
                Name = "Unit Gemma",
                CompatClass = baseCompat,
                StandardWidth = 1024,
                StandardHeight = 1024
            }
        };

        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[videoModel.Name] = videoModel;
        clipHandler.Models[gemmaModel.Name] = gemmaModel;
        return new TestModelBundle(baseModel, videoModel, gemmaModel);
    }

    private static TestModelBundle CreateBaseAndVideoModels(T2IModelCompatClass videoCompat, string videoClassId, string videoClassName)
    {
        T2IModelClass videoClass = new()
        {
            ID = videoClassId,
            Name = videoClassName,
            CompatClass = videoCompat,
            StandardWidth = 1024,
            StandardHeight = 576
        };
        return CreateBaseAndVideoModelsWithClass(videoClass);
    }
}
