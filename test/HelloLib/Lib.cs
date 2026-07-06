using System.Runtime.InteropServices;

public static class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "hello_add")]
    public static int Add(int a, int b) => a + b;
}
