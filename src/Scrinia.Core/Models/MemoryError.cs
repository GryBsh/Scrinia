namespace Scrinia.Core.Models;

/// <summary>Stable categories returned by memory-facing operations.</summary>
public enum MemoryErrorCategory
{
    MalformedAddress,
    UnsupportedInputForm,
    ForbiddenAddressKind,
    InvalidWriteContent
}

/// <summary>A structured error suitable for translation by an adapter.</summary>
public sealed record MemoryError(
    MemoryErrorCategory Category,
    string Code,
    string Message,
    string? Field = null,
    long? Limit = null,
    long? Actual = null);

/// <summary>A structured failure produced while parsing a logical memory address.</summary>
public sealed record MemoryAddressError(
    MemoryErrorCategory Category,
    string Code,
    string Message,
    string? Field = null,
    long? Limit = null,
    long? Actual = null)
{
    /// <summary>Creates a malformed-address error without echoing unsafe input.</summary>
    public static MemoryAddressError Create(string message, long? limit = null, long? actual = null) =>
        new(MemoryErrorCategory.MalformedAddress, "malformed_address", message, "address", limit, actual);
}

/// <summary>Thrown by <see cref="MemoryAddress.Parse(string)"/> for malformed input.</summary>
public sealed class MemoryAddressException : ArgumentException
{
    /// <summary>Initializes an exception for a structured address error.</summary>
    public MemoryAddressException(MemoryAddressError error)
        : base(error.Message, error.Field)
    {
        Error = error;
    }

    /// <summary>The categorized parsing failure.</summary>
    public MemoryAddressError Error { get; }

    /// <summary>The stable category of the parsing failure.</summary>
    public MemoryErrorCategory Category => Error.Category;
}
