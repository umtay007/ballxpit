using System;

namespace UnityEngine
{
    public sealed class AudioListener : Behaviour
    {
        public AudioListener(IntPtr pointer) : base(pointer) { }
    }

    public sealed class AudioSettings : Il2CppSystem.Object
    {
        public AudioSettings(IntPtr pointer) : base(pointer) { }
        public static int outputSampleRate => throw null;
    }
}
