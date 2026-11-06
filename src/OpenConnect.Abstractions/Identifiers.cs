namespace OpenConnect.Abstractions;

/// <summary>A 128-bit, time-sortable identifier using the ULID Crockford Base32 representation.</summary>
public readonly record struct Ulid : IComparable<Ulid>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly object GenerationLock = new();
    private static UInt128 _lastGenerated;
    private readonly UInt128 _value;

    private Ulid(UInt128 value) => _value = value;

    /// <summary>Creates a lexicographically monotonic ULID within this process.</summary>
    public static Ulid NewUlid()
    {
        lock (GenerationLock)
        {
            var milliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var previousTime = (ulong)(_lastGenerated >> 80);
            if (milliseconds <= previousTime && _lastGenerated != 0)
                _lastGenerated++;
            else
            {
                Span<byte> random = stackalloc byte[10];
                System.Security.Cryptography.RandomNumberGenerator.Fill(random);
                UInt128 randomness = 0;
                foreach (var b in random) randomness = (randomness << 8) | b;
                _lastGenerated = ((UInt128)milliseconds << 80) | randomness;
            }
            return new Ulid(_lastGenerated);
        }
    }

    /// <summary>Parses a 26-character Crockford Base32 ULID.</summary>
    public static Ulid Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 26) throw new FormatException("A ULID must contain exactly 26 characters.");
        UInt128 parsed = 0;
        foreach (var character in value)
        {
            var digit = Decode(character);
            if (parsed > (UInt128.MaxValue >> 5)) throw new FormatException("The ULID value is out of range.");
            parsed = (parsed << 5) | (uint)digit;
        }
        // The first Base32 digit has only three significant bits.
        if (Decode(value[0]) > 7) throw new FormatException("The ULID value is out of range.");
        return new Ulid(parsed);
    }

    /// <summary>Compares identifiers by their unsigned 128-bit value.</summary>
    public int CompareTo(Ulid other) => _value.CompareTo(other._value);

    /// <summary>Returns the canonical 26-character uppercase Crockford Base32 representation.</summary>
    public override string ToString()
    {
        Span<char> result = stackalloc char[26];
        var value = _value;
        for (var i = result.Length - 1; i >= 0; i--)
        {
            result[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return new string(result);
    }

    private static int Decode(char character)
    {
        character = char.ToUpperInvariant(character);
        // Crockford's common O/I/L aliases are accepted on input.
        if (character is 'O') character = '0';
        if (character is 'I' or 'L') character = '1';
        var index = Alphabet.IndexOf(character);
        if (index < 0) throw new FormatException($"'{character}' is not a Crockford Base32 character.");
        return index;
    }
}

public readonly record struct TenantId(Ulid Value);
public readonly record struct PrincipalId(Ulid Value);
public readonly record struct SessionId(Ulid Value);
public readonly record struct TaskId(Ulid Value);
public readonly record struct AgentId(string Value);
public readonly record struct ExecutionId(Ulid Value);
public readonly record struct CapabilityId(string Value);
public readonly record struct ToolId(string Value);
