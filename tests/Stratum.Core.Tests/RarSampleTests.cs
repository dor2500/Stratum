using Stratum.Core;

namespace Stratum.Core.Tests;

/// <summary>
/// RAR archives cannot be created by Stratum, so there is no generated fixture.
/// Point STRATUM_RAR_SAMPLE at any .rar file to exercise the extract path.
/// </summary>
public sealed class RarSampleTests
{
    [Fact]
    public async Task Rar_sample_lists_tests_and_extracts()
    {
        var sample = Environment.GetEnvironmentVariable("STRATUM_RAR_SAMPLE");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;

        var reader = await ArchiveReader.OpenAsync(sample);
        Assert.StartsWith("Rar", reader.FormatName);
        Assert.NotEmpty(reader.Entries);
        if (reader.HasEncryptedEntries) return;

        Assert.True(await reader.TestAsync());

        var dest = Path.Combine(Path.GetTempPath(), "stratum-rar-" + Guid.NewGuid().ToString("N"));
        try
        {
            await reader.ExtractAllAsync(dest);
            var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
            Assert.Equal(files.Count, Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count());
            Assert.Equal(files.Sum(e => e.Size), Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
        }
        finally
        {
            try { Directory.Delete(dest, true); } catch (IOException) { }
        }
    }
}
