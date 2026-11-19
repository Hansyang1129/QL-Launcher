using System.Text;
using Qinglan.Launcher.Core.Diagnostics;

namespace Qinglan.Tests;

/// <summary>R2 本地错误归因：分类命中、兜底原文、日志尾部与本地指纹。</summary>
internal static class DiagnosisSuite
{
    public static void Run()
    {
        T.Group("R2 归因：分类命中");
        Categories();
        T.Group("R2 归因：未知兜底与指纹");
        UnknownAndFingerprint();
        T.Group("R2 归因：日志尾部");
        Tail();
    }

    private static QlErrorContext Ctx(int code, string[]? stderr = null,
        string[]? recent = null, string[]? launcher = null, Exception? ex = null)
        => new(code, stderr ?? Array.Empty<string>(), recent ?? Array.Empty<string>(),
            launcher ?? Array.Empty<string>(), ex);

    private static void Categories()
    {
        var missing = QlErrorDiagnoser.Diagnose(Ctx(-1,
            ex: new IOException("CreateProcess error=2, 系统找不到指定的文件")));
        T.CheckEq("缺 Java 命中", "java-missing", missing.RuleId);
        T.Check("缺 Java 有建议", missing.Suggestions.Count >= 1);

        var version = QlErrorDiagnoser.Diagnose(Ctx(1,
            stderr: new[]
            {
                "java.lang.UnsupportedClassVersionError: net/minecraft/Main has been compiled by a more recent version (class file version 61.0)",
            }));
        T.CheckEq("Java 版本不匹配命中", "java-version", version.RuleId);

        var oom = QlErrorDiagnoser.Diagnose(Ctx(1,
            stderr: new[] { "java.lang.OutOfMemoryError: Java heap space" }));
        T.CheckEq("OOM 命中", "oom", oom.RuleId);

        var mods = QlErrorDiagnoser.Diagnose(Ctx(1,
            recent: new[] { "net.fabricmc.loader.impl.FormattedException: Incompatible mod set!" }));
        T.CheckEq("模组冲突命中", "mod-conflict", mods.RuleId);

        var download = QlErrorDiagnoser.Diagnose(Ctx(-1,
            launcher: new[] { "启动文件下载失败 2 个: 客户端 jar; timed out" }));
        T.CheckEq("下载失败命中", "download-network", download.RuleId);

        var auth = QlErrorDiagnoser.Diagnose(Ctx(-1,
            stderr: new[] { "QlAuthException: invalid_grant" }));
        T.CheckEq("登录失败命中", "auth", auth.RuleId);

        var native = QlErrorDiagnoser.Diagnose(Ctx(-1073741819));
        T.CheckEq("原生崩溃退出码命中", "native-crash", native.RuleId);
    }

    private static void UnknownAndFingerprint()
    {
        var unknown = QlErrorDiagnoser.Diagnose(Ctx(42,
            stderr: new[] { "some entirely new crash signature" }));
        T.CheckEq("未知兜底", "unknown", unknown.RuleId);
        T.Check("未知保留原始日志",
            unknown.RawExcerpt.Contains("some entirely new crash signature", StringComparison.Ordinal));
        T.Check("未知也给出建议", unknown.Suggestions.Count >= 1);
        T.Check("指纹非空", unknown.Fingerprint.Length > 0);
        T.Check("指纹大小非零", unknown.SizeBytes > 0);

        var again = QlErrorDiagnoser.Diagnose(Ctx(42,
            stderr: new[] { "some entirely new crash signature" }));
        T.CheckEq("同输入指纹稳定", unknown.Fingerprint, again.Fingerprint);
    }

    private static void Tail()
    {
        var path = Path.Combine(Fx.TempDir(), "launcher.log");
        File.WriteAllLines(path, Enumerable.Range(1, 300).Select(i => "line " + i),
            new UTF8Encoding(false));
        var tail = QlLogTail.ReadTail(path, 5);
        T.CheckEq("日志尾部行数", 5, tail.Count);
        T.CheckEq("日志尾部末行", "line 300", tail[^1]);
    }
}
