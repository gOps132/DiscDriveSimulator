# Roll Value as a Hypergeometric Sample

## The problem

Dismantling a disc needs exactly two facts: its **partition**, and its **Roll Value (RV)**.
Nothing else about the disc affects the keep/dismantle decision.

Building a disc to learn those two facts is wasteful. `DiscGenerator.GenerateDisc` does, per disc:

| Step | Cost |
| --- | --- |
| `Guid.NewGuid()` | 128-bit entropy read |
| `SubstatPool.Where(...).ToList()` | LINQ enumerator + list allocation |
| Fisher-Yates over 9-10 elements | ~9 swaps |
| `pool.Take(k).ToList()` | another list |
| `new List<SubstatRoll>()` + 3-4 rolls | 4-5 allocations |

At 1M runs (~2.5M drops) that is ~12M allocations on the Blazor WASM UI thread. The fix is to
learn RV without building the disc — and RV turns out to have a closed form.

## Step 1: RV collapses to a count

RV sums `SubstatRoll.RollHits` over the substats present in the partition's prioritized set
(`Disc.GetRollValue`). Every roll is created with exactly one hit
(`DiscGenerator` → `new SubstatRoll(stat, val, 1)`), and nothing in the simulation path
increments it. So:

> **RV = the number of drawn substats that are prioritized.**

RV is not a weighted sum over values. It is a count over *which stats were picked*.

## Step 2: the draws are without replacement

`GenerateDisc` builds the pool as `SubstatPool` minus the main stat, shuffles it, and takes
the first `k` entries. A shuffle-and-take-prefix draws `k` **distinct** pool elements, each
equally likely — the standard sampling-without-replacement model.

## Step 3: therefore hypergeometric

With pool size `n` after main-stat exclusion, `d` prioritized stats surviving in that pool,
and `k` substats drawn:

```
            d      n - d       d    n - d      C(n, k)
     C( , x) · C(     , k-x)   --------  · -----------------
P(X = x) = ---------------------  =  C(n, x)  C(n, k)      C(n, k)
            n      n
```

`X ~ Hypergeometric(n, d, k)`. It depends only on the **counts** `(n, d, k)` — never on which
stats the priority list names, nor on which set was rolled.

## The subtlety: two more sources of randomness

`BuildTableFor(partition, mainStat, prioritized)` is keyed on the main stat, because of two
things the naive version gets wrong. Both were caught by the tests, not by inspection.

**1. The substat count is itself random.** `GenerateDisc` does
`_random.Next(2) == 0 ? 3 : 4`, so the true RV distribution is a **mixture**:

```
0.5 · Hypergeom(n, d, 3) + 0.5 · Hypergeom(n, d, 4)
```

Assuming a fixed `k = 3` overstates `P(RV = 0)` by about 33% relative (0.238 vs the true
0.179), which keeps noticeably too many bad discs.

**2. The main stat changes the pool, but only sometimes.** A prioritized stat that becomes
the main stat is removed from the pool, dropping `d` by one — *if it was in the pool at all*.
Slots 5 and 6 offer main stats that are not substat-pool members (PenRatio, AnomalyMastery,
Impact, EnergyRegen, and the five elemental damage stats), so for those the pool keeps its
full size. Missing this shifts `P(RV = 0)` by ~0.035 on slot 5.

```
PoolSizeAfter(mainStat)       = SubstatPool.Length - (mainStat ∈ SubstatPool ? 1 : 0)
PrioritizedLeftAfter(main, p)  = p.Count - (p.Contains(main) ? 1 : 0)
```

## Worked example: slot 4, default priorities

Priorities are CritRate, CritDmg, ATKPercent. Slot 4 main stats are HPPercent, ATKPercent,
DEFPercent, CritRate, CritDmg, AnomalyProficiency — 3 of which are prioritized.

For each main stat, build the `k ∈ {3,4}` mixture, then mix the six cases with weight 1/6 each.
Testing against 120k real generated discs matches within 0.01 on every RV value.

## Which priorities the tables use

Tables are built from `SimulationState.GetRunPriorities(partition)`, not the raw per-slot
lists. When a target partition is set, those coincide — only that slot can survive the
partition check anyway. When the target is All, every slot is scored against the shared
All-slots list (key 0, the one the console editor shows). Scoring per-slot lists there
silently ignored the player's configuration: with 4 edited priorities but 3 per slot,
max RV is 3, so Min RV 4 kept 0 out of 25M drops.

## Sampling

`ToCumulative` turns a PMF into a CDF, forcing the last entry to exactly `1.0` so cumulative
rounding can never leave a uniform draw of `1.0` falling off the end. `Sample` is inverse
transform sampling: one uniform double plus a binary search over ≤5 entries.

## Committing to a sampled RV

Sampling RV and *then* drawing substats independently would be wrong — the built disc's real
RV could differ from the sampled one, so kept discs would not match the decision that kept
them. Instead `GenerateDiscWithRollValue` draws substats **conditioned** on the sampled RV:

1. Split the pool into prioritized / non-prioritized.
2. Take `rv` distinct prioritized stats and `k - rv` from the rest.
3. Interleave uniformly at random.

This is uniform over every subset having that exact RV, so the disc's marginal distribution is
identical to ordinary generation — the disc is indistinguishable from one that happened to
roll that way. `BuildTableFor` and the generator draw from the same `(n, d, k)`, so an
unachievable RV throws rather than silently producing a wrong disc.

## Numerical exactness

Tables are ratios of binomial coefficients in `double`. Exact at the sizes this game uses
(`n ≤ 10`, `k ≤ 4`, so `C(10,4) = 210`). `BuildTable` throws above `MaxExactPoolSize` (20) or
`MaxExactDrawCount` (8): a silently-wrong distribution in a simulator is worse than a crash.

## Draw order matters

The fast path draws, per drop: partition → main stat → substat count `k` → RV. Each step is
what the *next* step's table depends on.

- **Main stat before RV**, because a prioritized stat that becomes the main stat leaves the
  pool, dropping `d`.
- **`k` before RV**, sampling from the `k`-specific table rather than the mixture.

Sampling RV from the mixture and then drawing `k` independently leaves the **RV marginal
correct** while corrupting the composition of low-substat discs: `P(RV = x | k = 3)` would be
the mixture instead of `Hypergeom(n, d, 3)`. A marginal-only test passes and the bug ships,
so `RunSimulationAsync_RollValueIsCorrectPerSubstatCount` asserts the joint distribution with
a tolerance that scales with cell size.

## What this does not fix

- **Average CV** (`SimulationRunResult.AverageCv`) still depends on real substat values. It is
  averaged over kept discs only, which are fully materialized, so it stays correct — but it is
  an average of survivors, not of all drops.
- **RNG stream.** Results differ from pre-fast-path runs. Distributions are identical; exact
  reproducibility across versions is not.
- **When everything is kept**, e.g. RV threshold 1 with no partition target, there is nothing
  to skip and the fast path saves little. The cap (`SimulationConfig.MaxInventoryDiscs`) is what
  bounds memory in that case.

## Migration note

`SimulationState.AutoDismantleOnSim` is kept as a serialized property but its setter is a
no-op: it always reads `true`. Saves written before this change with `false` deserialize
without error and are silently upgraded. That is intended — the fast path only pays when
auto-dismantle is on, so it is an invariant rather than a preference.

## Files

| File | Role |
| --- | --- |
| `Services/HypergeometricSampler.cs` | Closed-form tables, CDF, sampling |
| `Services/SimulationService.cs` | Fast-path decision loop, `BuildFastPathSampler` |
| `Services/DiscGenerator.cs` | `GenerateDiscWithRollValue`, conditional substat draws |
| `tests/DiscDriveSimulator.Tests/HypergeometricSamplerTests.cs` | Formula vs brute force vs real generation |
| `tests/DiscDriveSimulator.Tests/SimulationFastPathTests.cs` | End-to-end fast-path contract |