// Copyright (c) 2012-2023 Wojciech Figat. All rights reserved.

#if USE_NETCORE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using FlaxEngine.Assertions;

#pragma warning disable 1591

namespace FlaxEngine.Interop
{
    /// <summary>
    /// Wrapper for managed arrays which are passed to unmanaged code.
    /// </summary>
    public unsafe class ManagedArray
    {
        private ManagedHandle _pinnedArrayHandle;
        private IntPtr _unmanagedData;
        private Type _arrayType;
        private Type _elementType;
        private int _elementSize;
        private int _length;

        public static ManagedArray WrapNewArray(Array arr) => new ManagedArray(arr, arr.GetType());

        public static ManagedArray WrapNewArray(Array arr, Type arrayType) => new ManagedArray(arr, arrayType);

        /// <summary>
        /// Returns an instance of ManagedArray from shared pool.
        /// </summary>
        /// <remarks>The resources must be released by calling FreePooled() instead of Free()-method.</remarks>
        public static ManagedArray WrapPooledArray(Array arr)
        {
            ManagedArray managedArray = ManagedArrayPool.Get();
            managedArray.WrapArray(arr, arr.GetType());
            return managedArray;
        }

        /// <summary>
        /// Returns an instance of ManagedArray from shared pool.
        /// </summary>
        /// <remarks>The resources must be released by calling FreePooled() instead of Free()-method.</remarks>
        public static ManagedArray WrapPooledArray(Array arr, Type arrayType)
        {
            ManagedArray managedArray = ManagedArrayPool.Get();
            managedArray.WrapArray(arr, arrayType);
            return managedArray;
        }

        internal static ManagedArray AllocateNewArray(int length, Type arrayType, Type elementType)
            => new ManagedArray((IntPtr)NativeInterop.NativeAlloc(length, Marshal.SizeOf(elementType)), length, arrayType, elementType);

        internal static ManagedArray AllocateNewArray(IntPtr ptr, int length, Type arrayType, Type elementType)
            => new ManagedArray(ptr, length, arrayType, elementType);

        /// <summary>
        /// Returns an instance of ManagedArray from shared pool.
        /// </summary>
        /// <remarks>The resources must be released by calling FreePooled() instead of Free()-method.</remarks>
        public static ManagedArray AllocatePooledArray<T>(T* ptr, int length) where T : unmanaged
        {
            ManagedArray managedArray = ManagedArrayPool.Get();
            managedArray.Allocate(ptr, length);
            return managedArray;
        }

        /// <summary>
        /// Returns an instance of ManagedArray from shared pool.
        /// </summary>
        /// <remarks>The resources must be released by calling FreePooled() instead of Free()-method.</remarks>
        public static ManagedArray AllocatePooledArray<T>(int length) where T : unmanaged
        {
            ManagedArray managedArray = ManagedArrayPool.Get();
            managedArray.Allocate((T*)NativeInterop.NativeAlloc(length, Unsafe.SizeOf<T>()), length);
            return managedArray;
        }

        public ManagedArray(Array arr, Type elementType) => WrapArray(arr, elementType);

        internal void WrapArray(Array arr, Type arrayType)
        {
            _pinnedArrayHandle = ManagedHandle.Alloc(arr, GCHandleType.Pinned);
            _unmanagedData = Marshal.UnsafeAddrOfPinnedArrayElement(arr, 0);
            _length = arr.Length;
            _arrayType = arrayType;
            _elementType = arr.GetType().GetElementType();
            _elementSize = Marshal.SizeOf(_elementType);
        }

        internal void Allocate<T>(T* ptr, int length) where T : unmanaged
        {
            _unmanagedData = new IntPtr(ptr);
            _length = length;
            _arrayType = typeof(T).MakeArrayType();
            _elementType = typeof(T);
            _elementSize = Unsafe.SizeOf<T>();
        }

        private ManagedArray()
        {
        }

        private ManagedArray(IntPtr ptr, int length, Type arrayType, Type elementType)
        {
            Assert.IsTrue(arrayType.IsArray);
            _unmanagedData = ptr;
            _length = length;
            _arrayType = arrayType;
            _elementType = elementType;
            _elementSize = Marshal.SizeOf(elementType);
        }

        ~ManagedArray()
        {
            if (_unmanagedData != IntPtr.Zero)
                Free();
        }

        public void Free()
        {
            GC.SuppressFinalize(this);
            if (_pinnedArrayHandle.IsAllocated)
            {
                _pinnedArrayHandle.Free();
                _unmanagedData = IntPtr.Zero;
            }
            if (_unmanagedData != IntPtr.Zero)
            {
                NativeInterop.NativeFree(_unmanagedData.ToPointer());
                _unmanagedData = IntPtr.Zero;
            }
        }

        public void FreePooled()
        {
            Free();
            ManagedArrayPool.Put(this);
        }

        internal IntPtr Pointer => _unmanagedData;

        internal int Length => _length;

        internal int ElementSize => _elementSize;

        internal Type ElementType => _elementType;

        internal Type ArrayType => _arrayType;

        public Span<T> ToSpan<T>() where T : struct => new Span<T>(_unmanagedData.ToPointer(), _length);

        public T[] ToArray<T>() where T : struct => new Span<T>(_unmanagedData.ToPointer(), _length).ToArray();

        public Array ToArray() => ArrayCast.ToArray(new Span<byte>(_unmanagedData.ToPointer(), _length * _elementSize), _elementType);

        /// <summary>
        /// Creates an Array of the specified type from span of bytes.
        /// </summary>
        private static class ArrayCast
        {
            delegate Array GetDelegate(Span<byte> span);

            [ThreadStatic]
            private static Dictionary<Type, GetDelegate> delegates;

            internal static Array ToArray(Span<byte> span, Type type)
            {
                if (delegates == null)
                    delegates = new();
                if (!delegates.TryGetValue(type, out var deleg))
                {
                    deleg = typeof(ArrayCastInternal<>).MakeGenericType(type).GetMethod(nameof(ArrayCastInternal<int>.ToArray), BindingFlags.Static | BindingFlags.NonPublic).CreateDelegate<GetDelegate>();
                    delegates.Add(type, deleg);
                }
                return deleg(span);
            }

            private static class ArrayCastInternal<T> where T : struct
            {
                internal static Array ToArray(Span<byte> span)
                {
                    return MemoryMarshal.Cast<byte, T>(span).ToArray();
                }
            }
        }

        /// <summary>
        /// Provides a pool of pre-allocated ManagedArray that can be re-used.
        /// </summary>
        private static class ManagedArrayPool
        {
            [ThreadStatic]
            private static List<ValueTuple<bool, ManagedArray>> pool;

            internal static ManagedArray Get()
            {
                if (pool == null)
                    pool = new List<ValueTuple<bool, ManagedArray>>();
                for (int i = 0; i < pool.Count; i++)
                {
                    if (!pool[i].Item1)
                    {
                        var tuple = pool[i];
                        tuple.Item1 = true;
                        pool[i] = tuple;
                        return tuple.Item2;
                    }
                }

                var newTuple = (true, new ManagedArray());
                pool.Add(newTuple);
                return newTuple.Item2;
            }

            internal static void Put(ManagedArray obj)
            {
                for (int i = 0; i < pool.Count; i++)
                {
                    if (pool[i].Item2 == obj)
                    {
                        var tuple = pool[i];
                        tuple.Item1 = false;
                        pool[i] = tuple;
                        return;
                    }
                }

                throw new Exception("Tried to free non-pooled ManagedArray as pooled ManagedArray");
            }
        }
    }

    internal static class ManagedString
    {
        internal static ManagedHandle EmptyStringHandle = ManagedHandle.Alloc(string.Empty);

        [System.Diagnostics.DebuggerStepThrough]
        internal static unsafe IntPtr ToNative(string str)
        {
            if (str == null)
                return IntPtr.Zero;
            else if (str == string.Empty)
                return ManagedHandle.ToIntPtr(EmptyStringHandle);
            Assert.IsTrue(str.Length > 0);
            return ManagedHandle.ToIntPtr(str);
        }

        [System.Diagnostics.DebuggerStepThrough]
        internal static unsafe IntPtr ToNativeWeak(string str)
        {
            if (str == null)
                return IntPtr.Zero;
            else if (str == string.Empty)
                return ManagedHandle.ToIntPtr(EmptyStringHandle);
            Assert.IsTrue(str.Length > 0);
            return ManagedHandle.ToIntPtr(str, GCHandleType.Weak);
        }

        [System.Diagnostics.DebuggerStepThrough]
        internal static string ToManaged(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return null;
            return Unsafe.As<string>(ManagedHandle.FromIntPtr(ptr).Target);
        }

        [System.Diagnostics.DebuggerStepThrough]
        internal static void Free(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return;
            ManagedHandle handle = ManagedHandle.FromIntPtr(ptr);
            if (handle == EmptyStringHandle)
                return;
            handle.Free();
        }
    }

    /// <summary>
    /// Handle to managed objects which can be stored in native code.
    /// </summary>
    public struct ManagedHandle
    {
        private IntPtr handle;

        private ManagedHandle(IntPtr handle)
        {
            this.handle = handle;
        }

        private ManagedHandle(object value, GCHandleType type)
        {
            handle = ManagedHandlePool.AllocateHandle(value, type);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ManagedHandle Alloc(object value) => new ManagedHandle(value, GCHandleType.Normal);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ManagedHandle Alloc(object value, GCHandleType type) => new ManagedHandle(value, type);

        public void Free()
        {
            if (handle != IntPtr.Zero)
            {
                ManagedHandlePool.FreeHandle(handle);
                handle = IntPtr.Zero;
            }

            ManagedHandlePool.TryCollectWeakHandles();
        }

        public object Target
        {
            get => ManagedHandlePool.GetObject(handle);
            set => ManagedHandlePool.SetObject(handle, value);
        }

        public bool IsAllocated => handle != IntPtr.Zero;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator ManagedHandle(IntPtr value) => FromIntPtr(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ManagedHandle FromIntPtr(IntPtr value) => new ManagedHandle(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator IntPtr(ManagedHandle value) => ToIntPtr(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr ToIntPtr(object value) => ManagedHandlePool.AllocateHandle(value, GCHandleType.Normal);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr ToIntPtr(object value, GCHandleType type) => ManagedHandlePool.AllocateHandle(value, type);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr ToIntPtr(ManagedHandle value) => value.handle;

        public override int GetHashCode() => handle.GetHashCode();

        public override bool Equals(object o) => o is ManagedHandle other && Equals(other);

        public bool Equals(ManagedHandle other) => handle == other.handle;

        public static bool operator ==(ManagedHandle a, ManagedHandle b) => a.handle == b.handle;

        public static bool operator !=(ManagedHandle a, ManagedHandle b) => a.handle != b.handle;

        private static class ManagedHandlePool
        {
            private static ulong normalHandleAccumulator = 0;
            private static ulong pinnedHandleAccumulator = 0;
            private static ulong weakHandleAccumulator = 0;

            private static object poolLock = new object();
            private static Dictionary<IntPtr, object> persistentPool = new Dictionary<nint, object>();
            private static Dictionary<IntPtr, GCHandle> pinnedPool = new Dictionary<nint, GCHandle>();

            private static Dictionary<IntPtr, object> weakPool1 = new Dictionary<nint, object>();
            private static Dictionary<IntPtr, object> weakPool2 = new Dictionary<nint, object>();
            private static Dictionary<IntPtr, object> weakPool = weakPool1;
            private static Dictionary<IntPtr, object> weakPoolOther = weakPool2;

            private static int nextCollection = GC.CollectionCount(0) + 1;

            /// <summary>
            /// Tries to free all references to old weak handles so GC can collect them.
            /// </summary>
            internal static void TryCollectWeakHandles()
            {
                if (GC.CollectionCount(0) < nextCollection)
                    return;

                lock (poolLock)
                {
                    nextCollection = GC.CollectionCount(0) + 1;

                    var swap = weakPoolOther;
                    weakPoolOther = weakPool;
                    weakPool = swap;

                    weakPool.Clear();
                }
            }

            private static IntPtr NewHandle(GCHandleType type)
            {
                IntPtr handle;
                if (type == GCHandleType.Normal)
                    handle = (IntPtr)Interlocked.Increment(ref normalHandleAccumulator);
                else if (type == GCHandleType.Pinned)
                    handle = (IntPtr)Interlocked.Increment(ref pinnedHandleAccumulator);
                else //if (type == GCHandleType.Weak || type == GCHandleType.WeakTrackResurrection)
                    handle = (IntPtr)Interlocked.Increment(ref weakHandleAccumulator);

                // Two bits reserved for the type
                handle |= (IntPtr)(((ulong)type << 62) & 0xC000000000000000);
                return handle;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static GCHandleType GetHandleType(IntPtr handle)
            {
                return (GCHandleType)(((ulong)handle & 0xC000000000000000) >> 62);
            }

            internal static IntPtr AllocateHandle(object value, GCHandleType type)
            {
                IntPtr handle = NewHandle(type);
                lock (poolLock)
                {
                    if (type == GCHandleType.Normal)
                        persistentPool.Add(handle, value);
                    else if (type == GCHandleType.Pinned)
                        pinnedPool.Add(handle, GCHandle.Alloc(value, GCHandleType.Pinned));
                    else if (type == GCHandleType.Weak || type == GCHandleType.WeakTrackResurrection)
                        weakPool.Add(handle, value);
                }

                return handle;
            }

            internal static object GetObject(IntPtr handle)
            {
                object value;
                GCHandleType type = GetHandleType(handle);
                lock (poolLock)
                {
                    if (type == GCHandleType.Normal && persistentPool.TryGetValue(handle, out value))
                        return value;
                    else if (type == GCHandleType.Pinned && pinnedPool.TryGetValue(handle, out GCHandle gchandle))
                        return gchandle.Target;
                    else if (weakPool.TryGetValue(handle, out value))
                        return value;
                    else if (weakPoolOther.TryGetValue(handle, out value))
                        return value;
                }

                throw new Exception("Invalid ManagedHandle");
            }

            internal static void SetObject(IntPtr handle, object value)
            {
                GCHandleType type = GetHandleType(handle);
                lock (poolLock)
                {
                    if (type == GCHandleType.Normal && persistentPool.ContainsKey(handle))
                        persistentPool[handle] = value;
                    else if (type == GCHandleType.Pinned && pinnedPool.TryGetValue(handle, out GCHandle gchandle))
                        gchandle.Target = value;
                    else if (weakPool.ContainsKey(handle))
                        weakPool[handle] = value;
                    else if (weakPoolOther.ContainsKey(handle))
                        weakPoolOther[handle] = value;
                    else
                        throw new Exception("Invalid ManagedHandle");
                }
            }

            internal static void FreeHandle(IntPtr handle)
            {
                GCHandleType type = GetHandleType(handle);
                lock (poolLock)
                {
                    if (type == GCHandleType.Normal && persistentPool.Remove(handle))
                        return;
                    else if (type == GCHandleType.Pinned && pinnedPool.Remove(handle, out GCHandle gchandle))
                    {
                        gchandle.Free();
                        return;
                    }
                    else if (weakPool.Remove(handle))
                        return;
                    else if (weakPoolOther.Remove(handle))
                        return;
                }

                throw new Exception("Invalid ManagedHandle");
            }
        }
    }
}

#endif