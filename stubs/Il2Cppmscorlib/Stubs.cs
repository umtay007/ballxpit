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

    public class Random : Object
    {
        public Random(IntPtr pointer) : base(pointer) { }
        public Random(int Seed) : base(IntPtr.Zero) => throw null;
    }

    public class Delegate : Object
    {
        public Delegate(IntPtr pointer) : base(pointer) { }
    }

    public class MulticastDelegate : Delegate
    {
        public MulticastDelegate(IntPtr pointer) : base(pointer) { }
    }
}

namespace Il2CppSystem.Collections.Generic
{
    public class List<T> : Object
    {
        public List(IntPtr pointer) : base(pointer) { }
        public List() : base(IntPtr.Zero) => throw null;
        public int Count => throw null;
        public T this[int index] => throw null;
        public void Add(T item) => throw null;
        public bool Remove(T item) => throw null;
        public void RemoveAt(int index) => throw null;
        public void Clear() => throw null;
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
