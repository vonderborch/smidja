using Afl.Core.Exceptions;
using Afl.Core.Storage;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the JSON store bases: round-trips, atomic writes, locking and schema versions.
/// </summary>
public class StorageTests : AbstractAflTest
{
    [Fact]
    public void Store_RoundTripsAndStampsCurrentVersion()
    {
        var store = new SampleStore(Path.Combine(this.Root, "sample.json"));

        store.Update(d => d.Title = "Hello");

        Assert.Equal("Hello", new SampleStore(store.FilePath).Load().Title);
        Assert.Contains("\"schemaVersion\": \"2.0\"", File.ReadAllText(store.FilePath));
        Assert.Empty(Directory.GetFiles(this.Root, "*.tmp"));
    }

    [Fact]
    public void Store_RefusesNewerAndMalformedDocuments()
    {
        var path = Path.Combine(this.Root, "sample.json");
        var store = new SampleStore(path);

        File.WriteAllText(path, """{ "schemaVersion": "3.0" }""");
        Assert.Contains("newer", Assert.Throws<AflException>(() => store.Load()).Message);

        File.WriteAllText(path, "{ nope");
        Assert.Equal(AflErrorKind.Storage, Assert.Throws<AflException>(() => store.Load()).Kind);
    }

    /// <summary>
    ///     A document at schema 2.0.
    /// </summary>
    [DocumentVersion(2, 0)]
    public class SampleDocument : AbstractVersionedDocument
    {
        /// <summary>
        ///     Gets or sets the title.
        /// </summary>
        public string? Title { get; set; }
    }

    /// <summary>
    ///     A store of <see cref="SampleDocument" />.
    /// </summary>
    /// <param name="path">The file path.</param>
    public class SampleStore(string path) : AbstractJsonStore<SampleDocument>(path);
}
