using System.Text.Json;

namespace TfReview.Core.Tests;

public class CanonicalJsonTests
{
    [Test]
    public async Task Sorts_keys_at_every_depth_by_ordinal()
    {
        var node = CanonicalJson.ParseStrict("""{"b":1,"a":{"z":1,"Z":2},"é":3}""");

        await Assert.That(CanonicalJson.Write(node)).IsEqualTo("""{"a":{"Z":2,"z":1},"b":1,"é":3}""");
    }

    [Test]
    public async Task Keeps_array_order()
    {
        var node = CanonicalJson.ParseStrict("""[3,1,2]""");

        await Assert.That(CanonicalJson.Write(node)).IsEqualTo("[3,1,2]");
    }

    [Test]
    public async Task Keeps_numbers_exactly_as_written()
    {
        var node = CanonicalJson.ParseStrict("""[12345678901234567890,1.10,1e2]""");

        await Assert.That(CanonicalJson.Write(node)).IsEqualTo("[12345678901234567890,1.10,1e2]");
    }

    [Test]
    public async Task Is_stable_when_written_twice()
    {
        var first = CanonicalJson.Write(CanonicalJson.ParseStrict("""{"b":[{"d":1,"c":null}],"a":"x"}"""));

        await Assert.That(CanonicalJson.Write(CanonicalJson.ParseStrict(first))).IsEqualTo(first);
    }

    [Test]
    [Arguments("""{"a":1,"a":2}""")]
    [Arguments("""{"x":{"a":1,"a":2}}""")]
    [Arguments("""[{"a":1,"a":2}]""")]
    public async Task Refuses_repeated_keys(string json)
    {
        await Assert.That(() => CanonicalJson.ParseStrict(json)).Throws<JsonException>();
    }

    [Test]
    public async Task Allows_the_same_key_in_different_objects()
    {
        await Assert.That(CanonicalJson.ParseStrict("""[{"a":1},{"a":2}]""")).IsNotNull();
    }
}
