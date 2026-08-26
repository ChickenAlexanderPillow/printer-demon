namespace PrinterDemon;

internal static class StaTaskRunner
{
    public static Task<T> RunAsync<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(operation());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Printer Demon STA worker"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
