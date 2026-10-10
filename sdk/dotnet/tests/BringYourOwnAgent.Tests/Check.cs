using System;
using System.Collections.Generic;

namespace BringYourOwnAgent.Tests;

/// <summary>A minimal test harness: no test framework, no dependencies.</summary>
internal static class Check
{
    public static int Passed { get; private set; }

    public static int Failed { get; private set; }

    public static void Run(string name, Action test)
    {
        try
        {
            test();
            Passed++;
        }
        catch (Exception exception)
        {
            Failed++;
            Console.WriteLine("FAIL " + name);
            Console.WriteLine("     " + (exception is TestFailure ? exception.Message : exception.ToString()));
        }
    }

    public static void Equal<T>(T expected, T actual, string what = "value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailure(what + ": expected " + Show(expected) + ", got " + Show(actual));
        }
    }

    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new TestFailure(what);
        }
    }

    public static TException Throws<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception other)
        {
            throw new TestFailure(what + ": expected " + typeof(TException).Name + ", got " + other.GetType().Name);
        }

        throw new TestFailure(what + ": expected " + typeof(TException).Name + ", nothing was thrown");
    }

    private static string Show<T>(T value) => value is null ? "null" : "<" + value + ">";

    private sealed class TestFailure : Exception
    {
        public TestFailure(string message)
            : base(message)
        {
        }
    }
}
