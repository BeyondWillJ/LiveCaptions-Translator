using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string dataDirectory = Path.Combine(AppContext.BaseDirectory, "test-data", "shared");
        Environment.SetEnvironmentVariable("LIVECAPTIONS_TRANSLATOR_DATA_DIR", dataDirectory);
    }
}
