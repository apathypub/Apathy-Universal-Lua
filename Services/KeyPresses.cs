namespace Apathy.Services;

/// <summary>
/// Counts physical key presses from a low-level keyboard hook. Script keybinds are polled every tick, and a quick tap
/// can start and end between two polls; comparing these counts catches it anyway.
/// </summary>
public static class KeyPresses
{
    private static readonly int[] Counts = new int[256];
    private static readonly bool[] Held = new bool[256];
    private static int _started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        new Thread(() => Native.RunPhysicalKeyHook(OnKey, CancellationToken.None))
        {
            IsBackground = true, Name = "Key presses", Priority = ThreadPriority.Highest,
        }.Start();
    }

    /// <summary>How many times the key has gone down (keyboard keys only; mouse buttons stay 0).</summary>
    public static int Count(int vk) => vk is > 0 and < 256 ? Volatile.Read(ref Counts[vk]) : 0;

    private static void OnKey(int vk, bool down)
    {
        if (vk is <= 0 or >= 256) return;
        if (down && !Held[vk])
        {
            Interlocked.Increment(ref Counts[vk]);
            // The hook reports left/right modifiers; keybinds may use the generic Shift / Ctrl / Alt codes.
            int generic = vk switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => 0 };
            if (generic != 0) Interlocked.Increment(ref Counts[generic]);
        }
        Held[vk] = down; // key repeat sends more "down"s while held; only the first one is a press
    }
}
