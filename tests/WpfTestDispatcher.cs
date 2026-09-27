using System.Windows.Threading;

namespace LiveCaptionsTranslator.Tests;

internal static class WpfTestDispatcher
{
    private static readonly Lazy<Dispatcher> dispatcher = new(CreateDispatcher);

    public static void Invoke(Action action) => dispatcher.Value.Invoke(action);

    private static Dispatcher CreateDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher current = Dispatcher.CurrentDispatcher;
            ready.SetResult(current);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "LiveCaptionsTranslator WPF test dispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
