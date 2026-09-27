using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class TextUtilTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("\nhello", "hello")]
    [InlineData("hello\n\nworld", "hello—world")]
    [InlineData("你好\n\n世界", "你好——世界")]
    public void ReplaceNewlines_HandlesEmptySegments(string input, string expected)
    {
        Assert.Equal(expected, TextUtil.ReplaceNewlines(input, TextUtil.MEDIUM_THRESHOLD));
    }
}
