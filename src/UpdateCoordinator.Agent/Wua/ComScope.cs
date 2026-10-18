using System.Runtime.InteropServices;

namespace UpdateCoordinator.Agent.Wua;

/// <summary>
/// Collects COM objects and releases them in reverse order when disposed. The WUA API leaks memory
/// if its objects are left to the garbage collector, so every object obtained from it is tracked.
/// </summary>
internal sealed class ComScope : IDisposable
{
    private readonly Stack<object> _objects = new();

    public object Track(object instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _objects.Push(instance);
        }

        return instance!;
    }

    public void Dispose()
    {
        while (_objects.TryPop(out var instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
