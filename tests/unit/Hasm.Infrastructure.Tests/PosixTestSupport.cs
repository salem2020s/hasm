using System.ComponentModel;
using System.Diagnostics;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Fake PostgreSQL tools (POSIX shell scripts) under the test OUTPUT directory, which is exec-capable unlike a possibly <c>noexec</c> temporary folder. Write every tool first, then call <see cref="WarmUp"/>:
/// it runs each tool's <c>--version</c> once and retries ONLY when the launch fails with ETXTBSY (errno 26: a fork in another test thread still held the file open for writing); any other launch error fails at once.
/// </summary>
public sealed class FakeTools : IDisposable
{
    private const int Etxtbsy = 26;
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "fake-pg", Guid.NewGuid().ToString("N"));
    private readonly List<string> written = [];

    public string Dir(string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A tool printing <c>&lt;tool&gt; (PostgreSQL) &lt;major&gt;.1</c> for <c>--version</c>; otherwise it runs <paramref name="body"/>.</summary>
    public void Write(string dir, string tool, string major, string body) =>
        WriteRaw(dir, tool, $"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo \"{tool} (PostgreSQL) {major}.1\"; exit 0; fi\n{body}\n");

    public void WriteRaw(string dir, string tool, string script)
    {
        var path = Path.Combine(dir, tool);
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        written.Add(path);
    }

    public void WarmUp() { foreach (var path in written) RunVersion(path); }

    public static bool IsBusyTextFile(Win32Exception ex) => ex.NativeErrorCode == Etxtbsy;

    /// <summary>Runs <c>&lt;path&gt; --version</c> to completion; retries only on ETXTBSY (bounded). <paramref name="onAttempt"/> counts attempts (tests).</summary>
    public static void RunVersion(string path, Action? onAttempt = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            onAttempt?.Invoke();
            try
            {
                using var p = Process.Start(new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return;
            }
            catch (Win32Exception ex) when (IsBusyTextFile(ex) && attempt < 20) { Thread.Sleep(50); }
        }
    }

    public void Dispose() { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } }
}
