namespace BoltMacro;

public static class IdUtil
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}
