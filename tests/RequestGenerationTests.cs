using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class RequestGenerationTests
{
    [Fact]
    public void BeginningANewRequest_CancelsAndInvalidatesPreviousResult()
    {
        var requests = new RequestGeneration();

        RequestLease first = requests.Begin();
        Assert.True(requests.IsCurrent(first));

        RequestLease second = requests.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(requests.IsCurrent(first));
        Assert.True(requests.IsCurrent(second));

        requests.CancelCurrent();

        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(requests.IsCurrent(second));
    }
}
