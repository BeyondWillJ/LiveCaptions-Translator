using System.Collections.Specialized;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.apis
{
    public static class LLMRequestDataFactory
    {
        private static readonly OrderedDictionary typeSequence = new()
        {
            ["integrated"] = typeof(IntegratedLLMRequestData),
            ["Aliyun"] = typeof(AliyunRequestData),
            ["Anthropic"] = typeof(AnthropicRequestData),
            ["Ollama"] = typeof(OllamaRequestData),
            ["OpenRouter"] = typeof(OpenRouterRequestData),
            ["OpenAI"] = typeof(OpenAIRequestData),
            ["XAI"] = typeof(XAIRequestData),
            ["base"] = typeof(BaseLLMRequestData)
        };

        public static int FallbackCount => typeSequence.Count;

        public static BaseLLMRequestData Create(string platform, string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            if (typeSequence[platform] is not Type requestType)
                throw new ArgumentException($"No request format is registered for '{platform}'.", nameof(platform));
            return Create(requestType, model, messages, temperature);
        }

        public static BaseLLMRequestData Create(int index, string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            if (index < 0 || index >= typeSequence.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (typeSequence[index] is not Type requestType)
                throw new InvalidOperationException($"No request format is registered at index {index}.");
            return Create(requestType, model, messages, temperature);
        }

        public static BaseLLMRequestData Create(string model, List<BaseLLMConfig.Message> messages, double temperature)
        {
            return new BaseLLMRequestData(model, messages, temperature);
        }

        private static BaseLLMRequestData Create(
            Type requestType, string model, List<BaseLLMConfig.Message> messages, double temperature) =>
            Activator.CreateInstance(requestType, model, messages, temperature) as BaseLLMRequestData
            ?? throw new InvalidOperationException($"Could not create request format '{requestType.Name}'.");
    }
}
