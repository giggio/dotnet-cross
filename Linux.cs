using System.Runtime.InteropServices;

namespace Cross;

internal static partial class Linux
{
    [LibraryImport("libc")]
    public static partial uint getuid();
    [LibraryImport("libc")]
    public static partial uint getgid();
}
