using Xunit;

namespace RatScanner.Tests;

/// <summary>
/// Serializes every test class that mutates <see cref="RatConfig"/> static state
/// (e.g. <c>RatConfig.Tracking.TarkovTracker</c> tokens, sources, flags) so xUnit
/// cannot run them in parallel and race on the shared singletons.
/// </summary>
// CA1711: xUnit convention names collection-definition classes "*Collection".
[CollectionDefinition(Name)]
#pragma warning disable CA1711
public sealed class RatConfigCollection
#pragma warning restore CA1711
{
    public const string Name = "RatConfig static state";
}
