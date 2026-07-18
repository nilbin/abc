using Tam;

namespace Erp;

// Shared semantic value types (labels live in locales/, never here — docs/21).


[Format("email")]
public readonly record struct EmailAddress(string Value);


[Format("phone")]
public readonly record struct PhoneNumber(string Value);


public readonly record struct Address(string Value);
