# AK.22 — Ordered-map source and packed-token lineage

Mẫu: `LordsMobileBot.exe`  
Ngày checkpoint: 2026-08-07  
Nhánh nghiên cứu: `ak21-items-not-tokens`  
Tiếp nối: `AK.22-record-layer-and-key-token-dataflow.md`

> [!IMPORTANT]
> Đây là checkpoint riêng cho nguồn dựng ordered `KEY -> real CLR token` map và packed-token lineage.
> Không thay thế các kết luận record-layer trong AK.22 trước đó.

---

## 1. Mục tiêu và stop-rule

Nút thắt offline hiện tại:

```text
method/context + KEY
-> pool index
-> packed token
-> RID/kind
-> real CLR token
-> ordered map
```

Không ưu tiên quay lại:

```text
raw payload grep
handle-cache deep dive
resolver-mask generator
full VM ISA
```

Stop-rule cho packed decoder:

```text
- chỉ một cửa sổ hẹp quanh transaction đã biết;
- tối đa khoảng 20 dynamic instructions liên quan;
- nếu semantic data-flow đủ kín thì không cần giải toàn micro-op implementation.
```

---

## 2. CONFIRMED — ordered map node được tạo `{KEY,0}` rồi gán token

Node layout quan sát:

```text
+0x00 left
+0x08 parent
+0x10 right
+0x18 KEY u32
+0x1C real CLR token u32
```

Ba node hiện biết:

```text
KEY1 node 0x24100668CE0
  +0x18 = 0x24100668CF8
  +0x1C = 0x24100668CFC

KEY2 node 0x24100668D10
  +0x18 = 0x24100668D28
  +0x1C = 0x24100668D2C

KEY3 node 0x24100668D40
  +0x18 = 0x24100668D58
  +0x1C = 0x24100668D5C
```

Exact writes:

```text
KEY1
A9BE9:1C70  IP 0x180001BA1  qword @0x24100668CF8 <- 1
A9BEF:1417  IP 0x1803525B9  dword @0x24100668CFC <- 0x040088ED

KEY2
A9BFE:9B8   IP 0x180001BA1  qword @0x24100668D28 <- 2
A9C04:1DEB  IP 0x18021024F  dword @0x24100668D2C <- 0x010004C9

KEY3
A9C17:11F7  IP 0x180001BA1  qword @0x24100668D58 <- 3
A9C1E:D58   IP 0x1801DF4F1 dword @0x24100668D5C <- 0x0A001D99
```

Phán quyết:

```text
CONFIRMED
  Mỗi node quan sát được tạo theo hai pha:
  1. insert/default state {KEY,0};
  2. gán real CLR token riêng vào node+0x1C.

STRONG
  Cấu trúc là ordered map / tree tương đương std::map<u32,u32>.

UNPROVEN
  Exact STL API/name không cần thiết cho offline rebuilder.
```

---

## 3. Static packed token pool — ba sample độc lập

Runtime pool base quan sát:

```text
0x241015F346C
```

Host offset đã biết:

```text
0xB1346C
```

Ba entry liên quan:

```text
KEY1 -> pool idx4 @ 0x241015F347C -> packed 0x00088ED3
KEY2 -> pool idx1 @ 0x241015F3470 -> packed 0x00004C91
KEY3 -> pool idx9 @ 0x241015F3490 -> packed 0x0001D995
```

Arithmetic decode:

```text
0x00088ED3 >> 4 = 0x88ED ; low nibble 3 -> FieldDef  -> 0x040088ED
0x00004C91 >> 4 = 0x04C9 ; low nibble 1 -> TypeRef   -> 0x010004C9
0x0001D995 >> 4 = 0x1D99 ; low nibble 5 -> MemberRef -> 0x0A001D99
```

Observed pool reads:

```text
KEY1 pool idx4:
A9BDD:1AF2  IP 0x1802B72B6
(later another read at ABBAD:2106 IP 0x18001708F)

KEY2 pool idx1:
A9BF0:122   IP 0x18029BB35

KEY3 pool idx9:
A9C04:200D  IP 0x1801A8C32
```

Phán quyết:

```text
CONFIRMED
  Ba packed entries decode số học đúng tuyệt đối thành ba real token
  được ghi vào KEY1/2/3 nodes.

STRONG
  Ordered KEY->token map được dựng từ static packed token pool.

UNPROVEN globally
  Mọi KEY ở mọi lifetime luôn dùng cùng pool/path.
```

---

## 4. KEY2 — exact pool -> packed staging

### 4.1 Pool load

TTD `A9BF0:122`:

```asm
0x18029BB35  mov r8d,dword ptr [r10+rdx*2-757EA108h]
```

Effective address:

```text
0x241015F3470
```

Result:

```text
R8D = 0x00004C91
```

### 4.2 Packed staging

TTD `A9BF0:127` / next step:

```text
RDI = 0xFD82557594
R10 = 0
R8D = 0x4C91
```

Transition:

```text
before [0xFD82557594] = 0x00000241
after  [0xFD82557594] = 0x00004C91
```

### 4.3 First exact consumer

TTD `A9BF0:14D`:

```asm
0x180208427  mov r9d,dword ptr [rdi+rcx-5F97BE27h]
```

EA:

```text
0xFD82557594
```

Transition:

```text
R9D before = 0x000001AC
R9D after  = 0x00004C91
```

Therefore:

```text
pool idx1 0x4C91
-> R8D
-> packed staging
-> R9D = packed 0x4C91
```

is instruction-level CONFIRMED.

---

## 5. Stack reuse correction

A narrow TTD write query over `[0xFD82557594,0xFD82557598)` between `A9BF0:14E..200C` returned `0x1D` overlapping writes.

Examples include:

```text
A9BF0:349   @57594 size4 <- 0x4C91
A9BF0:3D1   @57594 size4 <- 0
A9BF0:453   @57594 size4 <- 0xF
A9BF0:4EE   @57594 size4 <- 0xFFFFFFFE
A9BF0:574   @57594 size4 <- 1
...
```

Therefore:

```text
CONFIRMED
  Stack addresses are reused aggressively and must be labelled by TTD position.

RETRACTED
  0xFD82557594 has one fixed semantic throughout the transaction.
```

TTD memory-query interpretation used in this session:

```text
- queries can return overlapping accesses;
- inspect Address + Size for the semantic access;
- Value is treated only for the low Size bytes beginning at Address.
```

---

## 6. AK.22A.1 CLOSED — packed -> RID

This is the main result of this checkpoint.

### 6.1 Final input layout

Narrow write census showed:

```text
A9BF0:1EE3  @0xFD82557594 size4 <- 0
A9BF0:1F70  @0xFD82557596 size2 <- 4
A9BF0:1FCC  @0xFD82557592 size4 <- 0x4C91
A9BF0:2006  @0xFD82557594 size4 <- 0x4C9
             OverwrittenValue = 0x00040000
```

This retracted the earlier model that `@57594` was transformed in place from `0x4C91` to `0x4C9`.

### 6.2 Exact reads

Read query over `[0xFD82557592,0xFD82557598)` in `A9BF0:1FCC..2006` returned exactly the relevant input reads:

```text
A9BF0:1FFF
IP      0x1801FAEBE
Address 0xFD82557592
Size    4
Value   0x4C91

A9BF0:2000
IP      0x1801FAEC6
Address 0xFD82557596
Size    1
Value   0x04
```

Instruction-level state at `A9BF0:1FFF`:

```asm
0x1801FAEBE  or r9d,dword ptr [rdi+rbp*2-7463E5D6h]
```

Registers / semantic input:

```text
EA         = 0xFD82557592
memory     = 0x00004C91
R9D before = 0
R9D after  = 0x00004C91
```

So although the native opcode is `or`, for this transaction:

```text
R9D = 0 | 0x4C91 = 0x4C91
```

Next dynamic instruction at `A9BF0:2000`:

```asm
0x1801FAEC6  mov cl,byte ptr [rdi+rbp*2-7463E5D2h]
```

Exact input:

```text
EA    = 0xFD82557596
memory = 0x04
CL     = 4
```

The dynamic path then reaches:

```asm
0x1801FAED5  call 0x180372FBB
```

At `A9BF0:2006` inside that transaction:

```asm
0x180372FCB  mov dword ptr [rdi+rax*4-3ACh],r9d
```

Registers / result:

```text
R9D = 0x000004C9
EA  = 0xFD82557594
old = 0x00040000
new = 0x000004C9
```

Numerical relation:

```text
0x4C91 >> 4 = 0x4C9
```

### 6.3 Verdict

```text
CONFIRMED — KEY2 packed-token RID extraction

Input packed = 0x4C91
Input count  = 4
Result RID   = 0x4C9

Semantic decoder operation for this transaction:

    RID = packed >> 4
```

Exact native micro-op inside the intervening decoder call is not required for this conclusion and is intentionally not pursued beyond the stop-rule.

Across the three known pool samples:

```text
0x88ED3 >> 4 = 0x88ED
0x04C91 >> 4 = 0x04C9
0x1D995 >> 4 = 0x1D99
```

Current packed-format classification:

```text
CONFIRMED
  RID = packed >> 4

STRONG
  kind = packed & 0xF
```

`kind = packed & 0xF` remains STRONG until its local data-flow into metadata table prefix is inspected.

---

## 7. RID propagation after extraction — CONFIRMED

At `A9BF0:200C`:

```asm
0x180376CAB  mov ebp,dword ptr [rdi]
```

with:

```text
RDI = 0xFD82557594
[57594] = 0x4C9
```

therefore:

```text
EBP = 0x4C9
```

Later exact creator of a clean RID staging slot:

```text
A9BF5:11F5
0x180364E19  mov qword ptr [r10],rbp
R10 = 0xFD82557598
RBP = 0x4C9

before [57598] = 0x00000001800C023B
after  [57598] = 0x00000000000004C9
```

A later same-value rewrite:

```text
A9BF5:1D71
0x180328DD2  mov qword ptr [rbp+r10],r11
```

was explicitly rejected as the creator because the slot already contained `0x4C9`.

Later load:

```text
A9BF5:2620
0x18035C536  mov r10,qword ptr [...]
EA = 0xFD82557598
R10 = 0x4C9
```

Exact next staging writer:

```text
A9BF5:2642
0x1802B4DEA  mov qword ptr [r8+rax+69BBh],r10
EA = 0xFD825573A8
before = 0
after  = 0x4C9
```

Then:

```text
A9BF6:59A
0x1802D6A32  mov rcx,qword ptr [...]
EA = 0xFD825573A8
RCX = 0x4C9
```

Thus:

```text
RID 0x4C9
-> EBP
-> [57598]
-> R10
-> [573A8]
-> RCX
```

is CONFIRMED.

---

## 8. Decoder-end real-token state

At `A9BFB:330`:

```text
RCX = 0x000004C9
R9  = 0x00000001
RDI = 0x01000000
R14 = 0x010004C9
```

Instruction:

```asm
0x1801A9968  mov qword ptr [rsp+70h],r14
```

Transition at destination `0xFD825575A8`:

```text
before = 0xFFFFFFFFC7C65082
after  = 0x00000000010004C9
```

Classification:

```text
CONFIRMED
  decoder state contains RID=0x4C9 and full TypeRef token 0x010004C9.

STRONG
  R9=1 is decoded kind and RDI=0x01000000 is the table prefix derived from that kind.

UNPROVEN
  exact local instruction chain kind=1 -> prefix=0x01000000 -> R14.
```

This is AK.22A.2 and is subject to a strict small-window stop-rule.

---

## 9. Real token -> KEY2 node — instruction-level downstream chain

Final KEY2 node token writer:

```text
A9C04:1DEB
0x18021024F  mov dword ptr [rcx+rdx],ebx
RCX = 0x24100668D2C
RDX = 0
EBX = 0x010004C9
```

Known downstream staging chain:

```text
R10 = 0x010004C9
-> A9C03:19B4 writes 0xFD825573D0
-> A9C04:1B01 loads R9D from 0xFD825573D0
-> A9C04:1B06 writes 0x010004C9 to 0xFD825575AC
-> EBX loads 0x010004C9
-> A9C04:1DEB writes map[KEY2]+0x1C
```

A previous hypothesis that `0xFD825575A8` fed the final `R9D` path was retracted; the exact source for that later load is `0xFD825573D0`.

---

## 10. Dynamic CFG / TTD discipline learned in this session

### 10.1 Dynamic predecessor beats linear disassembly

At the RID-slot writer path, linear `ub` showed a nearby call, but `t-` returned:

```asm
0x18020E601  jmp rcx
```

with `RCX` targeting the actual continuation.

Therefore:

```text
RETRACTED
  A nearby instruction in linear disassembly is necessarily the executed predecessor.

RULE FOR THIS TRACE
  Use TTD `t-` as ground truth for dynamic predecessor.
```

### 10.2 Register-history caution

In this trace/session, `PrevRegisterWrite` repeatedly surfaced same-value/bookkeeping events for several registers and was not treated as a semantic producer without instruction confirmation.

`NextRegisterWrite` was useful when starting from a known earlier state and asking for a concrete changed target value, e.g.:

```text
RCX -> 0x4C9
R10 -> 0x4C9
RBP -> 0x4C9
```

### 10.3 Session-specific debugger-model quirks

Record only as session/build observations, not universal WinDbg rules:

```text
- arbitrary debugger-model aliases were unreliable in this session;
- TTD position construction showed parser/API quirks between attempted forms;
- do not generalize these to all WinDbg/TTD builds.
```

---

## 11. Current status table

| Claim | Status |
|---|---|
| Node created `{KEY,0}` then token assigned at `+0x1C` | CONFIRMED |
| KEY1 packed `0x88ED3` corresponds to token `0x040088ED` | CONFIRMED |
| KEY2 packed `0x4C91` corresponds to token `0x010004C9` | CONFIRMED |
| KEY3 packed `0x1D995` corresponds to token `0x0A001D99` | CONFIRMED |
| KEY2 pool -> packed staging -> packed register | CONFIRMED |
| `RID = packed >> 4` for KEY2 decoder transaction | CONFIRMED |
| Three known packed samples obey `RID = packed >> 4` | CONFIRMED |
| `kind = packed & 0xF` | STRONG |
| kind `1` -> prefix `0x01000000` exact local lineage | UNPROVEN |
| KEY2 decoded real token reaches `map[2]+0x1C` | CONFIRMED |
| All observed map tokens globally always originate from this pool | STRONG |
| KEY -> pool index mapping/source | UNPROVEN / PRIMARY BLOCKER |

---

## 12. Next priorities

### AK.22A.2 — kind -> prefix

Only a narrow local check around the known decoder-end state:

```text
kind = 1
-> prefix = 0x01000000
-> real token = 0x010004C9
```

Stop after one local window / roughly 20 relevant dynamic steps. If not closed quickly, retain STRONG and move on.

### AK.22B — KEY -> pool index — primary offline blocker

Known mapping:

```text
KEY1 -> idx4
KEY2 -> idx1
KEY3 -> idx9
```

At each exact pool read, resolve the effective-address component that varies as `4 / 1 / 9`:

```text
EA = poolBase + poolIndex * 4
```

Goal:

```text
method/context + KEY
-> pool index
```

Classification target:

```text
- source from host image: near-direct offline source;
- source from payload/S2: representation to reverse;
- source from temporary/register: follow exactly one layer to nearest memory producer.
```

### AK.22C — KEY4-6 and map lifetime

Expected virtual operands:

```text
0x06800004 -> KEY4 -> MethodDef
0x06800005 -> KEY5 -> MethodDef
0x0A800006 -> KEY6 -> MemberRef
```

Need determine whether map accumulates, resets per transaction/method, or another lifetime/map instance exists.

---

## 13. Offline completion edge now required

Current decoder knowledge is sufficient to stop treating packed-token arithmetic as the main blocker.

The required edge for the final host-only rebuilder is now:

```text
method + KEY
-> pool index
-> packed token
-> RID/kind
-> CLR token
```

Until `KEY -> pool index` is recovered from host-readable data, ordered-map reconstruction remains runtime-dependent.
