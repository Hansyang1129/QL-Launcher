namespace Qinglan.Tests;

/// <summary>极简断言/计数 runner（零依赖；打印 PASS/FAIL 与计数，退出码对 CI 友好）。</summary>
internal static class T
{
    private static int _pass;
    private static int _fail;
    private static string _group = "";

    public static void Group(string name)
    {
        _group = name;
        Console.WriteLine();
        Console.WriteLine("== " + name + " ==");
    }

    public static void Check(string name, bool condition)
    {
        if (condition)
        {
            Interlocked.Increment(ref _pass);
            Console.WriteLine("  [PASS] " + name);
        }
        else
        {
            Interlocked.Increment(ref _fail);
            Console.WriteLine("  [FAIL] " + name + "   （" + _group + "）");
        }
    }

    public static void CheckEq(string name, object expected, object actual)
        => Check(name + "（期望 " + expected + "，实际 " + actual + "）",
            Equals(expected, actual) || string.Equals(expected?.ToString(), actual?.ToString(), StringComparison.Ordinal));

    public static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine("== 结果: " + _pass + " 通过 / " + _fail + " 失败 ==");
        return _fail == 0 ? 0 : 1;
    }
}
