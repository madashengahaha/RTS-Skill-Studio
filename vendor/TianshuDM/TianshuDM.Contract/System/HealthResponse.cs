namespace TianshuDM.Contract.System;

public sealed record HealthResponse(
    string Status,
    string ApiVersion,
    string ServiceVersion,
    DateTimeOffset TimestampUtc);
