using System.Reflection;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class TestIsolationContractTests
{
    [Fact]
    public void Every_test_class_joins_the_shared_collection()
    {
        string[] offenders = [.. typeof(TestIsolationContractTests).Assembly
            .GetTypes()
            .Where(IsTestClass)
            .Where(type => CollectionNameOf(type) != "VideoStagesTests")
            .Select(type => type.FullName)
            .Order()];

        Assert.True(
            offenders.Length == 0,
            "Test classes outside the \"VideoStagesTests\" collection run in parallel with it "
                + "and race on SwarmUI's static state. Add [Collection(\"VideoStagesTests\")] to:\n  "
                + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_core_comfy_parameter_has_a_test_registration()
    {
        string[] unregistered = [.. typeof(ComfyUIBackendExtension)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(UnregisteredNames)
            .Order()];

        Assert.True(
            unregistered.Length == 0,
            "The shared test fixture left these ComfyUI parameters unregistered:\n  "
                + string.Join("\n  ", unregistered));
    }

    private static IEnumerable<string> UnregisteredNames(FieldInfo field)
    {
        if (IsRegisteredParam(field.FieldType))
        {
            return field.GetValue(null) is null ? [field.Name] : [];
        }
        if (!IsRegisteredParam(field.FieldType.GetElementType()))
        {
            return [];
        }
        Array slots = (Array)field.GetValue(null) ?? Array.CreateInstance(field.FieldType, 0);
        return Enumerable.Range(0, slots.Length)
            .Where(index => slots.GetValue(index) is null)
            .Select(index => $"{field.Name}[{index}]");
    }

    private static bool IsRegisteredParam(Type type) =>
        type is { IsGenericType: true }
        && type.GetGenericTypeDefinition() == typeof(T2IRegisteredParam<>);

    /// <summary>xUnit v2's CollectionAttribute keeps its name in the constructor argument only.</summary>
    private static string CollectionNameOf(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments[0].Value as string;

    private static bool IsTestClass(Type type) =>
        type.IsClass
        && !type.IsAbstract
        && type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(method => method.GetCustomAttribute<FactAttribute>() is not null);
}
