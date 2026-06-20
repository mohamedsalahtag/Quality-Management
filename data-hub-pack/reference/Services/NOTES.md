# Adapter notes — PivotService + filter shape

The `PivotService.cs` and `PerspectiveService.cs` files in this folder are
the **live Sharbatly QMS implementation**, copied verbatim. They reference
`FlatDefectFilter` directly inside the static helper `AppendFilterWhere`.

When you wire this pack into a new project, you must do two small edits
so the seam goes through `IDataHubFilterAdapter` instead:

## Edit 1 — `PivotRequest.cs`

Change the `Filter` property type from your host's filter type to `object?`:

```csharp
public object? Filter { get; set; }
```

This decouples the request shape from any single host's filter class.

## Edit 2 — `PivotService.cs`

Replace the constructor + `AppendFilterWhere` calls so the service uses the
injected adapter:

```csharp
private readonly IDataHubFilterAdapter _filterAdapter;

public PivotService(IConfiguration config, ILogger<PivotService> log,
                    IMemoryCache cache, IDataHubFilterAdapter filterAdapter)
{
    _cs            = config.GetConnectionString("Default")!;
    _log           = log;
    _cache         = cache;
    _filterAdapter = filterAdapter;
}
```

Then inside `RunAsync` and `GetDistinctValuesAsync`, replace:

```csharp
AppendFilterWhere(sb, p, request.Filter ?? new FlatDefectFilter());
```

with:

```csharp
_filterAdapter.AppendFilterWhere(sb, p, request.Filter);
```

and delete the private static `AppendFilterWhere(StringBuilder, DynamicParameters, FlatDefectFilter)` method entirely.

## Edit 3 — `PerspectiveService.cs`

No edits required. The perspective CRUD does not touch the host filter.

## Why ship the live code at all?

So you can diff future improvements made on the Sharbatly QMS side against
what's in your project. The pack's reference code is the *spec by example*
of how the live system behaves end-to-end.
