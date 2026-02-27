using System.Reflection;

namespace RemotePlayServer.Tests.Helpers;

/// <summary>
/// Helper for setting private fields in unit tests.
/// Used to manipulate time-dependent state (warmup, cooldown, recovery).
/// </summary>
internal static class ReflectionHelper
{
    public static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new ArgumentException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    public static T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new ArgumentException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T)field.GetValue(obj)!;
    }

    public static object? InvokePrivateStaticMethod(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new ArgumentException($"Method '{methodName}' not found on {type.Name}");
        return method.Invoke(null, args);
    }
}
