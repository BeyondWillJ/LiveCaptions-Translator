using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public class LLMRequestDataFactoryTests
{
    [Fact]
    public void FallbackIndicesRemainConstructibleInDefinedOrder()
    {
        Type[] expectedTypes =
        [
            typeof(IntegratedLLMRequestData),
            typeof(AliyunRequestData),
            typeof(AnthropicRequestData),
            typeof(OllamaRequestData),
            typeof(OpenRouterRequestData),
            typeof(OpenAIRequestData),
            typeof(XAIRequestData),
            typeof(BaseLLMRequestData)
        ];
        var messages = new List<BaseLLMConfig.Message>
        {
            new() { role = "user", content = "test" }
        };

        Assert.Equal(expectedTypes.Length, LLMRequestDataFactory.FallbackCount);
        for (int index = 0; index < expectedTypes.Length; index++)
        {
            BaseLLMRequestData request = LLMRequestDataFactory.Create(index, "model", messages, 0.5);
            Assert.Equal(expectedTypes[index], request.GetType());
        }
    }

    [Fact]
    public void InvalidPlatformOrFallbackIndexFailsExplicitly()
    {
        var messages = new List<BaseLLMConfig.Message>();

        Assert.Throws<ArgumentException>(() =>
            LLMRequestDataFactory.Create("unknown", "model", messages, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LLMRequestDataFactory.Create(LLMRequestDataFactory.FallbackCount, "model", messages, 0.5));
    }
}
