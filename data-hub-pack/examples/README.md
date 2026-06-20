# examples/

Concrete domain instantiations of the data-hub-pack seam. Each file shows how a host project wires its filter shape + view + registry entry + adapter into one cohesive analyzer mount.

- [`fruit-quality-defects-data-source.cs`](fruit-quality-defects-data-source.cs) — the canonical Sharbatly QMS instantiation (`vw_qms_flat_defects`, `FlatDefectFilter`, the `flat_defects` registry entry, the filter adapter). Distilled from the live code with comments calling out the seam.

## Writing your own example

When you wire the pack into a new project, you don't need to add a file here — but doing so makes the next AI hand-off easier. Each example should contain, in one file (200-400 lines is the sweet spot):

1. The host's `*Filter` POCO (one property per WHERE slot).
2. The host's `IDataHubFilterAdapter` implementation (parameterised — never concatenate).
3. The `PivotReport` registration the host adds to `PivotRegistry`.
4. The SQL of the view the registry points at.

Keep code minimal — strip down to the seam, not the full controller / Razor / JS. Those don't change between domains.
