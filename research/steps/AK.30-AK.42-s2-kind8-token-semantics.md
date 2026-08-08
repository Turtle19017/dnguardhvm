# AK.30–AK.42 — S2 global grammar, kind8 IL template, virtual ordinal law, token semantics

> Temporary handoff checkpoint for the next reasoning pass.  This file intentionally records **promotions, retractions, and current blockers** rather than every intermediate script/log.

## Goal / architecture constraint

Final target is still a **host-only/offline DNGuard HVM 4.9.6 rebuilder** for `LordsMobileBot.exe`:

```text
LordsMobileBot.exe only
  -> recover HVM payload / metadata / S2
  -> reconstruct MethodBody
  -> resolve virtual metadata references to CLR tokens
  -> patch managed module
```

Runtime JIT/TTD, `HVMRun64.dll`, `index-auto`, `tokenmap.json`, etc. are research oracles only.  Do not let Track-A capture become a final dependency.

---

## AK.30 — global S2 physical grammar CLOSED

Offline `s2.bin`:

```text
size            = 0x185E08 = 1,596,936 bytes
DWORDs          = 399,234
stream key      = 0x6A24E41B
nodes           = 16,851
items           = 382,383
nodes+items     = 399,234 DWORDs exactly
```

Global tiling reaches exact EOF with no gap/overlap.

For every node:

```text
count       = raw_header ^ 0x6A24E41B
node_bytes  = 4 * (1 + count)
decoded_i   = raw_item_i ^ (count + i)
```

The item de-whitening formula is runtime-confirmed from the AAB/DCB readers and closes the entire static stream.

`kind8` non-sentinel D1 pointers:

```text
4,885 / 4,885 point exactly to S2 node headers
4,885 unique roots
```

**RETRACTED / REFUTED:** generic kind0/kind3 high24 as S2 node-header pointer.  Their numeric projections mostly land on interior items.

---

## AK.31 — normalized S2 item format

For every decoded item:

```text
[type:4][marker:A:4][data24]
```

Global decoded type census:

```text
0 :   1,729
1 :   5,487
2 :   4,973
3 :   1,858
4 : 198,023
5 :  95,458
6 :  70,406
7 :   4,449
>7:       0
```

Maximum de-whitening mask is only `0x5329`, so bits 15..31 are invariant between raw and decoded items.  Therefore type nibble + `A` marker + upper data bits are physically plaintext in raw S2 for this corpus.

Known virtual / real token low24 values are absent verbatim from decoded `data24`:

```text
0x800001..0x800006
0x0088ED
0x0004C9
0x001D99
```

**REFUTED:** `data24` directly stores CLR RID, virtual token, or universal S2 offset/offset4 encoding.

---

## AK.32 — kind8 length and fixed prefix CLOSED on captured subset

Joined `4,767` kind8 methods against runtime `il.bin` (`118` missing oracle):

```text
runtime_il_size - kind8.high24 = 0x14    4767/4767
```

Thus on all captured kind8 methods:

```text
kind8.high24 = runtime suffix length
final runtime IL = fixed 0x14-byte prefix + suffix[high24]
```

The prefix is identical in all 4,767 samples:

```text
00 7F 01 00 80 04 FE 16 02 00 80 01 6F 03 00 80 0A 2D 01 00
```

Equivalent virtual operands:

```text
K1: 0x04800001
K2: 0x01800002
K3: 0x0A800003
```

---

## AK.33 — forward-neighbor “owned bundle” hypothesis REFUTED

Candidate `root + root.count following physical nodes` is **not** a globally owned object:

```text
kind8 complete candidates : 4,884
zero child-entry collision: 1,411
claimed physical nodes    : 10,079 / 16,851
overlap nodes             : 5,540
max owners / node         : 6
```

Forward context does reduce byte-output ambiguity, but increasing depth trends toward uniqueness-by-neighborhood and is not semantic proof.

For target `060008E1`, the three following physical nodes are runtime-visited context, but do not generalize ownership globally.

---

## AK.34 / AK.35 — dominant root byte diversity is mostly dead anti-analysis junk

Dominant kind8 family:

```text
length = 0x19
root count = 3
root signature = 665
root decoded =
  6A297A30
  6AE87A08
  5A2B686F
captured methods = 1,705
```

All 1,705 runtime suffixes differ only in bytes `+03..+06`:

```text
2B 05 28 ?? ?? ?? ?? 28 04 00 80 06 28 05 00 80 06 02 28 06 00 80 0A 00 2A
```

The first instruction is:

```text
2B 05   br.s -> +07
```

so bytes `[+02,+07)` are unreachable:

```text
28 <4-byte junk operand>
```

Reachable suffix for all 1,705 is identical:

```text
28 04 00 80 06
28 05 00 80 06
02
28 06 00 80 0A
00
2A
```

Global repeated-root census (`111` repeated groups / `2,042` methods):

```text
byte-exact consistent      :  1 group  /    2 methods
dead-junk normalized       : 78 groups / 1,946 methods
still live-inconsistent    : 32 groups /   94 methods
```

So `1,948/2,042` repeated-root methods are semantically/reachably identical after conservative dead-junk normalization.

**CORRECTION:** AK.32 root-only byte nondeterminism is not equivalent to semantic nondeterminism.  In the dominant root, all 1,705 differences are dead junk.

The remaining 32 groups contain real live differences (small constants, operand/branch-shape changes, etc.) and remain OPEN.

---

## AK.36–AK.38 — virtual KEY selector model REFUTED; KEY is occurrence ordinal

A real CIL instruction walker over all `4,767` captured kind8 methods closed the numbering law.

Virtual token key field:

```text
rawKey   = virtualToken & 0x007FFFFF
flag22   = rawKey & 0x00400000
ordinal  = rawKey & 0x003FFFFF
```

After stripping bit22:

```text
prefix K1,K2,K3              : 4767/4767
full K1..KN ordinal sequence : 4767/4767
first live suffix ordinal K4 : 4767/4767
failures                     : 0
```

Therefore:

```text
CONFIRMED:
virtual KEY is a 1-based metadata-operand occurrence ordinal in emitted IL order.
It is NOT an opaque S2-selected index.
```

Bit22 census:

```text
flagged operands : 31
flagged methods  : 28
table 0x0A       : 31/31
field opcodes    : 31/31  (ldfld / ldsfld)
method opcodes   : 0/31
```

Current wording:

```text
CONFIRMED association:
  bit22 is orthogonal to ordinal and marks observed table-0A field-use refs.
STRONG / UNPROVEN exact name:
  MemberRef-field discriminator/modifier.
```

This removes the old blocker “how does S2 choose KEY4/K5/K6?”.  It does not: ordinals are lexical occurrence IDs.

---

## AK.39 — exact token oracle vs S2 root

Per-method `tokenmap.json` exact coverage among kind8 samples:

```text
K4  4363/4767
K5  3103/3383
K6   930/2981
K7   685/857
K8   399/550
...
```

Important correlation result, repeated node fingerprints only:

```text
root node fingerprint -> exact real token

K4: 2042 repeated methods, det=2042, amb=0
K5: 1845 repeated methods, det=1845, amb=0
K6:  106 repeated methods, det=106,  amb=0
K7:   33 repeated methods, det=33,   amb=0
K8:   10 repeated methods, det=10,   amb=0
```

Nearby `node+1..+5` fingerprints are mostly ambiguous.  Thus exact **root semantic recipe**, not a specific physical child node, is the strongest predictor on repeated samples.

Direct representations inside `root + next4 nodes` had zero hits for every mapped K4..K13:

```text
real RID     : 0
packed24     : 0
ordinal      : 0
bootstrap idx: 0
```

So S2 does not trivially embed these resolved values.

---

## AK.40 — dynamic-token oracle closes semantic recipe for dominant root

Indexer separates:

```text
tokenmap.json         -> concrete `real` token
dynamic-tokenmap.json -> semantic identity/spec evidence without concrete real
```

Coverage examples:

```text
K1: 0 exact,    4767 dynamic, 0 missing
K2: 0 exact,       0 dynamic, 4767 missing
K3: 0 exact,    4767 dynamic, 0 missing
K4: 4363 exact,  395 dynamic, 9 missing
K5: 3103 exact,  257 dynamic, 23 missing
K6: 930 exact,  2032 dynamic, 19 missing
```

Repeated exact-root -> dynamic evidence is deterministic in all measured repeated groups:

```text
K4 repeatedDynamic=42   det=42   amb=0
K5 repeatedDynamic=20   det=20   amb=0
K6 repeatedDynamic=1709 det=1709 amb=0
K7 repeatedDynamic=2    det=2    amb=0
K8 repeatedDynamic=5    det=5    amb=0
```

### Dominant root 665 semantic recipe

For all `1,705` methods:

```text
K4 EXACT   -> 0x06004968                    one real token
K5 EXACT   -> 0x0600498B                    one real token
K6 DYNAMIC -> method System.Object::.ctor   one semantic identity
```

Therefore the dominant root is a **fixed semantic recipe**, not a wrapper whose K6 identity varies by method.

Target `060008E1`:

```text
K1 dynamic : field ZYXDNGuarder::a
K2 missing : no Track-A identity evidence
K3 dynamic : System.Object::GetHashCode
K4 exact   : 0x06004968
K5 exact   : 0x0600498B
K6 dynamic : System.Object::.ctor
```

Independent runtime ground truth already established earlier:

```text
K1 0x04800001 -> 0x040088ED
K2 0x01800002 -> 0x010004C9
K3 0x0A800003 -> 0x0A001D99
```

Do not equate dynamic identity `metadataToken` with a host-local token.  Example: `System.Object::GetHashCode` identity carries external `0x06000650`, but runtime target mapping is host-local MemberRef `0x0A001D99`.

---

## AK.41 / AK.42 — Track-A crosswalk bridge is not the final resolver

A global exact corpus contains:

```text
15,929 tokenmap files
266,912 exact entries
```

AK.42 tiered bridge stats:

```text
virtual keys             : 21,784   unique=9,510   ambiguous=12,274
meta semantic keys       : 23,161   unique=23,038  ambiguous=123
owner+name semantic keys : 33,042   unique=30,321  ambiguous=2,721
```

But coverage improvement is tiny:

```text
K1: 0/4767 resolved
K2: 0/4767 resolved
K3: 0/4767 resolved
K4: 4364/4767 (only +1 bridge)
K5: 3105/3383 (only +2)
K6:  931/2981 (only +1)
K7:  691/857  (only +6)
```

Dominant root remains:

```text
K4 exact 0x06004968
K5 exact 0x0600498B
K6 semantic System.Object::.ctor but global virtual value is ambiguous
```

Global virtual value is definitely **not** a global semantic key. Examples:

```text
0x06800004 -> 2101 different real MethodDefs in corpus
0x0A800006 -> 584 different real MemberRefs in corpus
```

This is exactly consistent with AK.38: low K is method-local occurrence numbering.

### RETRACTION / next architecture pivot

Do **not** keep expanding Track-A identity/virtual crosswalk heuristics as the final route.

Preferred final resolver architecture now is:

```text
S2 root
  -> semantic IL/reference recipe
  -> semantic identity/type/signature
  -> scan CLR metadata directly in LordsMobileBot.exe
  -> module-local CLR token
```

---

## Target `0x060008E1` current canonical chain

```text
MethodDef 0x060008E1
  -> kind8 record
  -> suffixLen = 0x19
  -> S2 root = 0xCB994
  -> decoded root sig 665:
       6A297A30
       6AE87A08
       5A2B686F

fixed 0x14 prefix:
  00
  7F 01 00 80 04       K1
  FE 16 02 00 80 01    K2
  6F 03 00 80 0A       K3
  2D 01
  00

suffix (runtime oracle):
  2B 05
  28 7D B9 39 5F       dead junk call, skipped by branch
  28 04 00 80 06       K4
  28 05 00 80 06       K5
  02
  28 06 00 80 0A       K6
  00
  2A
```

Token/reference state:

```text
K1 -> runtime real 0x040088ED; semantic field ZYXDNGuarder::a
K2 -> runtime real 0x010004C9; Track-A semantic identity missing
K3 -> runtime real 0x0A001D99; semantic System.Object::GetHashCode
K4 -> exact real 0x06004968
K5 -> exact real 0x0600498B
K6 -> semantic System.Object::.ctor; host-local real token still open
```

Reachable suffix after skipping dead call:

```text
28 04 00 80 06
28 05 00 80 06
02
28 06 00 80 0A
00
2A
```

---

## Immediate next step for Opus: AK.43 host metadata resolver probe

Do **not** continue broad S2 forward-depth census or TTD handler tracing yet.

Probe `LordsMobileBot.exe` CLR metadata directly (e.g. `System.Reflection.Metadata`) and validate these positive controls:

1. Decode `0x040088ED` as FieldDef and verify `ZYXDNGuarder::a`.
2. Decode `0x010004C9` as TypeRef and record the exact type.
3. Decode `0x0A001D99` as MemberRef and verify `System.Object::GetHashCode` + signature.
4. Decode `0x06004968`, `0x0600498B` as MethodDefs.
5. Search host metadata semantically for `ZYXDNGuarder::a`, `System.Object::GetHashCode`, `System.Object::.ctor` and count exact candidates.
6. Compare K1 field type with K2 TypeRef; this may derive K2 from prefix type relation.
7. If `System.Object::.ctor` has a unique host-local MemberRef/signature candidate, use it as target K6 candidate and validate with the rebuilt method/JIT semantics.

If semantic lookup is ambiguous, add **signature blob + parent scope/type** before any fuzzy naming heuristic.  For generic TypeSpec/MethodSpec cases, preserve spec blobs; do not collapse to owner+name only.

---

## Current high-level status

### CONFIRMED / CLOSED

- S2 exact global sequential grammar and EOF tiling.
- Node count decode and item de-whitening.
- kind8 non-sentinel D1 -> exact S2 root header.
- kind8 high24 -> runtime suffix length on 4,767 captured methods.
- one fixed 0x14 prefix on those 4,767 methods.
- virtual occurrence numbering K1..KN after bit22 strip: 4,767/4,767.
- bit22 observed only on table-0A field-use refs in current corpus.
- dominant root 665 reachable IL skeleton and K4/K5/K6 semantic recipe fixed across 1,705 methods.

### STRONG

- exact decoded S2 root determines semantic IL/reference recipe on repeated oracle evidence.
- bit22 is a MemberRef-field discriminator/modifier.
- final token resolver should resolve S2-derived semantic identity/signature directly against host CLR metadata.

### OPEN

- exact semantics of S2 item types/data24.
- source of live immediate constants in 32 minority repeated-root groups.
- host-only derivation of semantic identities/specs from S2 items (rather than Track-A oracle).
- host-local resolution for dynamic/corelib MemberRefs (target K6 first).
- K2 semantic/type derivation.
- S1 exact local types.
- kind0 body transform/semantics.
- EH count conflict (`1638` record-derived vs `1613` old `eh_table.csv`) remains separate.

### REFUTED / RETRACTED

- S2 has a missing 128-DWORD gap.
- kind8 uses same XOR+RC4 body transform as kind3.
- `root + root.count following nodes` is a globally owned object.
- exact-root byte diversity implies semantic diversity (dominant case was dead junk).
- virtual KEY is an opaque selector chosen from S2.
- virtual token value is a global semantic key.
- decoded `data24` directly stores known virtual/real token values or universal S2 offsets.
- 12-DWORD bootstrap packed pool is a global token resolver for the corpus.
