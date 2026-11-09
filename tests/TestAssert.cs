using System;
using System.Collections.Generic;

namespace Koi.Tests
{
    internal static class TestAssert
    {
        public static int Count { get; private set; }

        public static void True(bool condition, string message = null)
        {
            Count++;
            if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            Count++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException((message ?? "Values differ.") +
                    " Expected: " + expected + "; actual: " + actual);
        }

        public static T Throws<T>(Action action, string message = null) where T : Exception
        {
            Count++;
            try { action(); }
            catch (T ex) { return ex; }
            catch (Exception ex)
            {
                throw new InvalidOperationException((message ?? "Unexpected exception type.") +
                    " Expected: " + typeof(T).Name + "; actual: " + ex.GetType().Name, ex);
            }
            throw new InvalidOperationException(message ?? ("Expected " + typeof(T).Name + "."));
        }
    }
}
