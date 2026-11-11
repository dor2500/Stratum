using Stratum.Core;

namespace Stratum.Core.Tests;

public sealed class ArchiveRoundTripTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "stratum-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _out;

    public ArchiveRoundTripTests()
    {
        _source = Path.Combine(_work, "source");
        _out = Path.Combine(_work, "out");
        Directory.CreateDirectory(Path.Combine(_source, "docs", "nested"));
        File.WriteAllText(Path.Combine(_source, "readme.txt"), "hello stratum");
        File.WriteAllText(Path.Combine(_source, "docs", "a.txt"), new string('a', 5000));
        File.WriteAllText(Path.Combine(_source, "docs", "nested", "שלום.txt"), "unicode name");

        // Incompressible data, so split-volume tests really produce several volumes.
        var noise = new byte[300_000];
        new Random(42).NextBytes(noise);
        File.WriteAllBytes(Path.Combine(_source, "noise.bin"), noise);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private string Archive(string name) => Path.Combine(_work, name);

    private void AssertExtractedMatchesSource(string extractedRoot)
    {
        foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_work, file);
            var extracted = Path.Combine(extractedRoot, relative);
            Assert.True(File.Exists(extracted), $"missing {relative}");
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(extracted));
        }
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    [InlineData(CreateFormat.Tar)]
    public async Task Create_then_extract_restores_every_file(CreateFormat format)
    {
        var path = Archive("plain" + CreateOptions.Extension(format));
        var written = await ArchiveWriter.CreateAsync(path, [_source], new CreateOptions { Format = format });
        Assert.Equal([path], written);

        var reader = await ArchiveReader.OpenAsync(path);
        Assert.Equal(4, reader.Entries.Count(e => !e.IsDirectory));
        Assert.False(reader.HasEncryptedEntries);
        Assert.True(await reader.TestAsync());

        await reader.ExtractAllAsync(_out);
        AssertExtractedMatchesSource(_out);
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    public async Task Password_protected_archive_needs_the_right_password(CreateFormat format)
    {
        var path = Archive("secret" + CreateOptions.Extension(format));
        await ArchiveWriter.CreateAsync(path, [_source], new CreateOptions { Format = format, Password = "correct horse" });

        var locked = await ArchiveReader.OpenAsync(path);
        Assert.True(locked.HasEncryptedEntries);
        await Assert.ThrowsAsync<PasswordRequiredException>(() => locked.ExtractAllAsync(_out));

        var wrong = await ArchiveReader.OpenAsync(path, "nope");
        await Assert.ThrowsAnyAsync<ArchiveException>(() => wrong.ExtractAllAsync(Path.Combine(_work, "wrong")));
        Assert.False(File.Exists(Path.Combine(_work, "wrong", "source", "readme.txt"))
                     && File.ReadAllText(Path.Combine(_work, "wrong", "source", "readme.txt")) == "hello stratum");

        var open = await ArchiveReader.OpenAsync(path, "correct horse");
        await open.ExtractAllAsync(_out);
        AssertExtractedMatchesSource(_out);
    }

    [Fact]
    public async Task SevenZip_with_encrypted_file_names_cannot_be_listed_without_password()
    {
        var path = Archive("hidden.7z");
        await ArchiveWriter.CreateAsync(path, [_source],
            new CreateOptions { Format = CreateFormat.SevenZip, Password = "pw", EncryptFileNames = true });

        await Assert.ThrowsAsync<PasswordRequiredException>(() => ArchiveReader.OpenAsync(path));
        await Assert.ThrowsAsync<WrongPasswordException>(() => ArchiveReader.OpenAsync(path, "bad"));

        var reader = await ArchiveReader.OpenAsync(path, "pw");
        await reader.ExtractAllAsync(_out);
        AssertExtractedMatchesSource(_out);
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    public async Task Split_archive_is_written_as_volumes_and_extracts(CreateFormat format)
    {
        var path = Archive("split" + CreateOptions.Extension(format));
        var written = await ArchiveWriter.CreateAsync(path, [_source],
            new CreateOptions { Format = format, VolumeSize = 100_000 });

        Assert.True(written.Count >= 3, $"expected several volumes, got {written.Count}");
        Assert.False(File.Exists(path));
        Assert.EndsWith(".001", written[0]);
        Assert.All(written.Take(written.Count - 1), v => Assert.Equal(100_000, new FileInfo(v).Length));

        var reader = await ArchiveReader.OpenAsync(written[0]);
        Assert.Equal(written.Count, reader.VolumeCount);
        await reader.ExtractAllAsync(_out);
        AssertExtractedMatchesSource(_out);

        // Opening a later volume falls back to the first one.
        var viaSecond = await ArchiveReader.OpenAsync(written[1]);
        Assert.Equal(written[0], viaSecond.ArchivePath);
    }

    [Fact]
    public async Task Selected_entries_extract_relative_to_the_current_folder()
    {
        var path = Archive("partial.zip");
        await ArchiveWriter.CreateAsync(path, [_source], new CreateOptions());
        var reader = await ArchiveReader.OpenAsync(path);
        var tree = new ArchiveTree(reader.Entries);

        var inSource = tree.List("source");
        Assert.Equal(["docs", "noise.bin", "readme.txt"], inSource.Select(i => i.Name));
        Assert.True(inSource[0].IsDirectory);

        var docs = inSource.Where(i => i.Name == "docs");
        await reader.ExtractAsync(tree.IndexesUnder(docs), _out, stripPrefix: "source");

        Assert.True(File.Exists(Path.Combine(_out, "docs", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_out, "docs", "nested", "שלום.txt")));
        Assert.False(File.Exists(Path.Combine(_out, "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(_out, "source")));
    }

    [Fact]
    public async Task Overwrite_modes_are_respected()
    {
        var path = Archive("ow.zip");
        await ArchiveWriter.CreateAsync(path, [Path.Combine(_source, "readme.txt")], new CreateOptions());
        var reader = await ArchiveReader.OpenAsync(path);

        Directory.CreateDirectory(_out);
        var target = Path.Combine(_out, "readme.txt");
        File.WriteAllText(target, "existing");

        await reader.ExtractAllAsync(_out, OverwriteMode.Skip);
        Assert.Equal("existing", File.ReadAllText(target));

        await reader.ExtractAllAsync(_out, OverwriteMode.Rename);
        Assert.Equal("existing", File.ReadAllText(target));
        Assert.Equal("hello stratum", File.ReadAllText(Path.Combine(_out, "readme (2).txt")));

        await reader.ExtractAllAsync(_out, OverwriteMode.Overwrite);
        Assert.Equal("hello stratum", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    public async Task Add_and_delete_modify_an_existing_archive(CreateFormat format)
    {
        var path = Archive("edit" + CreateOptions.Extension(format));
        await ArchiveWriter.CreateAsync(path, [Path.Combine(_source, "readme.txt")], new CreateOptions { Format = format });

        await ArchiveWriter.AddAsync(path, [Path.Combine(_source, "docs")], "extra");
        var afterAdd = await ArchiveReader.OpenAsync(path);
        var names = afterAdd.Entries.Where(e => !e.IsDirectory).Select(e => e.NormalizedPath).Order().ToList();
        Assert.Equal(["extra/docs/a.txt", "extra/docs/nested/שלום.txt", "readme.txt"], names);

        var readme = afterAdd.Entries.Single(e => e.NormalizedPath == "readme.txt");
        await ArchiveWriter.DeleteAsync(path, [readme.Index]);
        var afterDelete = await ArchiveReader.OpenAsync(path);
        Assert.DoesNotContain(afterDelete.Entries, e => e.NormalizedPath == "readme.txt");
        Assert.Equal(2, afterDelete.Entries.Count(e => !e.IsDirectory));
        Assert.True(await afterDelete.TestAsync());
    }

    [Fact]
    public async Task Damaged_archive_fails_the_integrity_test()
    {
        var path = Archive("damaged.zip");
        await ArchiveWriter.CreateAsync(path, [Path.Combine(_source, "noise.bin")],
            new CreateOptions { Strength = CompressionStrength.Store });

        var bytes = File.ReadAllBytes(path);
        for (var i = 1000; i < 1100; i++) bytes[i] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var reader = await ArchiveReader.OpenAsync(path);
        Assert.False(await reader.TestAsync());
    }

    [Fact]
    public async Task Cancelled_creation_leaves_no_archive_behind()
    {
        var path = Archive("cancelled.7z");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveWriter.CreateAsync(path, [_source], new CreateOptions { Format = CreateFormat.SevenZip }, null, cts.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Tar_with_password_is_rejected()
    {
        await Assert.ThrowsAsync<ArchiveException>(() => ArchiveWriter.CreateAsync(Archive("x.tar"), [_source],
            new CreateOptions { Format = CreateFormat.Tar, Password = "pw" }));
    }

    [Fact]
    public async Task Non_archive_file_is_reported_as_unsupported()
    {
        var path = Archive("not-an-archive.zip");
        File.WriteAllText(path, "just some text, definitely not an archive");
        var ex = await Assert.ThrowsAnyAsync<ArchiveException>(() => ArchiveReader.OpenAsync(path));
        Assert.IsNotType<PasswordRequiredException>(ex);
    }
}
