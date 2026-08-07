# BÁO CÁO BÀN GIAO PHÂN TÍCH DNGuard HVM 4.9.6

**Ngày cập nhật:** 2026-08-05  
**Mục tiêu:** mang toàn bộ trạng thái phân tích hiện tại sang một cuộc trò chuyện khác mà không phải giải thích lại từ đầu.

---

## 1. Bối cảnh và mục tiêu cuối

Target đang phân tích:

- `LordsMobileBot.exe`
- DNGuard HVM khoảng phiên bản 4.9.6
- Method được dùng làm mẫu chính: `0x06002CD4`

Mục tiêu cuối không phải chỉ sửa một method, mà là xây dựng một pipeline generic:

1. Chỉ đọc dữ liệu từ `LordsMobileBot.exe`.
2. Khôi phục MethodBody mà `clrjit` thực sự nhận được.
3. Ghép dynamic prefix với decrypted suffix.
4. Chuyển virtual HVM token sang CLR metadata token thật.
5. Patch trở lại managed module.
6. Không cần giữ hoặc phân phối `HVMRun64.dll`.
7. Không phụ thuộc vào temp runtime dump.
8. Không cần capture thủ công từng method khi tool hoàn chỉnh.

Blocker lớn nhất hiện nay là tổng quát hóa:

```text
virtual slot / virtual token
        ↓
entry hoặc dữ liệu tương ứng trong host
        ↓
packed token
        ↓
real CLR metadata token
```

---

## 2. Dynamic prefix của method mẫu

Method `0x06002CD4` chứa ba virtual token quan trọng:

```text
0x04800001    slot 1    field-like
0x01800002    slot 2    type-like
0x0A800003    slot 3    memberref-like
```

Mapping runtime hiện đã xác nhận hoàn toàn:

```text
0x04800001 → 0x040088ED
0x01800002 → 0x010004C9
0x0A800003 → 0x0A001D99
```

Đây là kết quả chắc chắn, được xác nhận trực tiếp trong token map runtime.

---

## 3. Packed token format đã xác nhận

Packed DWORD sử dụng dạng:

```c
rid  = packed >> 4;
kind = packed & 0xF;
```

Các kind đã xác nhận:

```text
kind 1 → TypeRef
kind 3 → FieldDef
kind 4 → MemberRef
```

Các mapping đã chốt:

### Slot 1

```text
virtual token = 0x04800001
real token    = 0x040088ED
packed        = 0x00088ED3
RID           = 0x88ED
kind          = 3
```

### Slot 2

```text
virtual token = 0x01800002
real token    = 0x010004C9
packed        = 0x00004C91
RID           = 0x4C9
kind          = 1
```

### Slot 3

```text
virtual token = 0x0A800003
real token    = 0x0A001D99
packed        = 0x0001D994
RID           = 0x1D99
kind          = 4
```

Decoder dự kiến:

```csharp
static uint DecodePackedToken(uint packed)
{
    uint rid = packed >> 4;
    uint kind = packed & 0xF;

    uint prefix = kind switch
    {
        1 => 0x01000000, // TypeRef
        3 => 0x04000000, // FieldDef
        4 => 0x0A000000, // MemberRef
        _ => throw new NotSupportedException(
            $"Unknown packed token kind: {kind}")
    };

    return prefix | rid;
}
```

### Kind 5

Có entry:

```text
0x0001D995
```

giải thành cùng RID `0x1D99`, low nibble `5`.

`kind 5 → MethodDef` là giả thuyết rất mạnh vì candidate token sẽ là:

```text
0x06001D99
```

Nhưng chưa được đánh dấu confirmed bằng token map runtime.

---

## 4. Packed token region trong host

Một vùng runtime đã được quan sát tại:

```text
runtime base candidate = 0x241015F346C
```

Các DWORD xung quanh:

```text
+0x00  0x00004C71
+0x04  0x00004C91
+0x08  0x0001D9B5
+0x0C  0x0001D9C5
+0x10  0x00088ED3
+0x14  0x00000000
+0x18  0x00088EE3
+0x1C  0x000498E4
+0x20  0x00000000
+0x24  0x0001D995
+0x28  0x0006E1E4
+0x2C  0x00088F13
```

Slot 1 đã được đọc tại runtime:

```text
runtime address = 0x241015F347C
relative offset = +0x10
packed          = 0x00088ED3
```

Search trong `LordsMobileBot.exe` cho thấy cùng DWORD tại:

```text
file offset = 0xB1347C
```

Do đó current host-region mapping:

```text
host region base candidate = 0xB1346C
```

Entry `+0x24`:

```text
host offset = 0xB13490
packed      = 0x0001D995
```

### Cảnh báo quan trọng

Không được gọi:

```text
0x24100AE0000
```

là một raw buffer đầy đủ của `LordsMobileBot.exe`.

Kiểm tra cho thấy base suy ra đó chứa zero, không có `MZ`.

Kết luận chính xác hơn:

- Có một runtime blob/mapping bảo toàn byte hoặc file-offset của ít nhất vùng liên quan.
- Nó có thể là sparse mapping, section mapping, resource mapping hoặc blob được padding.
- Chưa có bằng chứng toàn bộ EXE được map 1:1 tại một base duy nhất.

---

## 5. Nhánh offset `0x24`

Một access runtime đọc:

```text
tableBase + 0x24
→ packed 0x0001D995
```

Pointer được tạo trực tiếp tại:

```asm
adc rcx,rbp
```

với:

```text
RCX trước = 0x24
RBP       = 0x241015F346C
RCX sau   = 0x241015F3490
```

Tại đoạn thực thi này carry không làm thay đổi kết quả, nên semantic là:

```c
entryPointer = tableBase + 0x24;
```

Sau đó runtime đọc DWORD:

```c
packed = *(uint32_t*)entryPointer;
// 0x0001D995
```

### Decoder tạo `0x24`

Runtime load static qword từ:

```text
HVMRun64.dll + RVA 0x192505
address = 0x180192505
value   = 0xDC000000C130CCC7
```

Chuỗi decode:

```asm
xor   rcx,rsi
neg   rcx
xor   rcx,0FFFFFFFFC1364609h
neg   rcx
bswap rcx
```

Với:

```text
encoded = 0xDC000000C130CCC7
RSI     = 0x00000000FFF97530
```

Kết quả:

```text
0x24
```

`PrevMemoryAccess("w", 0x180192505, 8)` trả rỗng vì qword tồn tại sẵn trong image `HVMRun64.dll`.

Kết luận:

- Đây là static/inlined constant của runtime path.
- Không phải nguồn host-only trực tiếp.
- Không nên tiếp tục lần writer của địa chỉ này.
- Nhánh này chỉ xác nhận cách runtime lấy byte offset `0x24`.

---

## 6. Token map runtime

Context quan sát:

```text
CONTEXT     = 0x24100669560
TREE_OBJECT = CONTEXT + 0xB8
            = 0x24100669618
```

Tree object:

```text
tree head pointer = [TREE_OBJECT + 0x08]
size field        = [TREE_OBJECT + 0x10]
```

Tại checkpoint ban đầu:

```text
HEAD = 0x24100668DA0
SIZE = 2
```

Node layout đã xác nhận:

```text
node + 0x00  link
node + 0x08  link
node + 0x10  link
node + 0x18  key / virtual slot
node + 0x1C  real CLR token
node + 0x20  tree flag/color
node + 0x21  flag phụ
```

Đây là cây ordered/red-black tương thích với layout `std::map` của MSVC.

### Node slot 1

```text
NODE  = 0x24100668CE0
KEY   = 1
TOKEN = 0x040088ED
```

### Node slot 2

```text
NODE  = 0x24100668D10
KEY   = 2
TOKEN = 0x010004C9
```

Dump xác nhận:

```text
node+0x18 = 00000002
node+0x1C = 010004C9
```

### Node slot 3

Size đổi:

```text
2 → 3
```

tại TTD:

```text
A9C17:1203
```

Node mới:

```text
NODE = 0x24100668D40
KEY  = 3
```

Khi vừa insert:

```text
TOKEN = 0
```

Sau đó caller mới ghi value, đúng kiểu:

```cpp
tokenMap[3] = realToken;
```

Rightmost pointer của sentinel sau khi link:

```text
HEAD + 0x10 = 0x24100668D40
```

Writer value cuối cùng ghi:

```text
0x24100668D5C:
00000000 → 0A001D99
```

Do đó:

```text
slot 3 → 0x0A001D99
```

đã confirmed hoàn toàn.

---

## 7. Luồng ghi token slot 3

Writer vào node value tại TTD:

```text
A9C1E:D58
```

Instruction:

```asm
mov dword ptr [rdx+rsi*4-10334h],r10d
```

Register tại đó:

```text
RDX  = 0x24100668D5C
RSI  = 0x40CD
R10D = 0x0A001D99
```

Vì:

```text
0x40CD * 4 = 0x10334
```

effective address rút gọn thành:

```asm
mov dword ptr [0x24100668D5C],r10d
```

---

## 8. Dataflow đã lần ngược của slot 3

Hiện đã lần ngược token `0x0A001D99` qua nhiều workspace stack.

### Hop 1

Tại `A9C1E:D51`:

```asm
mov r10d,dword ptr [rsi+r8-40C5h]
```

Register:

```text
RSI = 0x40CD
R8  = 0xFD825575A4
```

Effective address:

```text
0xFD825575AC
```

Nên:

```text
[0xFD825575AC] → R10D = 0x0A001D99
```

### Hop 2

Writer của `0xFD825575AC` tại `A9C1E:C7E`:

```asm
mov dword ptr [rsi+r8-0FFFFh],r11d
```

Với:

```text
RSI  = 0xFFFF
R8   = 0xFD825575AC
R11D = 0x0A001D99
```

Effective address vẫn là:

```text
0xFD825575AC
```

### Hop 3

Nguồn của `R11D` tại `A9C1E:C78`:

```asm
mov r11d,dword ptr [r10+rcx-4FF16EFCh]
```

Register:

```text
R10 = 0xFD825573A0
RCX = 0x4FF16EFC
```

Effective address:

```text
0xFD825573A0
```

Nên:

```text
[0xFD825573A0] → R11D = 0x0A001D99
```

### Hop 4

Writer của `0xFD825573A0` tại `A9C1E:5CE`:

```asm
mov qword ptr [r11+rbx-2E97F627h],r10
```

Register:

```text
R11 = 0xFD825573A0
RBX = 0x2E97F627
R10 = 0x000000000A001D99
```

Effective address:

```text
0xFD825573A0
```

Stack slot này trước đó chứa pointer node:

```text
0x0000024100668D40
```

rồi bị tái sử dụng và overwrite thành:

```text
0x000000000A001D99
```

### Hop 5

Nguồn `R10` tại `A9C1E:5B1`:

```asm
mov r10,qword ptr [r8+r10-7C7BC8A4h]
```

Register:

```text
R8        = 0xFD82557548
R10 trước = 0x7C7BC8A4
```

Effective address:

```text
0xFD82557548
```

Nên:

```text
[0xFD82557548] → R10 = 0x000000000A001D99
```

### Chuỗi hiện tại

```text
[0xFD82557548]
        ↓
R10 = 0x0A001D99
        ↓
[0xFD825573A0]
        ↓
R11D = 0x0A001D99
        ↓
[0xFD825575AC]
        ↓
R10D = 0x0A001D99
        ↓
tokenMap[3].value = 0x0A001D99
```

Tất cả các địa chỉ `0xFD...` ở trên là workspace stack và bị tái sử dụng.

Không được gán cho chúng ý nghĩa struct cố định xuyên suốt trace.

---

## 9. Nhánh `R13` là ABI noise

Tại thời điểm insert, `R13` cũng chứa:

```text
0x0A001D99
```

Nhưng `PrevRegisterWrite("r13")` dừng tại:

```asm
ntdll!RtlAllocateHeap
pop r13
```

Đây chỉ là restore nonvolatile register theo ABI sau allocator call.

Không phải nơi token được dựng.

Do đó:

- Không tiếp tục lần source từ `R13` sau heap call.
- Dùng dataflow qua `R10/R11` và memory source cụ thể.

---

## 10. Các occurrence của packed slot 3 trong host

Search `LordsMobileBot.exe` cho:

```text
0x0001D994
```

trả về:

```text
0x11ECE88
0x46C7A28
0x5C63E68
0x69CDF02
0x730500E
```

Chưa được phép chọn một occurrence chỉ vì nó xuất hiện đầu tiên.

Cần lần ngược đến load ngoài stack hoặc arithmetic decode để xác định occurrence chính xác thuộc resolver của method hiện tại.

Search:

```text
0x0001D995
```

trả nhiều hit, trong đó:

```text
0xB13490
```

là occurrence đã gắn trực tiếp với access runtime `tableBase + 0x24`.

Nhưng:

```text
0x0001D995 ≠ packed slot 3
```

Nó có cùng RID `0x1D99` nhưng low nibble khác:

```text
0x0001D994 → kind 4 → MemberRef 0x0A001D99
0x0001D995 → kind 5 → candidate MethodDef 0x06001D99
```

---

## 11. Blocker hiện tại

Ba virtual token của method mẫu đã map xong.

Blocker còn lại:

```text
tìm writer đầu tiên tạo hoặc nạp 0x0A001D99
trước khi giá trị bị forward qua nhiều stack workspace
```

Cụ thể, nguồn hiện tại là:

```text
0xFD82557548
```

Cần tìm writer trước đó của địa chỉ này.

Kết quả cần tìm thuộc một trong các loại:

### Trường hợp tốt nhất A

```asm
mov reg32,dword ptr [non-stack address]
```

Nếu địa chỉ nằm trong vùng host/runtime blob `0x241015...`, map nó về file offset.

### Trường hợp tốt nhất B

```asm
shr reg,4
or  reg,0A000000h
```

hoặc arithmetic tương đương.

Đây sẽ xác nhận trực tiếp:

```text
0x0001D994
    ↓
RID 0x1D99
    ↓
MemberRef prefix
0x0A001D99
```

### Trường hợp C

Writer vẫn copy từ stack.

Tiếp tục theo đúng source memory, nhưng không được suy diễn stack slot là field cố định.

---

## 12. Lệnh tiếp theo nên chạy

Bắt đầu tại checkpoint nguồn `R10`:

```text
!tt A9C1E:5B1

r @$t11 = @r8 + @r10 - 0x7c7bc8a4

.printf "\n=== SLOT 3 R10 MEMORY SOURCE ===\n"
.printf "RIP=%p SOURCE=%p DWORD=%08x QWORD=%p RSP=%p\n", @rip, @$t11, dwo(@$t11), poi(@$t11), @rsp

dd (@$t11-0x10) L8
dq (@$t11-0x10) L6

dx @$slot3R10MemWriter = @$curprocess.TTD.PrevMemoryAccess("w", @$t11, 4)
dx @$slot3R10MemWriter
```

Nếu có `Position`:

```text
dx @$slot3R10MemWriter.Position.SeekTo()

.printf "\n=== SLOT 3 R10 MEMORY WRITER BEFORE ===\n"
.printf "RIP=%p TARGET=%p BEFORE_DWORD=%08x BEFORE_QWORD=%p RSP=%p\n", @rip, @$t11, dwo(@$t11), poi(@$t11), @rsp

ub @rip L20
u @rip L12

.printf "RAX=%p RBX=%p RCX=%p RDX=%p\n", @rax, @rbx, @rcx, @rdx
.printf "RBP=%p RSI=%p RDI=%p RSP=%p\n", @rbp, @rsi, @rdi, @rsp
.printf "R8=%p R9=%p R10=%p R11=%p\n", @r8, @r9, @r10, @r11
.printf "R12=%p R13=%p R14=%p R15=%p\n", @r12, @r13, @r14, @r15

dd (@$t11-0x10) L8
dq (@$t11-0x10) L6

t

.printf "\n=== SLOT 3 R10 MEMORY WRITER AFTER ===\n"
.printf "RIP=%p TARGET=%p AFTER_DWORD=%08x AFTER_QWORD=%p\n", @rip, @$t11, dwo(@$t11), poi(@$t11)
u @rip L8
```

Nếu query 4-byte không trả kết quả, thử:

```text
!tt A9C1E:5B1

r @$t11 = @r8 + @r10 - 0x7c7bc8a4

dx @$slot3R10QwordWriter = @$curprocess.TTD.PrevMemoryAccess("w", @$t11, 8)
dx @$slot3R10QwordWriter
```

---

## 13. Quy tắc thao tác TTD cần giữ

1. Không gọi liên tiếp:

```text
PrevRegisterWrite(...).Position.SeekTo()
```

hai lần.

Luôn:

```text
dx @$x = PrevRegisterWrite(...)
dx @$x
dx @$x.Position.SeekTo()
```

2. TTD data breakpoint dừng sau instruction memory access.

3. Khi cần xem writer:

```text
t-
```

một instruction, kiểm tra BEFORE, rồi `t` để replay.

4. Không dùng linear disassembly làm nguồn sự thật duy nhất vì HVMRun64 có junk/dead code và indirect control flow.

5. Mỗi effective address phải tính bằng register tại đúng TTD position của instruction đó.

6. Không dùng register từ checkpoint sau để tính EA cho instruction trước.

7. Stack address bị tái sử dụng. Luôn ghi kèm:

```text
TTD position + address + before/after
```

8. Không dùng `ba w8` bừa trên stack khi DWORD cao/thấp overlap; có thể tạo false cursor update.

9. `PrevMemoryAccess("w", address, 4)` đôi khi trả writer 8 byte overlap. Phải đọc trường `Size`.

10. Nếu `PrevMemoryAccess` trả empty object, không được gọi `.Position.SeekTo()`.

---

## 14. Mức độ hoàn thành

### Method mẫu `0x06002CD4`

```text
Khôi phục mapping ba virtual token: hoàn tất
Xác nhận real token: hoàn tất
Xác nhận packed kind 1/3/4: hoàn tất
Xác nhận token-map node layout: hoàn tất
Tìm đúng packed occurrence slot 3 trong host: chưa hoàn tất
```

Ước lượng method mẫu:

```text
khoảng 90–95%
```

### Generic offline resolver

Đã có:

- packed token format cho kind 1/3/4;
- mapping slot 1/2/3 được runtime xác nhận;
- host packed region cho slot 1;
- token map cache và insertion flow;
- kỹ thuật lần ngược stack forwarding.

Còn thiếu:

1. Exact host source của slot 3.
2. Công thức generic `virtual slot → packed entry`.
3. Kiểm chứng trên nhiều method.
4. Kind khác ngoài 1/3/4.
5. Batch resolver và patcher.
6. Xử lý collision khi cùng packed DWORD xuất hiện nhiều lần trong host.

Ước lượng generic offline resolver:

```text
khoảng 70–80% về nghiên cứu,
chưa phải 70–80% về production-ready implementation.
```

---

## 15. Tóm tắt ngắn để AI mới tiếp tục

```text
Target: LordsMobileBot.exe, DNGuard HVM 4.9.6
Method mẫu: 0x06002CD4

Virtual mappings confirmed:
0x04800001 → 0x040088ED
0x01800002 → 0x010004C9
0x0A800003 → 0x0A001D99

Packed format:
rid  = packed >> 4
kind = packed & 0xF

Confirmed kinds:
1 → TypeRef
3 → FieldDef
4 → MemberRef

Packed values:
slot1 = 0x00088ED3
slot2 = 0x00004C91
slot3 = 0x0001D994

Token map:
context     = 0x24100669560
tree object = 0x24100669618
head        = 0x24100668DA0
size addr   = 0x24100669628

Nodes:
slot1 node = 0x24100668CE0
slot2 node = 0x24100668D10
slot3 node = 0x24100668D40

Node:
+0x18 key
+0x1C token
+0x20 flags/color

Slot3 value write:
0x24100668D5C: 0 → 0x0A001D99

Current reverse dataflow:
[0xFD82557548]
 → R10
 → [0xFD825573A0]
 → R11D
 → [0xFD825575AC]
 → R10D
 → tokenMap[3]

Current next task:
Find previous writer of 0xFD82557548 at or before A9C1E:5B1.

Do not continue R13 path:
PrevRegisterWrite(r13) hit ntdll!RtlAllocateHeap pop r13, only ABI restore.

Host occurrences of slot3 packed 0x0001D994:
0x11ECE88
0x46C7A28
0x5C63E68
0x69CDF02
0x730500E

No occurrence has been selected yet.
```

---

## 16. Điều không được kết luận quá sớm

- Không gọi runtime blob là full raw EXE mapping.
- Không khẳng định `kind 5 → MethodDef` là confirmed.
- Không chọn occurrence đầu tiên của `0x0001D994`.
- Không coi stack workspace là struct ổn định.
- Không coi `0x0001D995` tại `0xB13490` là slot 3.
- Không lần source từ `R13` qua allocator restore.
- Không nói generic resolver đã hoàn tất chỉ vì method mẫu đã map đủ ba token.
