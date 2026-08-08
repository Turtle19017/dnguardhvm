# BƯỚC AK.22 — Record layer toàn corpus và exact KEY→token data-flow

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06  
Tiếp nối: AK.21, commit `4b24cbabe20ad154d2ad928817f31c7463817a4c`

> [!IMPORTANT]
> ## CURRENT CANONICAL STATUS
>
> File này là trạng thái canonical mới nhất.
>
> AK.21 giữ vai trò lịch sử: giả thuyết payload, raw-grep âm, cache lineage và các phép đo dẫn tới kết quả hiện tại. Các overclaim cũ ở AK.21 §1–§13 được thay thế bởi phân loại trong tài liệu này.

---

## 1. Phạm vi đúng của kết luận `items[]` và S1

Biên quan sát:

```text
items[] = (tag << 24) | low24
low24 max = 0x2CD1
S1 size   = 0x2CD8
```

KEY 1 của method mẫu ánh xạ tới:

```text
KEY 1 → CLR token 0x040088ED
RID   = 0x88ED > 0x2CD1
```

Điều này bác mô hình trực tiếp:

```text
KEY k → record.items[k-1] → real CLR operand token
```

S1 đã có các signature chứa compressed `TypeDefOrRef` coded indices, ví dụ:

```text
12 82 31 → TypeRef 0x0100008C
12 BB CC → TypeDef 0x02000EF3
```

Phán quyết:

```text
CONFIRMED
  S1 signature blobs có thể chứa metadata type references
  dưới dạng compressed TypeDefOrRef coded indices.

REFUTED
  items[] là bảng KEY→real operand token trực tiếp.

STRONG
  items[] trỏ vào signature/type arena S1.

UNPROVEN
  Type references trong S1 liên hệ thế nào với virtual-token operands
  và ordered KEY→real-token map của cùng method.
```

Lập luận phân bố `tag5` không được dùng làm chứng minh bất khả thi; nó chỉ là thống kê corpus.

---

## 2. CONFIRMED — record layer khớp toàn bộ 10.960 row

Validator tái lập:

```text
research/tools/ak21_validate_record_layer.py
```

Raw header được đọc trực tiếp từ `md_full.bin`:

```text
recOff+0x00  u8  maxStack
recOff+0x01  u24 codeSize
recOff+0x04  u16 itemBytes
recOff+0x06  u16 ehCount
recOff+0x08  u16 itemCount
recOff+0x0A  u16 ehDataSize
```

Kết quả:

```text
CSV.maxStack == raw.maxStack                    mismatch 0
CSV.codeSize == raw.codeSize                    mismatch 0
CSV.nLocals  == raw.itemCount                   mismatch 0
CSV.ehCount  == raw.ehCount                     mismatch 0
raw.itemBytes == 4 * raw.itemCount              mismatch 0
nextRecOff-recOff == 12+itemBytes+ehDataSize    mismatch 0
ilOffset[i+1] == ilOffset[i] + codeSize[i]       mismatch 0
record bounds                                  mismatch 0
```

Record cuối:

```text
lastRecordEnd = 0x57C08 = S0 size
```

Phán quyết:

```text
CONFIRMED
  CSV.nLocals == raw.itemCount trên toàn corpus.
  raw.itemBytes == 4 * raw.itemCount.
  recordSize == 12 + itemBytes + ehDataSize.
  10.960 record phân hoạch kín S0 [0,0x57C08).
  ilOffset là cumulative codeSize trên toàn bộ 10.959 cạnh.

UNPROVEN
  ilOffset là raw offset thật trong pl_full.bin.
  Record của 0x060008E1 có itemCount=0.
```

Không có opcode local không chứng minh signature-item list rỗng; method mẫu vẫn chưa join với record cụ thể.

### Lệnh tái lập

```powershell
python .\research\tools\ak21_validate_record_layer.py `
  --md "C:\hvm\md_full.bin" `
  --csv "C:\hvm\methods.csv" `
  --payload "C:\hvm\pl_full.bin" `
  --s2 "C:\hvm\s2.bin" `
  --host "D:\LordsBot-Release\LordsMobileBot.exe" `
  --s0-size 0x57C08 `
  --out "C:\hvm\ak21_offline_validation.json"
```

---

## 3. Artifact provenance và positive controls

```text
md_full.bin
  size   0x1E06E8
  sha256 809333cb66fb64622e7af9f5f1d32836cbca3b19ba66f7c0c41d34caa0a62284

methods.csv
  size   0x4D76F
  sha256 49aa99ed2e1223577905e46b86902b0e194cf7fdb74dc93204166bbe14a3743a

pl_full.bin
  size   0x346C10
  sha256 1e007a5b90f5bf8afa2ea86c248a877cda6156233859fe35e5d9d4646e0ed3e7

s2.bin
  size   0x185E08
  sha256 190c4e8200e2cff577a3085a885f3fa7c99498e6559273fb9dcc153a0ea5ac25

LordsMobileBot.exe
  size   0x0D390628
  sha256 2178bc077a362a18dbdc5b141478740f0870fedc2774e2ed0d741c606f318a0e
```

Positive controls:

```text
md_full.bin first 16-byte anchor: offset 0, count 1
pl_full.bin first 16-byte anchor: offset 0, count 1
s2.bin first 16-byte anchor: offset 0, count 1
LordsMobileBot.exe: MZ at 0, PE signature at file offset 0xF0
S2: 399234 / 399234 DWORD có observed tag nibble 0xA
```

Raw grep âm tính chỉ áp dụng cho các anchor của method `0x060008E1`; không tổng quát hóa sang toàn corpus.

---

## 4. Census `codeSize/maxStack` — kết luận đúng mức

Quan sát:

```text
codeSize 0x2D: 71 record, 69 record EH=0
codeSize 0x19: 31 record, tất cả EH=0
không record nào trong hai tập đồng thời có maxStack=8
```

```text
CONFIRMED
  Bộ lọc kết hợp codeSize∈{0x2D,0x19} + maxStack=8 + EH=0
  trả 0 candidate.

UNPROVEN
  codeSize giả định, runtime↔record maxStack equality,
  hay phép join là thành phần thất bại.
```

`methods.csv.maxStack == raw.maxStack` đã được xác nhận toàn corpus. Chưa biết raw-record `maxStack` có khớp runtime sample vì chưa join record.

---

## 5. Tiny format và UserString

Tuple runtime:

```text
codeSize=0x2D, maxStack=8, EH=0, không thấy local opcode
```

chỉ tương thích tiny format; fat header vẫn có thể biểu diễn cùng giá trị.

```text
STRONG
  Sample tương thích CorILMethod_TinyFormat.

UNPROVEN
  Original protected MethodBody dùng tiny header.
```

Wrapper có conditional custom path cho UserString khi `[proxy+0x78] != 0`:

```text
CONFIRMED
  Conditional custom UserString path tồn tại.

STRONG
  DNGuard có thể bảo vệ/biến đổi #US qua path này.

UNPROVEN
  Sample UserString cụ thể và representation offline của #US.
```

---

## 6. CONFIRMED — ordered KEY→real-token map

Tại `AD7BF:32`:

```text
head = 0x24100668DA0
head+0x00 = 0x24100668CE0   leftmost
head+0x08 = 0x24100668D10   root
head+0x10 = 0x24100668D40   rightmost
```

Node layout:

```text
+0x00 left
+0x08 parent
+0x10 right
+0x18 u32 KEY
+0x1C u32 real CLR metadata token
```

Mappings quan sát:

```text
node 0x24100668CE0: KEY 1 → 0x040088ED
node 0x24100668D10: KEY 2 → 0x010004C9
node 0x24100668D40: KEY 3 → 0x0A001D99
```

Cây có root KEY 2, left KEY 1, right KEY 3. Tại position này không có non-sentinel node KEY 4–6.

```text
CONFIRMED
  Đây là ordered KEY→real-token map cho transaction/lifetime quan sát.

STRONG
  Implementation tương thích MSVC std::_Tree/std::map<u32,u32>.

UNPROVEN
  Lifetime/scope toàn method và vị trí KEY 4–6.
```

---

## 7. CONFIRMED — exact map value → EAX → R14D

Read watchpoint trên `0x24100668CFC` bắt exact load:

```asm
0x18000587F  mov eax,dword ptr [rbx+1Ch]
```

Với:

```text
RBX        = 0x24100668CE0
[RBX+18h]  = 1
[RBX+1Ch]  = 0x040088ED
```

Hàm epilogue rồi `ret` về:

```asm
0x180378851  mov r14d,eax
```

Data dependency:

```text
map[KEY 1].value = 0x040088ED
  → EAX = 0x040088ED
  → R14D = 0x040088ED
```

Metadata token không thuộc output ABI của `CORINFO_RESOLVED_TOKEN`; output ABI là handles/spec blobs. Tuy nhiên helper nội bộ giữ real token trong EAX/R14D trước cache layer.

---

## 8. CONFIRMED — R14D + resolver mask → masked-real R8D

Tại `AD7BF:30..32`:

```asm
0x180379287  mov r8d,dword ptr [rax+30h]
0x18037928B  xor r8d,r14d
0x18037928E  mov dword ptr [rsp+150h],r8d
```

Với:

```text
RAX          = resolver state 0x24100666840
[RAX+30h]    = 0x6A714B62
R14D         = 0x040088ED
R8D sau XOR  = 0x6E71C38F
```

```text
maskedReal = resolverMask XOR realToken
0x6E71C38F = 0x6A714B62 XOR 0x040088ED
```

Việc stack slot trước đó từng chứa raw virtual token chỉ là storage lifecycle. Bằng chứng semantic nằm ở chain register/instruction trên; không dùng từ `scrub`.

KEY 2 xác nhận cùng mask:

```text
0x6B714FAB XOR 0x6A714B62 = 0x010004C9
```

---

## 9. Pipeline runtime canonical cho KEY 1

```text
virtual token 0x04800001
    ↓ KEY extraction / lookup call context
ordered KEY→real-token map
    ↓ node KEY 1, value 0x040088ED
mov eax,[node+1Ch]
    ↓
EAX = 0x040088ED
    ↓ mov r14d,eax
R14D = real CLR token
    ↓ mov r8d,[resolverState+30h]
    ↓ xor r8d,r14d
R8D = masked-real 0x6E71C38F
    ↓ cache lookup 0x1800021B0, key (module, masked-real)

cache hit:
    node handles → output

cache miss:
    request.token ^= resolverMask
    ↓ real CLR token 0x040088ED
    ↓ underlying CoreCLR CEEInfo::resolveToken
    ↓ cache insert 0x1800058A0

runtime handles
    ↓ 0x38-byte copy into CORINFO_RESOLVED_TOKEN+0x18
```

Offline rebuilder không cần mô phỏng runtime handle cache. Cạnh bắt buộc là nguồn host/offline dựng:

```text
method/KEY → real CLR metadata token
```

---

## 10. CURRENT STATUS

```text
CONFIRMED
  S1 signature blobs có compressed TypeDefOrRef references.
  items[] không phải bảng KEY→real-token trực tiếp.
  S0 record layout và CSV projection trên toàn bộ 10.960 record.
  nLocals == raw.itemCount; itemBytes == 4*itemCount.
  recordSize = 12+itemBytes+ehDataSize; coverage đúng 0x57C08.
  ilOffset recurrence đúng toàn bộ 10.959 cạnh.
  ordered KEY→real-token map, node +0x18 KEY / +0x1C token.
  KEY1→040088ED, KEY2→010004C9, KEY3→0A001D99.
  map value→EAX→R14D exact data-flow.
  R8D=[resolverState+0x30] XOR R14D exact data-flow.
  Handle cache key (module, masked-real), CoreCLR delegation và 0x38-byte output copy.

STRONG
  Payload dùng encoded/structured representation.
  KEY scope cục bộ theo method/transaction.
  Resolver mask scope theo resolver/proxy instance.

UNPROVEN
  Exact source host/offline dựng KEY→real-token map.
  KEY extraction từ virtual token ở lookup call-site.
  KEY 4–6 và lifetime/map stage của suffix.
  Record của 0x060008E1 và RID→recordIndex.
  Payload/S2 representation và mask source offline.
```

---

## 11. Thứ tự tiếp theo

| Hạng | Việc | Mục tiêu |
|---|---|---|
| 1 | Truy caller/source dựng ordered KEY→real-token map | Đóng nguồn offline quan trọng nhất |
| 2 | Bắt KEY extraction và comparator/lookup input | Hoàn tất virtual token → KEY → node |
| 3 | Kiểm map ở các lifetime/position muộn để tìm KEY 4–6 | Hoàn thành method oracle |
| 4 | Đếm MethodDef rows host | Xác định 10.960 record có thể phủ toàn bộ MethodDef hay chỉ một tập con được bảo vệ |
| 5 | Join method `0x060008E1` với S0 record | Cố định RID→record và field semantics |
| 6 | Reverse payload/S2 bằng cặp method/KEY đã biết | Tiến tới host-only decoder |

Row count một mình không xác định thứ tự record, không chứng minh dense index và cũng không chứng minh permutation.
