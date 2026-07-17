using System.Reflection;

namespace Aetheria.Tests;

/// <summary>Marks a public static parameterless method as a test case for the MiniTest runner.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public TestAttribute(string? description = null) => Description = description;

    public string? Description { get; }
}

/// <summary>Raised by <see cref="Assert"/> when an expectation fails.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

/// <summary>xUnit-shaped assertions so migrating to xUnit later is a mechanical rename.</summary>
public static class Assert
{
    public static void True(bool condition, string? because = null)
    {
        if (!condition)
        {
            throw new AssertionException($"Expected true. {because}".TrimEnd());
        }
    }

    public static void False(bool condition, string? because = null)
    {
        if (condition)
        {
            throw new AssertionException($"Expected false. {because}".TrimEnd());
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"Expected <{expected}> but got <{actual}>.");
        }
    }

    public static void Close(float expected, float actual, float tolerance = 1e-4f)
    {
        if (System.Math.Abs(expected - actual) > tolerance)
        {
            throw new AssertionException(
                $"Expected <{expected}> ± {tolerance} but got <{actual}>.");
        }
    }

    public static void Contains(int expected, IEnumerable<int> collection)
    {
        foreach (int item in collection)
        {
            if (item == expected)
            {
                return;
            }
        }

        throw new AssertionException($"Expected collection to contain <{expected}>.");
    }

    public static void DoesNotContain(int unexpected, IEnumerable<int> collection)
    {
        foreach (int item in collection)
        {
            if (item == unexpected)
            {
                throw new AssertionException($"Expected collection to NOT contain <{unexpected}>.");
            }
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException(
                $"Expected {typeof(TException).Name} but got {ex.GetType().Name}.");
        }

        throw new AssertionException($"Expected {typeof(TException).Name} but no exception was thrown.");
    }
}

/// <summary>
/// A tiny zero-dependency test runner: discovers every public static parameterless method tagged
/// with <see cref="TestAttribute"/>, runs it, and reports pass/fail. Returns a non-zero exit code
/// if anything fails, so CI treats a failing test as a failing build.
/// </summary>
public static class MiniTestRunner
{
    public static int RunAll(Assembly assembly)
    {
        var tests = DiscoverTests(assembly);
        int passed = 0;
        var failures = new List<string>();

        Console.WriteLine($"Running {tests.Count} test(s)...\n");

        foreach (MethodInfo test in tests)
        {
            string name = $"{test.DeclaringType?.Name}.{test.Name}";
            try
            {
                test.Invoke(null, null);
                passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (TargetInvocationException tie)
            {
                Exception inner = tie.InnerException ?? tie;
                failures.Add($"{name}: {inner.Message}");
                Console.WriteLine($"  FAIL  {name}\n          {inner.Message}");
            }
        }

        Console.WriteLine($"\n{passed} passed, {failures.Count} failed, {tests.Count} total.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static List<MethodInfo> DiscoverTests(Assembly assembly)
    {
        var result = new List<MethodInfo>();
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<TestAttribute>() is not null &&
                    method.GetParameters().Length == 0)
                {
                    result.Add(method);
                }
            }
        }

        return result;
    }
}
