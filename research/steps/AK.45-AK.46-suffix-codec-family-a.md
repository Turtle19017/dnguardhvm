# AK.45–AK.46 — suffix codec family A and corpus census

> Checkpoint after the AK.45 dynamic provenance pass and AK.46 offline structural census. Runtime TTD / captured `il.bin` are research oracles only. The final architecture remains host-only/offline from `LordsMobileBot.exe`.

## Status summary

```text
host pl_full -> raw suffix block                  CLOSED for 0x06004968
Stage-1 repeating-key XOR                         CLOSED for 0x06004968
Stage-2 64-bit-pair transform                     CLOSED for family A
0x06004968 exact aligned output                    32 / 32 bytes
0x06004968 exact meaningful suffix                 29 / 29 bytes
family-A exact full-suffix corpus PASS             858 methods
round-count-only hypothesis                        REFUTED
small structural-variant hypothesis                REFUTED
family-B / alternate preprocessing classification CURRENT BLOCKER
```

AK.44 recipe-selection work is no longer the active path for exact suffix reconstruction. The payload channel itself contains the opcode/literal/dead-junk bytes; S2 remains the virtual-token descriptor channel.

---

## AK.45A–D — host bridge and Stage-1

Live method used as the primary runtime oracle:

```text
MethodDef       = 0x06004968
final IL size   = 0x31
fixed prefix    = 0x14 bytes
suffix length   = 0x1D
aligned payload = 0x20 bytes
```

Exact final suffix captured at JIT:

```text
2B 05 28 9B 1C 4B 65 28
04 00 80 0A 39 0B 00 00
00 28 05 00 80 06 73 06
00 80 0A 7A 2A
```

The corresponding raw encoded 32-byte block occurs in host `pl_full.bin` at:

```text
payload offset = 0x345B38
```

Raw bytes:

```text
EE 17 AB 85 C3 6C 25 8E
DD 06 71 55 64 71 EC E3
ED C4 8B 0D 50 20 DF 47
FB C4 A3 E2 0C BF 3D FA
```

Runtime provenance independently showed the same offset inside the full `pl_full` bulk copy. Therefore the scanner offset for `0x06004968` is correct even though its parsed size field is not.

### Stage-1

Stage-1 is a repeating XOR with the same 16-byte key already recovered for metadata decryption:

```text
5F AA 95 01 A4 61 BC 81 05 8E 63 52 2B C6 69 7A
```

Equation:

```c
stage1[i] = raw[i] ^ key16[i & 15];
```

For `0x06004968` this produces:

```text
B1 BD 3E 84 67 0D 99 0F
D8 88 12 07 4F B7 85 99
B2 6E 1E 0C F4 41 63 C6
FE 4A C0 B0 27 79 54 80
```

This matched the runtime Stage-1 buffer exactly, 32/32 bytes.

---

## AK.45 Stage-2 — family A transform

Stage-2 processes independent 64-bit pairs. Each pair is two little-endian `uint32_t` lanes `(v0,v1)` and uses two rounds.

Key DWORD table, little-endian from the 16-byte key:

```text
K[0] = 0x0195AA5F
K[1] = 0x81BC61A4
K[2] = 0x52638E05
K[3] = 0x7A69C62B
```

State constants:

```text
DELTA       = 0x61398397
initial sum = 0xC273072E = 2 * DELTA mod 2^32
rounds      = 2
```

Mixer:

```c
uint32_t mx(uint32_t x, uint32_t sum, uint32_t key)
{
    uint32_t a =
        (((x << 4) ^ (x >> 3)) +
         ((x >> 5) ^ (x << 2)));

    uint32_t b =
        (key ^ x) +
        (sum ^ x);

    return a ^ b;
}
```

All arithmetic is modulo `2^32`.

Pair decoder:

```c
uint32_t sum = 0xC273072E;

for (int round = 0; round < 2; ++round) {
    uint32_t e = (sum >> 2) & 3;

    v1 -= mx(v0, sum, K[e ^ 1]);
    v0 -= mx(v1, sum, K[e]);

    sum -= 0x61398397;
}
```

This is a custom TEA-family-like 4/3/5/2 mixer. It is **not** standard XXTEA: the observed constant is not the standard TEA/XXTEA delta/subtraction equivalent, and the measured dependency structure is the pair transform above.

### Dynamic traversal evidence

For pair `B718/B71C`:

```text
F4E : update B71C round 1
F7D : update B718 round 1
FBC : update B71C round 2
```

At `F7D`:

```text
B718 before = 071288D8
mask        = 199D5BD5
B718 after  = ED752D03
```

The resulting `ED752D03` is exactly the input used by the following B71C round-2 mixer.

For pair `B720/B724`, writer ordering is:

```text
103A : B724 round 1
1069 : B720 round 1
10A8 : B724 round 2
10D7 : B720 round 2
```

This confirms the per-round lane order:

```text
update v1 using old/current v0
update v0 using updated v1
```

### Key-selector evidence

For B720 round 1:

```text
sum = C273072E
(sum >> 2) & 3 = 3
K[3] loaded dynamically
```

For B720 round 2:

```text
sum = 61398397
(sum >> 2) & 3 = 1
K[1] loaded dynamically
```

For the lane-1 update, the observed selector is `e ^ 1`; for lane-0 it is `e`.

---

## Exact offline test vector — `0x06004968`

Stage-1 DWORD pairs:

```text
pair 0: 843EBDB1 0F990D67
pair 1: 071288D8 9985B74F
pair 2: 0C1E6EB2 C66341F4
pair 3: B0C04AFE 80547927
```

After Stage-2:

```text
pair 0: 9B28052B 28654B1C
pair 1: 0A800004 00000B39
pair 2: 00052800 06730680
pair 3: 7A0A8000 0000002A
```

Aligned output:

```text
2B 05 28 9B 1C 4B 65 28
04 00 80 0A 39 0B 00 00
00 28 05 00 80 06 73 06
00 80 0A 7A 2A 00 00 00
```

Trim to meaningful length `0x1D`:

```text
2B 05 28 9B 1C 4B 65 28
04 00 80 0A 39 0B 00 00
00 28 05 00 80 06 73 06
00 80 0A 7A 2A
```

Offline validation reproduced the aligned block 32/32 bytes and the meaningful suffix 29/29 bytes exactly.

This also reconstructs the unreachable five-byte junk directly from the payload channel. Therefore an opcode/literal/junk recipe selector is not required for byte-exact suffix recovery for this family.

---

## Architectural consequence for AK.44

The old active model was:

```text
S2 root/context -> choose suffix opcode/literal recipe
```

AK.45 shows a different exact path:

```text
pl_full payload
  -> Stage-1 XOR
  -> Stage-2 pair decoder
  -> exact virtual-tokenized suffix bytes
```

S2 then supplies the positional token-descriptor mapping already solved by AK.43G:

```text
decoded virtual-token operand
  + positional S2 root item
  -> real CLR metadata token
```

Therefore:

- AK.44 recipe-selection experiments remain useful historical evidence, but are **SUPERSEDED** as the active exact-suffix architecture.
- Dead junk, branch bytes, literals, and opcode bytes are carried by the encoded payload itself for family A.
- S2 does not need to synthesize those bytes.

---

## Scanner correction exposed by `0x06004968`

Scanner row for `0x06004968` reports:

```text
payload offset = 0x345B38
parsed size    = 0x80000
pl_full size   = 0x346C10
```

Runtime and host matching prove `0x345B38` is correct. The parsed size is wrong for this representation.

Do **not** disable the bounds check or hard-code `0x20`; the size/length bitfield still needs to be decoded correctly from host metadata.

---

## AK.45 corpus validation

Using rows for which the existing scanner supplied usable `payload_offset`, `meaningful_size`, and `aligned_size`, and comparing against captured fixed-prefix-family suffixes:

```text
rows             = 27,296
usable           = 10,522
missing_il       = 342
non_kind8        = 277
size_mismatch    = 4,767
bad_range        = 0
bad_alignment    = 0
exact PASS       = 858
FAIL             = 9,664
```

The 858 PASS methods span multiple suffix sizes and payload regions, so family A is a real reusable codec family rather than a one-method overfit.

The sampled failures differ from expected output at byte zero, which argues against a tail-padding/token-patching-only issue.

### Round census

Trying family-A structure with round counts `1..32` on the first 8-byte pair:

```text
checked          = 10,522
rounds=2         = 858
no round 1..32   = 9,664
```

No alternate round count matched any additional method.

The `size_mismatch` delta histogram is heterogeneous rather than a single `+/-0x14` correction, so the numerical equality between `4,767` size mismatches and the prior `4,767` joined kind8 corpus must not be promoted to a parser rule.

---

## AK.46 — structural variant census

The 9,664 failures were tested under a bounded family of nearby variants:

```text
rounds        = 1..8
lane order    = 10 or 01
lane0 key xor = 0..3
lane1 key xor = 0..3
```

Result:

```text
rows checked : 10,522

match multiplicity:
  0 matches : 9,664
  1 match   :   858

only nonzero variant:
  rounds = 2
  order  = 10
  c0     = 0
  c1     = 1
  count  = 858
```

### Promotions

**CONFIRMED:** the 858 exact-PASS methods belong to one unique measured structural codec family A.

**REFUTED:** the remaining 9,664 methods are explained by only changing round count, lane-update order, or the simple `e ^ c` lane key selectors while keeping the same Stage-1/key/delta/mixer.

**UNPROVEN:** the 9,664 methods necessarily use a different Stage-2 codec. A scanner payload-offset/preprocessing split is still possible and should be ruled out offline before tracing a second family dynamically.

---

## Evidence ledger at this checkpoint

### CONFIRMED

- `0x06004968` host raw block is `pl_full + 0x345B38`.
- Stage-1 for the primary oracle is repeating XOR with the recovered global key16.
- Family-A Stage-2 pair equation, update order, two-round state schedule, and key selectors reproduce the complete aligned block exactly.
- `0x06004968` offline output is 32/32 aligned bytes and 29/29 meaningful suffix bytes exact against the JIT oracle.
- Family A yields exact full-suffix PASS on 858 corpus methods.
- AK.46 finds exactly one matching structural variant for those same 858 methods.

### STRONG

- The 16-byte key is reused globally between metadata decryption and at least suffix family A.
- Exact suffix reconstruction should be modeled as a payload codec followed by S2 virtual-token translation, rather than S2 opcode-recipe synthesis.

### REFUTED / RETRACTED / SUPERSEDED

- Standard XXTEA — **REFUTED**.
- Whole-array round-1 then whole-array round-2 traversal — **REFUTED**.
- Per-word complete-two-round processing — **RETRACTED**; the measured unit is a 64-bit pair.
- Family A with only a different round count explains all usable methods — **REFUTED**.
- Nearby changes to round/order/simple key-index xor constants explain the 9,664 failures — **REFUTED**.
- AK.44 generic opcode/literal recipe selection as the active exact-suffix blocker — **SUPERSEDED** for family A by direct payload decoding.

### OPEN

- Determine whether the 9,664 failures are true alternate codec/preprocessing families or incorrect scanner payload locations.
- Decode the host representation of payload meaningful/aligned lengths, including the `0x06004968` `0x80000` size bug.
- Static derivation/location of the global 16-byte key from `LordsMobileBot.exe`.
- Cross-family Stage-1 / Stage-2 selector if multiple codecs are confirmed.
- Prefix K1–K3 exact offline derivation / guard semantics.
- Locals type reconstruction from S0/S1 and remaining exact MethodBody metadata/EH discrepancies.

---

## Next experiment — AK.47

Before opening a second TTD target, invert family A on the expected first decoded pair:

```text
expected decoded pair
  -> inverse Stage-2
  -> required Stage-1 pair
  -> XOR key16
  -> required raw 8-byte pair
```

Search that required raw pair across all of `pl_full`.

Interpretation:

```text
required raw found elsewhere with stable deltas
  -> scanner payload-offset family likely wrong

required raw absent globally for most failures
  -> strong evidence for alternate preprocessing/key/codec family
```

If alternate family evidence survives AK.47, use a small failing oracle such as `0x06000054` and measure only the first discriminators: Stage-1 key/preprocessing, Stage-2 key table, DELTA/initial sum, and mixer shape.
