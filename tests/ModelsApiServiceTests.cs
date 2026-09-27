using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.Tests;

public class ModelsApiServiceTests
{
    [Fact]
    public async Task UnsupportedProviderReturnsNoEndpointAndNoModels()
    {
        Assert.Null(ModelsApiService.GetModelsEndpoint("Unsupported", "http://localhost"));
        Assert.Empty(await ModelsApiService.FetchModelsAsync("Unsupported", "http://localhost"));
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoAnEmptyModelList()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelsApiService.FetchModelsAsync("LMStudio", "http://127.0.0.1:1", cancellation.Token));
    }
}
