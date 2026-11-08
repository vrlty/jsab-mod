using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace JsabPractice;

// The game's seeded random state, read without touching MyMath's interop wrapper: that runs
// MyMath's static constructor, which throws (SEHException in il2cpp_runtime_class_init) and,
// from Plugin.Load, took the whole plugin down. Read straight from the class's static fields
// instead (Il2CppClass.static_fields at +0xB8; then isInit, tabCos, tabSin, roomSeed at
// +0x18, DEBUG_useMathRandom at +0x1C).
//
// Every random choice in a run follows from roomSeed (MyMath.generateNewSeed, new each run
// and restart) and the spawn's idInSong and randomOffset (MyMath.randomFromMetaSpawn); the
// editor ports that, so recordings save the seed of the run they hold.
internal static class GameRandom
{
    private static IntPtr _myMath;

    private static IntPtr Statics()
    {
        if (_myMath == IntPtr.Zero)
            _myMath = IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "", "MyMath");
        return _myMath == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(_myMath, 0xB8);
    }

    // MyMath.roomSeed; null if the class isn't set up yet.
    internal static int? RoomSeed()
    {
        IntPtr statics = Statics();
        return statics == IntPtr.Zero ? null : Marshal.ReadInt32(statics, 0x18);
    }

    // MyMath.DEBUG_useMathRandom: when set, the game skips the seeded generator.
    internal static bool? DebugUseMathRandom()
    {
        IntPtr statics = Statics();
        return statics == IntPtr.Zero ? null : Marshal.ReadByte(statics, 0x1C) != 0;
    }
}
