using System;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppSystem
{
    public class Object : Il2CppObjectBase
    {
        public Object(IntPtr pointer) : base(pointer) { }
    }

    public class Type : Object
    {
        public Type(IntPtr pointer) : base(pointer) { }
    }
}

namespace Il2CppSystem.Collections
{
    // Il2CppInterop turns IL2CPP interfaces into classes.
    public class IEnumerator : Object
    {
        public IEnumerator(IntPtr pointer) : base(pointer) { }
    }

    public class IEnumerable : Object
    {
        public IEnumerable(IntPtr pointer) : base(pointer) { }
    }
}
