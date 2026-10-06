Phase 5’i **implementation olarak başarılı**, ama **CLOSED/PASS olarak henüz kabul etmiyorum**. Router yönü doğru ve Win32/UIA ayrımı ciddi kazanç üretmiş. Özellikle `28.5 ms → 1.16 ms` window metadata ve UIA double-traversal’ın kaldırılması gerçek optimizasyon.

Fakat raporda dört kapanış sorunu var:

- `SetValue` başarısızsa otomatik `TypeText` fallback’i **semantik olarak eşdeğer değil**. `SetValue` replace semantics ister; typing caret/selection durumuna bağlıdır.
- “element read 14.1 ms → 0.01 ms cached handle instant read” ifadesi şüpheli. **Element identity cache** ile **live property/value cache** aynı şey değildir. 0.01 ms sadece element resolution ise “read” diye raporlanmamalı.
- “Her mutation `world.bump()` ile plan cache’i invalidate ediyor” denirken aynı raporda “repeated plan steps 100% cache hit” deniyor. Bunun hangi step dizisinde geçerli olduğu açıklanmamış.
- Phase 5 kriteri **en az bir gerçek workflow’un end-to-end hızlanmasını** istiyordu. Şu an ölçülenlerin çoğu mikro-operasyon.

Bunları düzeltip kapatalım.

[ANTIGRAVITY_TASK]
PHASE 5 CLOSURE REPAIR — SEMANTIC FIDELITY + CACHE CORRECTNESS + END-TO-END PROOF

Do NOT add new backends.

Do NOT change IPC, CLI, Skill, broker architecture, or coordinate system.

Do NOT optimize anything unrelated.

Goal:
Prove that the new router is not merely fast, but semantically correct and that its cache does not return stale dynamic UI state.

---

## 1 — SEPARATE ELEMENT IDENTITY FROM ELEMENT STATE

Audit:

```text
PlanContext
plan_elements
ElementRef
UIA read paths
```

The plan-local cache may safely cache:

```text
resolved element identity / live COM element reference
HWND association
selector → element mapping
```

subject to validity rules.

It must NOT silently treat dynamic properties such as:

```text
Value
Name when mutable
ToggleState
Selection state
Enabled state
text content
```

as permanently current merely because the element identity is cached.

Required conceptual separation:

```text
ElementIdentityCache
!=
DynamicPropertyCache
```

If dynamic properties are cached at all, they require explicit freshness/version semantics.

---

## 2 — AUDIT THE “0.01 ms READ” CLAIM

Previous report says:

```text
Notepad element read:
14.1 ms → 0.01 ms
(cached handle instant read)
```

Determine exactly what the 0.01 ms operation measured.

If it measured only:

```text
selector → cached ElementRef lookup
```

rename the metric:

```text
element resolution cache hit
```

Do NOT call it:

```text
element read
```

A real live value read must obtain current state from the provider unless an explicit valid current-value snapshot exists.

Add separate timings:

```text
element_identity_resolution_us
live_value_read_ms
```

---

## 3 — LIVE READ CORRECTNESS TEST

Create a deterministic test with fake UIA provider or controlled backend:

1. Resolve element.
2. Cache identity.
3. Read value = `"A"`.
4. Mutate provider externally to `"B"` WITHOUT replacing the element identity.
5. Read again.

Expected:

```text
"B"
```

not cached `"A"`.

Test:

```text
Plan_CachedElementIdentity_DoesNotCacheStaleDynamicValue
```

This is mandatory.

---

## 4 — RECONCILE WORLD VERSION / CACHE HIT SEMANTICS

Current report states:

```text
cache key:
(Hwnd, Selector) -> (ElementRef, world_version)
```

and:

```text
any mutation → world.bump()
→ cached reference invalid
```

But also claims:

```text
100% cache hit on repeated plan steps
```

Explain the exact behavior.

Instrument a sample Plan:

```text
Find document
Read document
Read document
SetValue document
Read document
```

Report for each step:

```text
cache hit/miss
world version before/after
whether UIA tree traversal occurred
```

Expected conservative behavior:

```text
Find            miss
Read            hit identity
Read            hit identity
SetValue        hit identity if still valid
post-mutation Read:
    either validated reuse
    or deliberate re-resolution
```

Do not report impossible 100% hit rates across invalidating mutations.

---

## 5 — CONSIDER STRUCTURAL VS NON-STRUCTURAL INVALIDATION

Audit whether:

```text
SetValue
Toggle
Invoke
Select
```

all currently trigger the same global structural invalidation.

Do NOT change this merely for performance.

But classify what currently happens.

If a text value change causes:

```text
world.bump()
→ invalidate every cached element in entire plan
```

report whether this is required for correctness or merely conservative.

If safely possible using existing state model, distinguish:

```text
state mutation
```

from:

```text
UI structure mutation
```

so element identity is not discarded unnecessarily.

However:

Correctness > reuse.

If uncertain, preserve conservative invalidation.

---

## 6 — FIX SETVALUE SEMANTICS

This is mandatory.

A requested operation:

```text
SetValue(target, "abc")
```

means replacement semantics.

If `ValuePattern.SetValue` is unavailable, DO NOT silently execute generic:

```text
TypeText("abc")
```

at arbitrary caret position.

Allowed fallback only if runtime can establish equivalent semantics.

For example:

```text
focus target
select all existing text
type replacement
verify final value == requested value
```

only if:
- physical fallback is permitted,
- control supports reliable text replacement behavior,
- target/foreground guard remains valid.

Otherwise return:

```text
SemanticOperationUnavailable
```

or closest existing structured error.

Do not corrupt content to preserve apparent success.

---

## 7 — DISTINGUISH TEXT ACTIONS

Ensure protocol/runtime preserves distinct semantics:

```text
SetValue
    final content must equal supplied value

TypeText
    keyboard-like insertion at current caret/selection

AppendText
    append semantics if supported/defined
```

Do not merge these internally merely because they all contain strings.

Add tests:

```text
SetValue_DoesNotFallbackToArbitraryCaretTyping
TypeText_PreservesInsertionSemantics
SetValue_PhysicalFallbackReplacesContentOrFails
```

If `AppendText` does not currently exist, do not add it solely for this task; just ensure current operations remain semantically distinct.

---

## 8 — SEMANTIC CLICK ROUTER MATRIX

Audit generic click behavior.

Do not reduce all semantic clicking to:

```text
InvokePattern else physical
```

Use control/action semantics where available.

Preferred mappings include:

```text
Button/MenuItem
→ InvokePattern

Checkbox/Toggle
→ TogglePattern when appropriate

Selectable list/tree item
→ SelectionItemPattern when the requested intent is selection

Edit value mutation
→ ValuePattern
```

Physical click remains final fallback.

Add tests covering at least:

```text
Click_Button_PrefersInvoke
Select_Item_PrefersSelectionItem
Toggle_Control_PrefersTogglePattern
SemanticPatternUnavailable_PhysicalFallbackOnlyWhenPermitted
```

Do not infer a toggle intent from every raw coordinate click.

Router follows requested action semantics.

---

## 9 — NOTEPAD 2-SECOND CLAIM

Report says:

```text
before:
Value mutation + readback = 2079 ms

after:
inline verification = 2.45 ms
```

but also:

```text
remaining bottleneck:
Windows 11 Notepad SetValue provider takes ~2s when out of focus
```

Reconcile this.

Measure separately:

```text
Notepad focused SetValue
Notepad unfocused SetValue
inline CurrentValue read
fresh independent live read
```

Minimum 10 warm samples each where safe.

Report median/p95.

Do not mix:

```text
provider mutation latency
read-lane timeout
focus transition
```

into one number.

---

## 10 — VERIFY INLINE READBACK IS REAL

After:

```text
ValuePattern.SetValue("X")
```

the current code reportedly reads:

```text
pattern.CurrentValue()
```

on the same apartment thread.

Good optimization, but verify that this represents committed provider state.

Test:

```text
SetValue_InlineVerificationMatchesFreshIndependentRead
```

Procedure:

```text
SetValue
→ inline CurrentValue
→ separately reacquire/read live value
```

Expected equality.

If providers can be eventually consistent, define retry/wait semantics explicitly rather than assuming immediate commit.

---

## 11 — REAL WORKFLOW BENCHMARK

Phase 5 requires at least one actual workflow improvement.

Run before/after equivalent warm workflows.

At minimum:

### Notepad workflow

```text
find document
set exact value
fresh readback verification
```

### Calculator workflow

```text
resolve controls
perform known expression
verify display
```

### Window workflow

```text
find target top-level window
read title/process/bounds
```

Run:

```text
10 baseline-compatible samples
10 optimized samples
```

If old build is no longer conveniently runnable, use a retained baseline binary/commit.

If no valid baseline exists, state that and compare the old instrumentation path only if it can be reproduced without changing semantics.

Report:

```text
median
p95
```

---

## 12 — END-TO-END METRIC REQUIRED

Do not pass Phase 5 based only on individual operation timings.

Need at least one:

```text
complete workflow median before
complete workflow median after
```

with measurable improvement and equivalent correctness.

---

## 13 — BACKEND CALL COUNTS MUST BE MEASURED

For each benchmark report actual counts:

```text
Win32 calls
UIA FindAll / tree traversals
UIA property calls
UIA pattern acquisitions
CDP requests
physical broker requests
```

Do not report approximate percentage reductions unless instrumentation recorded actual counts.

If “52% UIA reduction” was inferred, either provide numerator/denominator or retract exact percentage.

---

## 14 — BROWSER ROUTING SAFETY

Preserve:

```text
browser content → CDP
browser chrome → UIA/Win32
```

Add one negative regression:

```text
BrowserContent_NoMatchingCdpTarget_DoesNotFallbackToDifferentBrowserInstance
```

Expected:

structured failure.

Never attach to another browser merely to complete the task.

---

## 15 — PLAN CACHE FAILURE SAFETY

Test:

```text
cached element identity exists
element becomes stale/destroyed
next action attempts reuse
```

Expected:

```text
detect stale
invalidate only relevant entry
fresh resolution
```

If fresh resolution fails:

```text
TargetNotFound / TargetGone
```

No stale coordinate fallback.

Add:

```text
Plan_StaleCachedElement_ReResolvesOrFailsSafely
```

---

## 16 — PERFORMANCE CLAIM HYGIENE

Allowed claims must distinguish:

```text
element resolution
live state read
semantic mutation
full workflow
```

Do not say:

```text
read = 0.01 ms
```

if only identity resolution was cached.

Do not say:

```text
SetValue = 2.45 ms
```

if that excludes provider commit or independent verification.

---

## 17 — TARGETED TESTS

Run:

```bash
cargo fmt --all -- --check
cargo check --workspace
cargo test -p inbrisk-runtime
cargo test -p inbrisk-uia
```

and affected broker/core tests only if their code changed.

No unrelated full regression unless central execution semantics change.

---

## 18 — REQUIRED RESULT FORMAT

Return:

```text
[ANTIGRAVITY_RESULT]

## CACHE SEMANTICS
identity cached:
dynamic properties cached:
freshness model:

## PREVIOUS 0.01 MS CLAIM
what it actually measured:
correct metric name:
live read latency:

## PLAN CACHE TRACE
Step 1:
Step 2:
Step 3:
Step 4:
Step 5:

## WORLD VERSION / INVALIDATION
structural mutations:
non-structural mutations:
cache behavior:

## TEXT SEMANTICS
SetValue:
TypeText:
fallback behavior:
verification:

## SEMANTIC ACTION ROUTING
Invoke:
Toggle:
SelectionItem:
Value:
Physical fallback:

## NOTEPAD PROVIDER RECONCILIATION
focused:
unfocused:
inline read:
fresh read:

## END-TO-END BENCHMARKS
Notepad before/after:
Calculator before/after:
Window workflow before/after:

## BACKEND CALL COUNTS
UIA traversal before/after:
property calls:
pattern acquisitions:
physical requests:

## BROWSER SAFETY

## TESTS

## REGRESSIONS

## VALID PERFORMANCE CLAIMS

## RETRACTED / CORRECTED CLAIMS

## REMAINING ISSUES

## PHASE 5 FINAL CLOSURE
PASS / FAIL
```

Phase 5 may return PASS only if:

1. Cached identity cannot produce stale live property/value reads.
2. `SetValue` semantics are not silently degraded into arbitrary caret typing.
3. Semantic action mappings are control/action appropriate.
4. Cache hit claims are reconciled with world-version invalidation.
5. The Notepad 2-second vs 2.45-ms discrepancy is explained.
6. Inline readback agrees with a fresh independent read.
7. At least one complete real workflow shows measured end-to-end improvement.
8. UIA call-count reductions are backed by actual counters.
9. Browser content cannot cross browser-instance boundaries.
10. Stale cached elements fail/re-resolve safely.

If PASS, end exactly:

`PHASE 5 CLOSED — ROUTER FAST PATHS VERIFIED SEMANTICALLY AND END-TO-END`

Do not start another phase until Lead Architect review.

[/ANTIGRAVITY_TASK]

Şu an en önemli kontrol bu: **hız kazanırken semantiği bozmadığımızdan emin olmak**. Özellikle `SetValue → TypeText` ve `cached handle → live read` ayrımı düzelmeden bu optimizasyonu production-grade saymak istemem.