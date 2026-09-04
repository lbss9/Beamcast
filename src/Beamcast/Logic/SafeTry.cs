namespace Beamcast;

/// <summary>Runs a delegate and swallows exceptions for simple fallback paths.</summary>
public static class SafeTry
{
    public static T? Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch
        {
            return default;
        }
    }

    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch { }
    }

    public static async Task<T?> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch
        {
            return default;
        }
    }

    public static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch { }
    }
}
