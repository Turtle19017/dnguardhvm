# AK.25–AK.29 — Payload-family routing, S0/S2 identity, runtime bridge, and first kind8 node decoder

Mẫu: `LordsMobileBot.exe`  
Ngày checkpoint: 2026-08-07  
Nhánh nghiên cứu: `ak21-items-not-tokens`  
Tiếp nối trực tiếp các checkpoint AK.21/AK.22 và các probe AK.23/AK.24.

> [!IMPORTANT]
> Checkpoint này gom toàn bộ bước AK.25 → AK.29 thành một mốc canonical. Mục tiêu là tránh giữ các kết luận rời rạc hoặc parser-family cũ như thể chúng còn đúng toàn cục.
>
> Trạng thái sử dụng trong tài liệu:
> - `CONFIRMED`: đã có exact structural/runtime evidence phù hợp.
> - `STRONG`: nhiều evidence độc lập phù hợp nhưng còn thiếu generalization/semantic closure.
> - `UNPROVEN`: hypothesis hữu ích nhưng chưa đủ dữ liệu.
> - `RETRACTED`: giả thuyết trước đã bị dữ liệu mới bác bỏ hoặc thay thế.
> - `REFUTED`: phép đo trực tiếp cho kết quả ngược với giả thuyết.

---

## 1. Bối cảnh trước AK.25

Đến trước checkpoint này đã biết:

```text
host MethodDef RID
  -> Stage0 lookup
  -> directory entry
  -> payload recordOffset
```

và hai blob offline đã được tái tạo exact:

```text
pl_full.bin  size 0x346C10
md_full.bin  size 0x1E06E8
```

Metadata physical split:

```text
S0 [0x000000, 0x057C08)   10,960 exact records
S1 [0x057C08, 0x05A8E0)   signature/type arena
S2 [0x05A8E0, 0x1E06E8)   0x185E08 bytes / 399,234 DWORDs
```

S0 record layer đã đóng trước đó:

```text
u8  field0/maxStackCandidate
u24 codeSize
u16 itemDataSize
u16 ehCount
u16 itemCount
u16 ehDataSize
items[]
ehData[]
```

với:

```text
itemDataSize = 4 * itemCount
```

Nhưng trước AK.25 vẫn chưa có một identity join đúng giữa method payload records và S0/S2 families.

---

# AK.25 — Method-resolved payload → S0 join

## 2. Scanner dataset

Full method scanner đi qua:

```text
MethodDef rows        : 27,296
parser-completed rows : 15,908
failed rows           : 11,388
```

`parser-completed` chỉ có nghĩa current parser chain chạy hết; không đồng nghĩa semantic validity.

CSV canonical fields:

```text
status
 token
 rid
 encoded_value
 directory_rva
 directory_host_offset
 directory_tag
 directory_flags
 record_offset
 metadata_ref_tag
 metadata_offset
 metadata_tag
 meaningful_size
 aligned_size
 payload_offset
 il_size
 il_sha256
 il_hex
 error
```

Không có `record_header` column. Điều này quan trọng vì AK.26 v1 từng dựa vào column không tồn tại và cho kết quả vacuous.

## 3. AK.25 v2 census

Join current scanner output với exact S0 record starts cho kết quả:

```text
S0 rows                         : 10,960
scan rows                       : 27,296
status=extracted                : 15,908
exact S0 record-offset hits     : 11,031
misses                          : 4,877
size mismatches on S0 hits      : 0
field0 mismatches on S0 hits    : 0
unique S0 records referenced    : 10,960 / 10,960
meaningful_size > max S0 size   : 2,220
```

### Methodological correction

`sizeOK` và `field0OK` không phải hai evidence độc lập nếu candidate offset đã rơi vào exact S0 record, vì scanner đang đọc lại cùng S0 DWORD mà S0 parser đã dùng.

Do đó:

```text
RETRACTED as independent proof:
  field0 equality from this join alone proves runtime maxStack semantics.
```

Structural equality vẫn hữu ích để chứng minh parser đang trỏ đúng S0 record, nhưng không được double-count evidence.

## 4. Known controls

### 0x060015E2

```text
payload record : 0x1F01F8
metadataOffset : 0x3AA50
S0 row         : #7065
field0         : 2
codeSize       : 0x0E
nLocals        : 1
EH             : 0
```

Exact S0 hit: PASS.

### 0x06002CD4

```text
payload record : 0x2DE950
metadataOffset : 0x485AC
field0         : 2
codeSize       : 0x1A
nLocals        : 1
EH             : 0
```

Exact S0 hit: PASS.

### 0x060008E1

Old parser produced:

```text
recordOffset   : 0x1CFF40
raw header     : 08 19 00 00 94 B9 0C 00
metadataRefTag : 0x08
metadataOffset : 0x19
meaningful     : 0x30100
```

`0x19` is not an S0 record start and the huge `meaningful_size` is a family-parse failure, not a real method body size.

Verdict:

```text
CONFIRMED:
  0x060008E1 does not use the old kind3/S0 parser grammar.
```

## 5. AK.25 duplicate clue

AK.25 showed two S0 starts referenced repeatedly:

```text
S0 recOff 0x1C : count 45
S0 recOff 0x30 : count 28
```

Total excess exact hits:

```text
11,031 - 10,960 = 71
```

At AK.25 this was only an anomaly. AK.26 later explains it exactly as accidental kind8 high24 collisions with two small S0 offsets.

---

# AK.25C — ref08 transform hypothesis rejected

## 6. Target hypothesis

For `0x060008E1`:

```text
D0 = 0x00001908
low8  = 0x08
high24 = 0x19
D1 = 0x000CB994
```

Candidate interpretation:

```text
kind8
len   = 0x19
s2off = 0xCB994
```

A first hypothesis reused kind3's XOR + HVM-RC4 body transform starting at `record+8`.

Expected known suffix:

```text
2B 05 28 7D B9 39 5F 28 04 00 80 06
28 05 00 80 06 02 28 06 00 80 0A 00 2A
```

Result:

```text
suffix exact : FAIL
padding zero : FAIL
```

Verdict:

```text
RETRACTED:
  kind8 body at record+8 uses the same XOR+RC4 transform as kind3.
```

The failure does not invalidate the structural `len/s2off` interpretation; AK.26 tests that independently over every method-resolved kind8 record.

---

# AK.26 — Payload-family census and exact S0 partition

## 7. AK.26 v1 invalidation

AK.26 v1 reported every raw header missing because it expected a nonexistent scanner CSV `record_header` field.

Verdict:

```text
INVALID EXPERIMENT / SCRIPT BUG
```

No conclusion from AK.26 v1 is retained.

## 8. AK.26 v2 raw-header census

Directly reading `pl_full.bin` at each scanner `record_offset` gives:

```text
scan rows                         : 27,296
rows with record_offset           : 15,908
payload bytes                     : 3,435,536 / 0x346C10
S2 bytes                          : 1,596,936 / 0x185E08
raw-header OOB                    : 0
scanner refTag != raw kind        : 0
```

Raw kind distribution:

```text
kind 0x00 :   661
kind 0x03 : 10,299
kind 0x08 :  4,948
-----------------
total     : 15,908
```

## 9. kind0 / kind3 → exact S0 identity

### kind3

```text
rows            : 10,299
exact S0 hits   : 10,299 / 10,299
unique S0 rows  : 10,299
duplicates      : 0
```

### kind0

```text
rows            : 661
exact S0 hits   : 661 / 661
unique S0 rows  : 661
duplicates      : 0
```

### union

```text
kind0 unique S0          :   661
kind3 unique S0          : 10,299
union                    : 10,960 / 10,960
overlap(kind0, kind3)    : 0
```

Therefore:

```text
CONFIRMED structural partition:

kind0/3 payload record
  D0.low8   = kind
  D0.high24 = exact S0 record offset
```

This closes MethodDef → S0 identity for every S0-backed method-resolved payload record in this sample.

Important limitation:

```text
UNPROVEN:
  kind0 uses the same payload-body crypto/semantics as kind3.
```

Only routing/identity is closed here.

## 10. kind8 grammar

All 4,948 method-resolved kind8 records satisfy:

```text
D0.low8   = 0x08
D0.high24 = len candidate
D1        = s2off or 0xFFFFFFFF sentinel
```

Census:

```text
kind8 rows          : 4,948
valid S2/sentinel   : 4,948 / 4,948
invalid             : 0
sentinel FFFFFFFF   : 63
non-sentinel        : 4,885
len min             : 0x07
len max             : 0x3F
```

Most common lengths include:

```text
0x19 : 1802
0x0E :  977
0x10 :  257
0x1B :  155
0x21 :  132
0x16 :  130
```

Target:

```text
060008E1
recordOffset = 0x1CFF40
D0           = 0x00001908
len          = 0x19
D1/s2off     = 0xCB994
```

Verdict:

```text
CONFIRMED structurally for method-resolved kind8 records:
  [08][u24 value][u32 s2off/sentinel]

STRONG for 060008E1:
  u24 value is related to its 0x19-byte runtime suffix.

UNPROVEN globally:
  u24 value is always final suffix length for every kind8 method.
```

## 11. AK.25 duplicate anomaly explained

For kind8 only:

```text
rows with D0.high24 accidentally equal an S0 start : 71
unique accidental S0 rows                           : 2
```

This exactly equals AK.25's excess:

```text
11,031 - 10,960 = 71
```

Therefore:

```text
CONFIRMED:
  the 71 AK.25 excess S0 hits are accidental kind8 high24 collisions.

RULE:
  never apply high24 -> S0 routing to kind8.
```

Canonical payload dispatch after AK.26:

```text
MethodDef RID
  -> host lookup
  -> payload record
     -> kind0/3 -> D0.high24 -> S0
     -> kind8   -> D0.high24 value + D1 s2off -> S2
```

---

# AK.27 — S2 pointer invariants

## 12. Physical S2 DWORD format

S2 contains:

```text
399,234 DWORDs
```

Every DWORD satisfies:

```text
(word >> 24) & 0xF == 0xA
```

Observed high-nibble tags:

```text
0 :   1,729
1 :   5,487
2 :   4,973
3 :   1,858
4 : 198,023
5 :  95,458
6 :  87,257
7 :   4,449
```

Physical split used in analysis:

```text
tag    = (word >> 28) & 0xF
marker = (word >> 24) & 0xF   // always A in raw S2
data24 = word & 0xFFFFFF
```

Semantic meaning of tags remains open at this stage.

## 13. kind8 pointers

```text
kind8 total        : 4,948
non-sentinel       : 4,885
sentinel           : 63
unique s2off       : 4,885
duplicate s2off    : 0
```

Most important invariant:

```text
first tag at every non-sentinel kind8 s2off = 6
4885 / 4885
```

Verdict:

```text
CONFIRMED observed invariant:
  every non-sentinel kind8 entry points to a tag6 DWORD.

STRONG:
  tag6 at s2off is an S2 logical object/node opener or canonical entry marker.
```

## 14. S2 span hypotheses rejected

Sorted next-pointer deltas do not match simple lengths:

```text
match len            : 17
match align8(len)    : 215
match 4*len          : 23
match 4*align8(len)  : 26
```

out of 4,885 pointers.

Verdict:

```text
REFUTED:
  nextS2Pointer-currentS2Pointer is a simple contiguous record size derived from kind8 len.
```

## 15. Target static S2 window

For `0x060008E1`:

```text
s2off = 0xCB994
```

First 32 DWORDs:

```text
6A24E418 6A297A33 6AE87A0C 5A2B686A
6A24E41A 5A686884 6A24E41A 5A286893
6A24E413 5AE86859 5AE96858 4AF261EA
4A3361EB 6A636A5F 5AC06890 4AF861EE
7A866873 6A24E41E 4A7361E0 5AA86855
2A6168B0 4A7161ED 7A456879 6A24E41E
4AF361E1 5A286855 2AE168B1 4AF161EC
7AC5687A 6A24E41F 0A4B687D 5AA86840
```

Direct search for virtual-token low24 values `0x800001..0x800006` in S2 returned zero hits.

Verdict:

```text
REFUTED:
  S2 stores observed virtual tokens verbatim as simple DWORDs.
```

---

# AK.28 — Offline S2 ↔ runtime-reader bridge

## 16. Historical runtime-word correlation

23 DWORD values previously seen at the runtime S2 reader were searched over all 399,234 S2 DWORDs.

Result:

```text
historical values present in S2 : 23 / 23
globally unique among them       : 17
```

Examples of globally unique controls:

```text
4A3361F9 @ S2+0xCC05C
5A2968EA @ S2+0xCBBEC
6A297A71 @ S2+0xC9F0C
6AED6A40 @ S2+0xCB3B8
5A69696D @ S2+0xCC23C
```

This proves exact content identity between the offline S2 corpus and the historical runtime-reader corpus.

## 17. Target fingerprint hypothesis rejected

When every kind8 pointer was scored with a 128-word window, target `060008E1` ranked:

```text
rank          : 3179 / 4885
distinct hits : 1
hit           : 6A297A33 at local +1 DWORD
global count  : 1845
```

Verdict:

```text
RETRACTED:
  the historical 23-value set is a fingerprint specific to 060008E1.
```

The historical set mixes reader events from multiple S2 objects/method transactions.

## 18. Direct runtime S2 base

Decoded metadata runtime base for this TTD trace:

```text
0x24104631040
```

S2 offset inside metadata:

```text
0x5A8E0
```

Candidate runtime S2 base:

```text
0x24104631040 + 0x5A8E0
= 0x2410468B920
```

Two globally unique controls match at the exact predicted addresses:

```text
offline S2+0xCC05C = 0x4A3361F9
runtime 0x2410475797C = 0x4A3361F9

offline S2+0xCC23C = 0x5A69696D
runtime 0x24104757B5C = 0x5A69696D
```

Second control is read directly at:

```asm
HVMRun64!VMRuntime+0x36dd2b
0x180377AAB  xor r14d,dword ptr [r15+rax*4]
EA = 0x24104757B5C
DWORD = 0x5A69696D
```

Therefore:

```text
CONFIRMED for this runtime trace:
  runtime S2 base = 0x2410468B920
  runtimeAddress = runtimeS2Base + offlineS2Offset
```

## 19. Exact runtime object for 060008E1

```text
060008E1 s2off = 0xCB994
runtime address = 0x2410468B920 + 0xCB994
                = 0x241047572B4
```

Runtime dump at `0x241047572B4` matched the first 32 offline S2 DWORDs exactly, including:

```text
6A24E418 6A297A33 6AE87A0C 5A2B686A
6A24E41A 5A686884 6A24E41A 5A286893
6A24E413 5AE86859 5AE96858 4AF261EA
4A3361EB 6A636A5F 5AC06890 4AF861EE
...
```

Verdict:

```text
CONFIRMED:

host MethodDef 060008E1
 -> payload record 0x1CFF40
 -> kind8
 -> s2off 0xCB994
 -> offline S2+0xCB994
 -> runtime S2 object 0x241047572B4

runtime S2 contents are byte-for-byte identical to offline s2.bin for this object/window.
```

---

# AK.29 — Runtime traversal and first kind8 node decoder

## 20. Target read census

TTD read query over:

```text
0x241047572B4 .. 0x24104757334
```

returned 0x11E / 286 read events.

Early bytewise reads at `0x18001A352` occur during a pre-final transform stage and are not used as S2 VM semantics.

Final S2 DWORD reads expose three relevant runtime consumers:

```text
0x1803958E7  special/control-word consumer
0x180377AAB  generic-word consumer A
0x180377DCB  generic-word consumer B
```

## 21. Non-linear logical traversal

Physical target words by slot:

```text
slot 0  6A24E418   control family
slot 1  6A297A33
slot 2  6AE87A0C
slot 3  5A2B686A
slot 4  6A24E41A   control family
slot 5  5A686884
slot 6  6A24E41A   control family
slot 7  5A286893
slot 8  6A24E413   control family
slot 9  5AE86859
...
slot16  7A866873
```

Observed runtime visit order:

```text
slot0 control
 -> slot1
 -> slot2
 -> slot3
 -> slot6 control
 -> slot7
 -> slot4 control
 -> slot5
 -> slot8 control
 -> slot9..slot16
```

Therefore:

```text
CONFIRMED:
  runtime traversal is not a simple linear S2 walk.

STRONG:
  S2 contains logical/threaded nodes or graph-like control structure.
```

Do not yet call it a specific tree/CFG implementation.

## 22. Distinct control family consumer

Filtering out AAB/DCB shows exactly the four special target words read by one IP:

```text
11A7E2:16EA   0x1803958E7  @slot0  6A24E418
11CEBC:1D4B   0x1803958E7  @slot6  6A24E41A
11CF9B:9A2    0x1803958E7  @slot4  6A24E41A
121B00:1153   0x1803958E7  @slot8  6A24E413
```

At the handler:

```asm
0x1803958E7  xor eax,dword ptr [rcx]
0x1803958E9  btr dx,13h
0x1803958EE  mov dword ptr [rbx+0D8h],eax
...
0x1803958FB  lea rax,[rcx+4]
```

For root control:

```text
EAX before = 0x6A24E41B
raw        = 0x6A24E418
XOR result = 3
```

For the other three:

```text
6A24E41B ^ 6A24E41A = 1
6A24E41B ^ 6A24E41A = 1
6A24E41B ^ 6A24E413 = 8
```

Thus for all four observed target controls:

```text
CONFIRMED for 060008E1:
  controlValue = rawControl ^ 0x6A24E41B

observed controlValue sequence:
  3, 1, 1, 8
```

Execution consumes exactly 3, 1, 1, and 8 associated generic words respectively.

Therefore:

```text
STRONG++ for 060008E1:
  controlValue is the generic-item count of the logical node.
```

Global all-kind8 generalization remains unproven until offline census.

## 23. AAB generic consumer

At first generic word:

```asm
0x180377AAB  xor r14d,dword ptr [r15+rax*4]
```

Observed:

```text
word         = 0x6A297A33
R14D before  = 0
R14D after   = 0x6A297A33
```

At second generic word:

```text
word         = 0x6AE87A0C
R14D before  = 0
R14D after   = 0x6AE87A0C
```

Addressing correction:

At the first hit:

```text
R15 = 0x241047572B8
RAX = 0
EA  = R15
```

Therefore:

```text
RETRACTED:
  R15 is the global runtime S2 base and RAX is an absolute S2 DWORD index.

CONFIRMED at observed hits:
  R15 is a current/local S2 word-base pointer;
  RAX is a local index for that reader invocation.
```

AAB's XOR is not an accumulator across the first two measured generic nodes because R14D starts at zero independently.

## 24. DCB generic consumer

At the same generic word:

```asm
0x180377DCB  mov eax,dword ptr [r15+rax*4]
0x180377DCF  xor eax,dword ptr [rsp+34h]
```

First item of root block:

```text
raw        = 0x6A297A33
[rsp+34]   = 3
decoded    = 0x6A297A30
```

The initial hypothesis that `[rsp+34]` was a countdown was directly rejected.

Observed root block (`controlValue=3`):

```text
item i=0 mask 0x03
item i=1 mask 0x04
item i=2 mask 0x05
```

Observed `controlValue=1` block:

```text
item i=0 mask 0x01
```

Observed `controlValue=8` block:

```text
i=0 mask 0x08
i=1 mask 0x09
i=3 mask 0x0B
i=6 mask 0x0E
i=7 mask 0x0F
```

These include independent intermediate/end-point checks:

```text
121B01:710   [rsp+34] = 0x09
121B01:14B4  [rsp+34] = 0x0B
121B02:309   [rsp+34] = 0x0E
121B02:A67   [rsp+34] = 0x0F
```

Therefore for all measured items of `060008E1`:

```text
CONFIRMED observed relation:
  itemMask = controlValue + itemIndex

  decodedItem = rawItem ^ itemMask
```

The earlier countdown model is:

```text
REFUTED.
```

## 25. First offline decoded target nodes

### node count=3

```text
control raw     6A24E418 -> count 3

raw 6A297A33 ^ 03 = 6A297A30
raw 6AE87A0C ^ 04 = 6AE87A08
raw 5A2B686A ^ 05 = 5A2B686F
```

### node count=1 at physical slot6

```text
control raw     6A24E41A -> count 1
raw 5A286893 ^ 01 = 5A286892
```

### node count=1 at physical slot4

```text
control raw     6A24E41A -> count 1
raw 5A686884 ^ 01 = 5A686885
```

### node count=8

```text
control raw     6A24E413 -> count 8

5AE86859 ^ 08 = 5AE86851
5AE96858 ^ 09 = 5AE96851
4AF261EA ^ 0A = 4AF261E0
4A3361EB ^ 0B = 4A3361E0
6A636A5F ^ 0C = 6A636A53
5AC06890 ^ 0D = 5AC0689D
4AF861EE ^ 0E = 4AF861E0
7A866873 ^ 0F = 7A86687C
```

The convergence of several decoded low bytes (`E0`, `51`) is supporting evidence that `count+i` removes a real position/index encoding layer rather than matching accidentally.

Do not assign IL opcode/token semantics to these decoded words yet.

---

# 26. Current canonical architecture

After AK.25–AK.29:

```text
MethodDef RID
  -> host Stage0 lookup
  -> directory entry
  -> payload record
       |
       +-- kind0 / kind3
       |      -> D0.high24
       |      -> exact S0 record
       |      -> codeSize/items/EH/... structural metadata
       |
       +-- kind8
              -> D0.high24 value
              -> D1 s2off/sentinel
              -> exact S2 entry
              -> logical control/item traversal
              -> first observed decode layer:
                   count = controlRaw ^ 0x6A24E41B
                   item  = itemRaw ^ (count + i)
              -> deeper semantic VM decoding OPEN
```

For `060008E1` specifically the chain is now exact through S2 runtime identity:

```text
060008E1
 -> payload @0x1CFF40
 -> kind8
 -> value 0x19
 -> s2off 0xCB994
 -> offline S2+0xCB994
 -> runtime S2 object 0x241047572B4
 -> control/generic readers
 -> observed first decode layer
```

---

# 27. Retractions / corrections carried forward

Do not regress to these older models:

```text
RETRACTED / REFUTED
-------------------
primaryOffset == generic S0 record offset for all payload families
kind8 high24 -> S0
kind8 body uses kind3 XOR+RC4 at record+8
next kind8 S2 pointer delta is simple record length
S2 stores virtual tokens verbatim
historical 23 reader DWORDs are a 060008E1-specific fingerprint
R15 at AAB is global S2 base
[rsp+34] is a countdown
```

Also retain:

```text
- scanner `extracted` != semantic success;
- exact S0 size/field0 comparisons are not independent evidence after an exact same-record join;
- kind0 routing identity does not prove kind0 body crypto equals kind3;
- 060008E1 control/item decoder is not yet proven globally over every kind8 object.
```

---

# 28. What is now closed vs open

## CONFIRMED / structurally closed

```text
- payload method lookup to recordOffset
- payload raw kind split: 0 / 3 / 8
- kind0+kind3 perfect 10,960-record S0 partition
- kind8 method-resolved [08][u24][u32 s2off/sentinel] structure
- 4,885 unique non-sentinel kind8 S2 pointers
- 4,885/4,885 entry pointers begin on raw tag6
- offline S2 exact runtime identity for the TTD trace
- runtime S2 base 0x2410468B920 for that trace
- exact 060008E1 runtime S2 object
- special 6A24E4xx target words use 0x1803958E7
- generic target words use AAB/DCB pair
- 060008E1 control XOR decode values 3/1/1/8
- observed item mask relation count+i
```

## OPEN

```text
- global validation of AK.29 grammar across 4,885 non-sentinel kind8 entries
- semantics of decoded control/items beyond the first XOR layer
- exact logical-node linkage/traversal encoding
- dynamic prefix reconstruction in general
- virtual KEY -> exact packed-pool selector algorithm
- exact local types via S1
- kind0 body semantics/crypto
- 63 kind8 sentinel semantics
```

---

# 29. Next phase — AK.30

Do not broaden TTD again before using the decoder already obtained.

Next offline task:

```text
AK.30 — S2 logical-node census
```

Goals:

1. Starting from all 4,885 non-sentinel kind8 entry pointers, test whether control words decode consistently under the observed control XOR relation or derive the correct per-context control base if not constant.
2. Test whether decoded control value predicts the number/range of generic items for each logical node.
3. Apply `itemRaw ^ (count+i)` where structurally justified and census decoded tag/marker/data distributions.
4. Determine whether logical nodes can be traversed entirely offline without following the native VM.
5. Use runtime only as a narrow oracle when an offline structural ambiguity remains.

Stop rule:

```text
Do not claim a global kind8 parser until the offline census closes boundaries and traversal on substantially more than the single 060008E1 target.
```

---

## Final checkpoint verdict

AK.25–AK.29 changes the problem substantially:

```text
Before:
  MethodDef -> payload -> ambiguous S0/S2 interpretation -> runtime-only VM behavior

Now:
  MethodDef -> payload family dispatch
            -> kind0/3 -> exact S0
            -> kind8   -> exact S2 runtime object
                       -> observed logical-node traversal
                       -> first reproducible offline XOR decode layer
```

The final host-only unpacker is still incomplete, but S0 identity and the static-to-runtime S2 bridge are no longer blockers. The next blocker is semantic reconstruction from the decoded S2 logical nodes, not locating or identifying their data.