using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.State;
using Velocity.Core.State;
using Velocity.TestSupport;

namespace Velocity.Core.Tests;

/// <summary>
/// The state accessor is where the transaction system's guarantees are enforced rather than
/// assumed, so each guarantee has a test.
/// </summary>
public sealed class TransactionalStateAccessorTests
{
    private static readonly StateKey Declared = new("memory", "path", "Declared");
    private static readonly StateKey Undeclared = new("memory", "path", "Undeclared");

    [Fact]
    public async Task Write_IsAllowedForADeclaredKey()
    {
        (TransactionalStateAccessor accessor, InMemoryStateProvider provider) = Create();

        await accessor.WriteAsync(Declared, StateValue.FromUInt32(1), CancellationToken.None);

        Assert.Equal(StateValue.FromUInt32(1), await provider.ReadAsync(Declared, CancellationToken.None));
        Assert.Single(accessor.WrittenKeys);
    }

    [Fact]
    public async Task Write_IsRefusedForAnUndeclaredKey()
    {
        (TransactionalStateAccessor accessor, InMemoryStateProvider provider) = Create();

        UndeclaredStateWriteException exception =
            await Assert.ThrowsAsync<UndeclaredStateWriteException>(
                () => accessor.WriteAsync(Undeclared, StateValue.FromUInt32(1), CancellationToken.None));

        Assert.Equal(Undeclared, exception.Key);
        Assert.Equal(0, provider.WriteCount);
    }

    [Fact]
    public async Task Read_IsAllowedForAnyKey()
    {
        (TransactionalStateAccessor accessor, InMemoryStateProvider provider) = Create();
        provider.Seed(Undeclared, StateValue.FromString("visible"));

        StateValue value = await accessor.ReadAsync(Undeclared, CancellationToken.None);

        // Detection has to be able to describe the machine, including settings it will not change.
        Assert.Equal("visible", value.Data);
    }

    [Fact]
    public async Task Write_FailsLoudlyWhenElevationIsRequiredAndUnavailable()
    {
        var provider = new InMemoryStateProvider();
        provider.RequireElevation(Declared);

        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(PrivilegeChannel.None),
            "test.tweak",
            new[] { Declared });

        await Assert.ThrowsAsync<PrivilegeRequiredException>(
            () => accessor.WriteAsync(Declared, StateValue.FromUInt32(1), CancellationToken.None));

        // Silently doing nothing and reporting success is the failure mode this prevents.
        Assert.Equal(0, provider.WriteCount);
    }

    [Fact]
    public async Task Write_IsRefusedByAReadOnlyProvider()
    {
        var provider = new InMemoryStateProvider(canWrite: false);
        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            "test.tweak",
            new[] { Declared });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => accessor.WriteAsync(Declared, StateValue.FromUInt32(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReadMany_ReturnsEveryRequestedKey()
    {
        (TransactionalStateAccessor accessor, InMemoryStateProvider provider) = Create();
        provider.Seed(Declared, StateValue.FromUInt32(5));

        IReadOnlyDictionary<StateKey, StateValue> values =
            await accessor.ReadManyAsync(new[] { Declared, Undeclared }, CancellationToken.None);

        Assert.Equal(2, values.Count);
        Assert.True(values[Undeclared].IsAbsent);
    }

    [Fact]
    public void Resolve_FailsClearlyForAnUnknownScheme()
    {
        var registry = new StateProviderRegistry(new IStateProvider[] { new InMemoryStateProvider() });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => registry.Resolve(new StateKey("nonexistent", "path", "item")));

        Assert.Contains("nonexistent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsTwoProvidersForOneScheme()
    {
        Assert.Throws<ArgumentException>(() => new StateProviderRegistry(new IStateProvider[]
        {
            new InMemoryStateProvider("registry"),
            new InMemoryStateProvider("registry"),
        }));
    }

    private static (TransactionalStateAccessor Accessor, InMemoryStateProvider Provider) Create()
    {
        var provider = new InMemoryStateProvider();
        var accessor = new TransactionalStateAccessor(
            new StateProviderRegistry(new IStateProvider[] { provider }),
            new FakePrivilegeContext(),
            "test.tweak",
            new[] { Declared });

        return (accessor, provider);
    }
}

/// <summary>Round trip tests for the canonical state key form used in the journal.</summary>
public sealed class StateKeyTests
{
    [Fact]
    public void ToString_ProducesTheCanonicalForm()
    {
        var key = new StateKey("registry", @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");

        Assert.Equal(
            @"registry://HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl#Win32PrioritySeparation",
            key.ToString());
    }

    [Fact]
    public void Parse_RoundTripsAKeyWithAnItem()
    {
        var key = new StateKey("registry", @"HKLM\SOFTWARE\Test", "Value");

        Assert.Equal(key, StateKey.Parse(key.ToString()));
    }

    [Fact]
    public void Parse_RoundTripsAKeyWithoutAnItem()
    {
        var key = new StateKey("service", "SysMain", null);

        Assert.Equal(key, StateKey.Parse(key.ToString()));
    }

    [Fact]
    public void Parse_RejectsTextWithoutAScheme()
    {
        Assert.Throws<FormatException>(() => StateKey.Parse(@"HKLM\SOFTWARE\Test#Value"));
    }

    [Fact]
    public void AbsentValue_IsDistinguishableFromAnEmptyString()
    {
        Assert.True(StateValue.Absent.IsAbsent);
        Assert.False(StateValue.FromString(string.Empty).IsAbsent);
        Assert.Equal("(not set)", StateValue.Absent.ToDisplayString());
    }
}
