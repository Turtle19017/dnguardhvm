# AK.43–AK.44 — kind8 direct token decoder and suffix-recipe selector

> Checkpoint after AK.43B2 → AK.44D.  This note records only evidence promotions, retractions, exact formulas, and the current blocker.  Runtime IL / tokenmap remain research oracles only; the final architecture is still host-only/offline from `LordsMobileBot.exe`.

## Status summary

```text
kind8 D1 -> S2 entry root                 CLOSED
root.count == virtual suffix occurrences CLOSED on captured non-sentinel corpus
root.item[i] <-> virtual K(4+i)          CLOSED on captured non-sentinel corpus
S2 item -> CLR token                      CLOSED for this DG 4.9.6 target/version
root/host state -> generic suffix recipe CURRENT BLOCKER
```

The token-resolver TTD path is no longer the active blocker.  The active work is reconstructing the generic kind8 suffix opcode/literal recipe offline.

---

## AK.43B2 — virtual-token bit layout corrected

Corpus:

```text
methods joined             = 4,767
live virtual occurrences   = 13,158
positional item pairs      = 13,158
```

Old low23 ordinal extraction failed on 28 methods.  Masking bit22 out closes all failures:

```text
old low23 K4.. pass = 4,739 / 4,767
low22 K4.. pass     = 4,767 / 4,767
```

`table 0A` full 2x2:

```text
                    bit22=0   bit22=1
Field                     0          31
Method                 2803           0
```

The 28 old sequence-fail methods are exactly the 28 methods containing `0A + bit22=1`.

### Promotion

On the captured non-sentinel kind8 corpus:

```text
bits31..24 = CLR virtual table byte
bit23      = virtual marker
bit22      = MemberRef field-form discriminator
bits21..0  = per-method virtual occurrence ordinal
```

`root.item[i]` corresponds positionally to virtual occurrence ordinal `K(4+i)` for all 4,767 joined methods.

Item tag dispatch observed structurally/oracle-assisted:

```text
tag0 -> TypeSpec             (support tiny; STRONG)
tag1 -> TypeRef
tag2 -> TypeDef
tag3 -> MemberRef / Field
tag4 -> FieldDef
tag5 -> MemberRef / Method
tag6 -> MethodDef
tag7 -> MethodSpec
```

---

## AK.43C / AK.43D — `(tag,data24)` is a global semantic descriptor on observed repeats

Oracle-resolved positional pairs: `9,957`.

`tag -> real CLR metadata table`:

```text
mismatches = 0 / 9,957
```

Descriptor determinism:

```text
(tag,data24) repeated groups       = 607
consistent repeated groups         = 607
ambiguous repeated groups          = 0
repeated occurrences               = 6,113
ambiguous occurrences              = 0
```

`data24` alone is not sufficient; cross-tag collisions exist.

Simple transforms were rejected on the oracle set:

```text
data24 == RID                 0 hits
data24 >> 4 == RID            0 hits
data24 ^ 0x24E41B == RID      0 hits
data24 == packed resolver     0 hits
```

The earlier weighted Spearman signal was also not robust: after deduplicating descriptor keys, tag4/tag5/tag6 correlations fall close to zero.  Do not model `data24` as a monotonic metadata-RID index.

---

## AK.43E / AK.43F — descriptor factorization

`low16` alone is NOT a semantic RID selector:

```text
(tag,low16) ambiguous keys = 845 / 2,264
low16 -> RID ambiguous     = 886 / 2,119
```

However, every observed alias of one real token preserves the same low16:

```text
real tokens with >=2 descriptors = 515
all aliases same low16            = 515 / 515
```

High8 exhaustive mask search over all 256 masks found a minimal perfect mask:

```text
mask                         = 0xC0
semantic high8 bits          = bits 7..6
canonical keys               = 3,733
unique real tokens           = 3,733
selector ambiguity           = 0
alias excess                 = 0
```

So `data24[21:16]` is an observed non-semantic alias dimension for token identity, while `data24[23:22]` carries the low two RID bits.

---

## AK.43G — direct item -> CLR token formula CLOSED on oracle corpus

A single XOR constant is observed for all 9,957 oracle-resolved items:

```text
low16 XOR (realRID >> 2) = 0x686A
```

Exact validation:

```text
slot2 == realRID & 3          = 9,957 / 9,957
low16^686A == realRID >> 2    = 9,957 / 9,957
combined predicted RID        = 9,957 / 9,957
predicted CLR table           = 9,957 / 9,957
predicted full CLR token      = 9,957 / 9,957
```

Direct RID decoder:

```c
uint32_t rid =
    (((data24 & 0xFFFFu) ^ 0x686Au) << 2)
    | ((data24 >> 22) & 3u);
```

For an actual IL patcher, the CLR table byte is already present in the virtual token, so the preferred reconstruction is:

```c
uint32_t realToken =
    (virtualToken & 0xFF000000u)
    | rid;
```

The S2 item tag remains useful as a grammar / resolver-class validator but is not required to supply the table byte to the patcher.

### Semantic item layout for this target/version

```text
31      28 27      24 23 22 21             16 15                 0
+---------+----------+-----+------------------+--------------------+
| item tag| marker A |slot2|      alias6      | encoded RID group  |
+---------+----------+-----+------------------+--------------------+

slot2             = RID & 3
encoded RID group = (RID >> 2) XOR 0x686A
alias6            = data24 bits21..16; non-semantic for observed token identity
```

The static origin/derivation of magic `0x686A` remains OPEN for cross-version genericity.

---

## Target `0x060008E1` — K4/K5/K6 solved host-only

Target S2 entry root:

```text
root offset = 0xCB994
count       = 3
signature   = 665
items data24:
  K4  tag6  0x297A30
  K5  tag6  0xE87A08
  K6  tag5  0x2B686F
```

Formula result:

```text
K4 -> RID 0x004968 -> 0x06004968
K5 -> RID 0x00498B -> 0x0600498B
K6 -> RID 0x000014 -> 0x0A000014
```

K4/K5 agree with independent Track-A oracle mappings.  K6 was missing from the target tokenmap but is produced directly by the formula.

---

## AK.44A — full host-only kind8 formula census

No `index-auto`, `il.bin`, `tokenmap.json`, runtime dump, or `HVMRun64.dll` was used for this census.

```text
S2 nodes                         = 16,851
kind8 total                      = 4,948
kind8 non-sentinel               = 4,885
kind8 sentinel                   = 63
bad non-sentinel roots           = 0
decoded non-sentinel root items  = 13,490
marker A                         = 13,490 / 13,490
RID == 0                         = 0
RID out of metadata-table range  = 0
missing table row count          = 0
```

Per tag all predicted RIDs are in range, including the previously unoracled MethodSpec class:

```text
tag0 -> TypeSpec    9 / 9
tag1 -> TypeRef    56 / 56
tag2 -> TypeDef    58 / 58
tag3 -> MemberRef 159 / 159
tag4 -> FieldDef 4527 / 4527
tag5 -> MemberRef 2914 / 2914
tag6 -> MethodDef 5718 / 5718
tag7 -> MethodSpec 49 / 49
```

`0x0A000014` is predicted 1,988 times across the host-only corpus, so the target K6 result is not an isolated accidental in-range token.

### Promotion level

- **CONFIRMED (oracle):** direct token formula exact on 9,957/9,957 resolved occurrences.
- **STRONG (host-only global census):** all 13,490 non-sentinel kind8 root items decode to nonzero in-range host metadata rows.
- **OPEN:** static derivation of `0x686A`; independent semantic identity check for every previously unoracled prediction.

---

## AK.44B / AK.44C — suffix recipe selector

Captured suffix corpus:

```text
kind8 non-sentinel   = 4,885
joined IL            = 4,767
missing IL           = 118
decode failures      = 0
positional failures  = 0
external CFG edges   = 0
unique live recipes  = 1,120
```

Almost every captured suffix contains five unreachable bytes:

```text
dead-byte histogram = {5: 4751, 0: 16}
```

### Exact root content is NOT enough

AK.44B repeated exact-root groups:

```text
repeated groups       = 126
recipe deterministic  = 68
recipe ambiguous      = 58
```

Therefore:

> **REFUTED:** exact S2 root content alone uniquely determines the reachable suffix recipe.

Adding suffix length improves but does not close the model:

```text
(root-content,length) repeated groups = 111
live-recipe deterministic              = 79 / 111
ambiguous                              = 32
opcode-skeleton deterministic          = 88 / 111
operand-shape deterministic            = 110 / 111
```

Thus the residual problem is overwhelmingly opcode/literal selection, not broad body-layout selection.

---

## Dominant target family suffix recipe CLOSED

For `signature=665, suffixLength=0x19`:

```text
methods              = 1,710
exact root variants  = 6
live recipes          = 1
opcode skeletons      = 1
```

The target's exact `(root-content,length)` group alone has:

```text
methods      = 1,705
live recipes = 1
```

Canonical reachable suffix recipe:

```text
+00 br.s -> +07
+07 call ITEM[0]  // table 06
+0C call ITEM[1]  // table 06
+11 ldarg.0
+12 call ITEM[2]  // table 0A
+17 nop
+18 ret
```

The bytes at `+02..+06` are unreachable dead junk for this family.  Byte-exact reproduction of that junk remains separate from semantic/runnable reconstruction.

This means `0x060008E1` now has enough host-side information for its suffix emitter:

```text
recipe family 665 + 0x19
+
AK.43G item-token formula
=
reachable suffix with K4/K5/K6 resolved
```

---

## AK.44D — physical forward-context differential

Residual ambiguous `(root-content,length)` groups:

```text
groups       = 32
occurrences  = 94
same opcode skeleton        = 9 / 32
same operand-shape skeleton = 31 / 32
shape genuinely varies      = 1
```

Exact forward physical context appears predictive:

```text
next 1 node: 29/32 conflict-free
next 2 nodes: 31/32 conflict-free
next 3 nodes: 32/32 conflict-free
```

But this must NOT be promoted to a decoder: cross-validation disappears as context depth grows.

```text
depth 1: groups with repeated evidence = 1, rows = 2
depth 2: repeated evidence = 0
depth 3: repeated evidence = 0
```

A single context cell splits 31/32 groups, most frequently:

```text
N1.I0.data24  -> 28 groups
N1.I0.low16   -> 26 groups
N1.I0.slot2   -> 17 groups
N1.I0.alias6  -> 16 groups
```

This is **correlation only**; many groups have one unique context value per method and therefore provide no repeated evidence of a causal grammar.

### Current blocker

The generic kind8 suffix decoder still needs the host-static source of opcode/literal variants such as:

```text
ldc.i4.0 .. ldc.i4.8
ldc.i4.s N
brtrue / brfalse
small constants used before stfld/call/array construction
```

AK.44E is the next pending probe: test MethodDef RID bits and other host-static record coordinates against the 32 residual recipe groups before returning to TTD or treating physical S2 adjacency as causal.

---

## Evidence ledger at this checkpoint

### CONFIRMED

- S2 global node grammar and de-whitening from prior AK.30/31.
- kind8 non-sentinel D1 points to S2 entry roots.
- On 4,767 captured non-sentinel methods, `root.count == number of virtual suffix operands`.
- `root.item[i] <-> K(4+i)` after correcting ordinal mask to low22.
- Virtual-token bit22 distinguishes field-shaped vs method-shaped MemberRef in the measured table-0A corpus.
- Direct item->CLR-token formula is exact on all 9,957 oracle-resolved occurrences.
- Target K4/K5 decode to `0x06004968` / `0x0600498B`.
- Dominant `665 + 0x19` suffix family has one reachable recipe on all 1,710 captured methods.

### STRONG

- Direct token formula applies to all 13,490 non-sentinel kind8 root items for this target/version: every result is nonzero and inside the expected host metadata table range.
- Target K6 is `0x0A000014`.
- `data24[21:16]` is non-semantic alias state for token identity in the observed corpus.

### REFUTED / RETRACTED

- `vtoken & 0x007FFFFF` is the occurrence ordinal — **REFUTED**; bit22 is not ordinal state.
- S2 kind8 entry-root residual slots represent locals/StandAloneSig — **REFUTED** on captured non-sentinel kind8 because root count equals exact virtual occurrence count.
- Exact root content alone determines reachable suffix recipe — **REFUTED**.
- Weighted Spearman evidence for direct/ordered `data24 -> RID` mapping — **RETRACTED** after unique-descriptor recheck.
- `next3 physical nodes -> recipe` as a proven decoder — **NOT PROMOTED**; current evidence can be explained by context uniquification.

### OPEN

- Static origin/derivation of XOR constant `0x686A` for cross-version genericity.
- Host-static source of generic suffix opcode/literal variants.
- Sentinel kind8 representation (`63` rows) remains separate; fixed `+0x14` captured-IL delta does not apply to them.
- Exact byte generator for unreachable 5-byte junk, if byte-identical MethodBody reconstruction is required.
- Prefix K1–K3 exact offline derivation / guard semantics remain separate from suffix token decoding.
- Locals type reconstruction and remaining exact MethodBody metadata remain separate pipeline work.

---

## Next experiment

Run AK.44E static selector scan over the 32 residual `(root-content,length)` groups:

1. MethodDef RID low bits / parity.
2. S2 root offset and node index low bits.
3. payload record offset / scanner numeric metadata.
4. simple host-static xor/delta combinations.

Only selectors with repeated-recipe validation should be promoted.  Raw coordinates that merely make every method unique are not causal evidence.
