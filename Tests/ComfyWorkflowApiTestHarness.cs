using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.Runtime.CompilerServices;

namespace VideoStages.Tests;

// Uses the API behind "Import from Generate Tab".
internal static class ComfyWorkflowApiTestHarness
{
    private static readonly BackendHandler TestBackends = new();

    // Backend startup normally discovers the custom nodes for media uploads and WebSocket saves.
    public static readonly string[] SwarmNodeFeatures =
        ["comfy_loadimage_b64", "comfy_saveimage_ws", Ltx2HostIntegration.FeatureFlag];

    public static async Task<JObject> GenerateAsync(
        JObject postParameters,
        IEnumerable<string> extraFeatures = null,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> extraSteps = null)
    {
        ArgumentNullException.ThrowIfNull(postParameters);

        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];
        BackendHandler priorBackends = Program.Backends;
        HashSet<string> priorFeatures = ComfyUIBackendExtension.FeaturesSupported;
        try
        {
            ComfyUIBackendExtension.FeaturesSupported =
                [.. priorFeatures, .. SwarmNodeFeatures, .. extraFeatures ?? []];
            WorkflowGenerator.Steps = [.. WorkflowTestHarness.ProductionSteps()
                .Concat(extraSteps ?? [])
                .OrderBy(step => step.Priority)];
            InstallNoNetworkComfyBackend();
            JObject response = await ComfyUIWebAPI.ComfyGetGeneratedWorkflow(
                CreateSession(),
                postParameters);
            if (response["error"] is JToken error)
            {
                // Preserve the exception type that identifies a user-facing refusal.
                throw new SwarmReadableErrorException(
                    error.Type == JTokenType.String ? error.Value<string>() : error.ToString());
            }
            string workflow = response.Value<string>("workflow")
                ?? throw new InvalidOperationException(
                    "ComfyGetGeneratedWorkflow returned no workflow.");
            return JObject.Parse(workflow);
        }
        finally
        {
            TestBackends.AllBackends.Clear();
            Program.Backends = priorBackends;
            WorkflowGenerator.Steps = priorSteps;
            ComfyUIBackendExtension.FeaturesSupported = priorFeatures;
        }
    }

    public static async Task<(JObject Workflow, WorkflowGenerator Generator)> GenerateWithStateAsync(
        JObject postParameters,
        IEnumerable<string> extraFeatures = null,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> extraSteps = null)
    {
        // Capture the generator after core's cleanup, before the route discards it.
        WorkflowGenerator generator = null;
        JObject workflow = await GenerateAsync(
            postParameters,
            extraFeatures,
            [.. extraSteps ?? [], new(g => generator = g, double.MaxValue)]);
        return (workflow, generator ?? throw new InvalidOperationException(
            "The capture step never ran, so a step set SkipFurtherSteps before it."));
    }

    private static void InstallNoNetworkComfyBackend()
    {
        TestBackends.AllBackends.Clear();
        ComfyUIAPIBackend backend = new()
        {
            SettingsRaw = new ComfyUIAPIBackend.ComfyUIAPISettings
            {
                Address = "http://comfy-api-test.invalid",
            },
            ModelFolderFormat = "/",
            Status = BackendStatus.RUNNING,
        };
        BackendHandler.T2IBackendData data = new()
        {
            ID = int.MinValue,
            Backend = backend,
        };
        backend.BackendData = data;
        backend.Handler = TestBackends;
        TestBackends.AllBackends[data.ID] = data;
        Program.Backends = TestBackends;
    }

    private static Session CreateSession()
    {
        User user = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
        user.Data = new User.DatabaseEntry { ID = "video-stages-api-test" };
        // GetUninitializedObject skips the Settings initializer that core relies on.
        user.Settings = new Settings.User();
        user.CalculatedRole = new Role("video-stages-api-test")
        {
            Data = new Role.RoleData
            {
                PermissionFlags = ["*"],
            },
        };
        return new Session
        {
            ID = "video-stages-api-test",
            User = user,
            Persist = false,
        };
    }
}
