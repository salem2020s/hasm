namespace Hasm.Infrastructure.Tests;

/// <summary>Business code receives <c>CompanyAuditedWork.Transaction</c> publicly but must never commit it: the audited transaction owns the commit (audit requirement, poison check, anchoring).</summary>
public class CompanyTransactionDisciplineTests
{
    [Fact]
    public void No_source_file_commits_the_transaction_of_an_audited_work_directly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "backend", "Hasm.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, n: i + 1)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"\b(work|unit|w)\.Transaction\.(Commit|CommitAsync|Dispose|DisposeAsync)\b"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n}")
            .ToList();
        Assert.Empty(offenders);
    }
}
