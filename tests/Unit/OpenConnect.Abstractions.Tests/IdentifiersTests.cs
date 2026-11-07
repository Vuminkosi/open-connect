using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class IdentifiersTests
{
    [Fact]
    public void Generated_value_uses_canonical_26_character_format()
    {
        var value = Ulid.NewUlid().ToString();

        Assert.Equal(26, value.Length);
        Assert.All(value, character => Assert.Contains(character, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
    }

    [Fact]
    public void Parse_and_format_round_trip()
    {
        const string value = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

        Assert.Equal(value, Ulid.Parse(value).ToString());
    }

    [Fact]
    public void Generated_values_are_monotonic()
    {
        var values = Enumerable.Range(0, 100).Select(_ => Ulid.NewUlid()).ToArray();

        for (var index = 1; index < values.Length; index++)
            Assert.True(values[index - 1].CompareTo(values[index]) < 0);
    }

    [Fact]
    public void String_order_matches_identifier_order()
    {
        var earlier = Ulid.Parse("00000000000000000000000001");
        var later = Ulid.Parse("00000000000000000000000002");

        Assert.True(earlier.CompareTo(later) < 0);
        Assert.True(string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA!")]
    [InlineData("Z1ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public void Parse_rejects_malformed_or_out_of_range_values(string value) => Assert.Throws<FormatException>(() => Ulid.Parse(value));

    [Fact]
    public void Parse_accepts_lowercase()
    {
        const string value = "01arz3ndektsv4rrffq69g5fav";

        Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAV", Ulid.Parse(value).ToString());
    }
}
