Dưới đây là bản **handoff đầy đủ để bro dán nguyên vào chat GPT mới**. Nó chỉ tập trung vào các cột mốc phát hiện **sau báo cáo HVM0 trước đó**, đồng thời giữ đủ baseline để GPT mới không phải hỏi lại từ đầu.

---

# HANDOFF — DNGuard HVM 4.9.6 / LordsMobileBot

## 1. Mục tiêu hiện tại

Target:

```text
D:\LordsBot-Release\LordsMobileBot.exe
```

Managed module:

```text
LordsMobileBot.dll
```

Runtime:

```text
HVMRun64.dll
Version 4.9.6.0 x64
Preferred base: 0x180000000
Image size: 0x3A4000
```

Mục tiêu cuối:

```text
Khôi phục HVM hoàn toàn offline từ host,
không phụ thuộc temp HVMRun64.dll hoặc runtime dump.
```

Báo cáo trước đã giải quyết phần lớn:

* tìm và decode bảng method trong host;
* giải record, metadata, payload;
* XOR + RC4;
* LZARI;
* trích xuất được method HVM offline;
* batch scan 27.296 MethodDef;
* phân loại các profile record/meta;
* xác nhận suffix IL của method `0x06002CD4`.

Phần dưới đây là các phát hiện mới sau báo cáo.

---

# 2. Method đang phân tích chính

```text
Method token: 0x06002CD4
RID:          0x2CD4
```

Payload giải mã được trước đây:

```text
2B 05 28 9D 70 15 41 00
28 04 00 80 0A
28 05 00 80 0A
0A
38 00 00 00 00
06
2A
```

Đây là suffix dài `0x1A` byte, không phải toàn bộ final IL.

Final JIT IL runtime có:

```text
Pointer: 0x2410066A290
Size:    0x2E
```

Toàn bộ bytes:

```text
00
7F 01 00 80 04
FE 16 02 00 80 01
6F 03 00 80 0A
2D 01
00
2B 05
28 9D 70 15 41
00
28 04 00 80 0A
28 05 00 80 0A
0A
38 00 00 00 00
06
2A
```

Chia thành:

```text
prefix: 20 byte
suffix: 26 byte
```

Prefix:

```text
00 7F 01 00 80 04
FE 16 02 00 80 01
6F 03 00 80 0A
2D 01 00
```

Suffix chính là RC4 output trước đó.

Runtime copy suffix trực tiếp tới:

```text
finalIL + 0x14
RIP HVMRun64+0x3948BF
```

Prefix được tạo động tại:

```text
0x24100669774
```

rồi copy 20 byte vào đầu final IL. 

---

# 3. Virtual tokens trong prefix

Ba writer đã xác định:

```text
prefix+0x02 = 0x04800001
RIP 0x18028A166

prefix+0x08 = 0x01800002
RIP 0x1802AA0ED

prefix+0x0D = 0x0A800003
RIP 0x18037527C
```

Các token có dạng:

```text
category table
| 0x00800000 marker
| slot
```

Không được xóa bit marker rồi coi phần còn lại là metadata RID thật.

Ví dụ:

```text
0x04800001 không phải FieldDef RID 1
```

Nó là virtual field token, slot `1`.

---

# 4. ResolveToken proxy của HVM

Outer CLR request cho:

```text
virtual token = 0x04800001
kind          = 4
request ptr   = 0xFD8257B9D0
```

Proxy HVM entry:

```text
0x18003EE80
```

Logic nhánh:

```c
if (token & 0x00800000)
    HvmCustomResolveToken();
else
    OriginalCoreCLRResolveToken();
```

Custom resolver call tại:

```text
0x18003EEE2
```

Arguments:

```text
RCX = 0x2410066E720
EDX = virtual token
R8  = caller return address
```

Outer request không bị thay token. HVM điền trực tiếp kết quả handle:

```text
class  = 0x7FFA7500C700
method = 0
field  = 0x7FFA74FC4198
```

Observed request layout:

```c
struct ResolvedTokenLike {
    void* context;       // +0x00
    void* scope;         // +0x08
    uint32_t token;      // +0x10
    uint32_t tokenKind;  // +0x14
    void* classHandle;   // +0x18
    void* methodHandle;  // +0x20
    void* fieldHandle;   // +0x28
};
```



---

# 5. Exact virtual-token mapping đã chứng minh

Một CoreCLR `CEEInfo::resolveToken` thật được gọi với:

```text
request ptr = 0xFD8257B080
token       = 0x040088ED
kind        = 4
scope       = LordsMobileBot.dll
```

CoreCLR trả về đúng:

```text
class = 0x7FFA7500C700
field = 0x7FFA74FC4198
```

Đây là cùng kết quả với request virtual token `0x04800001`.

Do đó mapping đã được chứng minh:

```text
0x04800001 → 0x040088ED
```

Không còn là suy đoán.

SOS:

```text
MethodTable: 0x7FFA7500C700
EEClass:     0x7FFA74FD0980
Module:      0x7FFA74E9E0A0
Type token:  0x02000F3E
Assembly:    LordsMobileBot.dll
```



---

# 6. Token XOR mask trước CoreCLR

Descriptor:

```text
0x24100666840
```

Mask:

```text
[descriptor+0x30] = 0x6A714B62
```

Template request ban đầu chứa:

```text
encoded token = 0x6E71C38F
```

Công thức:

```text
0x6E71C38F XOR 0x6A714B62 = 0x040088ED
```

HVM tạo request encoded bằng:

```asm
mov r8d,[descriptor+30h]
xor r8d,r14d
mov [request.token],r8d
```

Ngay trước gọi CoreCLR:

```asm
mov r9d,[descriptor+30h]
xor [request.token],r9d
call OriginalCoreCLRResolveToken
```

Công thức tổng quát:

```c
encodedToken = realToken ^ tokenXorMask;
realToken    = encodedToken ^ tokenXorMask;
```



---

# 7. Slot-to-real-token map

Virtual slot được lấy bằng:

```c
slot = virtualToken & 0x000FFFFF;
```

Với:

```text
virtual token = 0x04800001
slot          = 1
```

Actual path:

```asm
mov r13d,edi
and r13d,0x000FFFFF
mov edx,r13d
mov rcx,[rbx+0x2E0]
call HVMRun64+0x5730
mov r14d,eax
```

Call:

```text
RCX = context 0x24100669560
EDX = slot 1
```

Return:

```text
EAX = 0x040088ED
```

Helper `HVMRun64+0x5730` là ordered-tree lookup, rất giống:

```cpp
std::map<uint32_t,uint32_t>
```

Tree object:

```text
context+0xB8 = 0x24100669618
```

Head/sentinel:

```text
[tree+8] = 0x24100668DA0
```

Matched node:

```text
0x24100668CE0
```

Node layout quan sát được:

```c
struct TokenMapNode {
    void* left;          // +0x00
    void* parent;        // +0x08
    void* right;         // +0x10
    uint32_t slot;       // +0x18
    uint32_t realToken;  // +0x1C
    uint8_t color;       // +0x20
    uint8_t isNil;       // +0x21
};
```

Node:

```text
slot      = 1
realToken = 0x040088ED
```

Helper trả về:

```asm
mov eax,[rbx+0x1C]
```



---

# 8. Node được tạo `{1,0}`, sau đó mới điền token

Node key writer:

```text
TTD A9BE9:1C70
RIP 0x180001BA1
```

Instruction:

```asm
mov qword ptr [r11+0x18],rcx
```

Registers:

```text
R11 = node 0x24100668CE0
RCX = 1
```

Nó ghi qword:

```text
node+0x18:
key   = 1
value = 0
```

Sau đó value writer tại:

```text
TTD A9BEF:1417
RIP 0x1803525B9
```

Instruction:

```asm
mov dword ptr [rbp+rax*8-0xB8],edx
```

Registers:

```text
RBP = 0x24100668CFC
RAX = 0x17
EDX = 0x040088ED
```

Địa chỉ hiệu dụng chính xác:

```text
0x24100668CFC = node+0x1C
```

Tức:

```c
mapNode->realToken = 0x040088ED;
```

Runtime map lưu clear token, không lưu virtual token hoặc encoded token. 

---

# 9. Chuỗi trace ngược từ node value writer

Đã trace rất sâu qua nhiều stack slot và register forwarding.

Kết quả quan trọng:

```text
map[1] = 0x040088ED
```

được đưa tới writer cuối qua một chuỗi stack slot bị obfuscate.

Nhiều lệnh chỉ là:

```text
mov register,[stack]
mov [stack],register
push/pop
generic stack copier
```

Không phải nơi tạo token.

Phải luôn phân biệt:

```text
BEFORE != AFTER → writer có ý nghĩa
BEFORE == AFTER → ghi lặp
```

---

# 10. Full token và RID tồn tại riêng

Đã chứng minh runtime giữ đồng thời:

```text
full token = 0x040088ED
RID        = 0x000088ED
```

Một cặp stack quan sát được:

```text
0xFD825573C8 = 0x040088ED
0xFD825573D0 = 0x000088ED
```

Writer full token:

```text
TTD A9BE6:1812
RIP 0x1802B4DEA
```

```asm
mov qword ptr [r8+rax+0x69BB],r10
```

Registers:

```text
R8  = 0xFD825573C8
RAX = -0x69BB
R10 = 0x040088ED
```

Writer RID:

```text
TTD A9BE6:1B45
RIP 0x180317DD3
```

```asm
mov qword ptr [rcx+rsi*2-0x7A],r8
```

Registers:

```text
RCX = 0xFD825573D0
RSI = 0x3D
R8  = 0x000088ED
```

Hai giá trị được forward độc lập.

Chưa chứng minh chúng được tạo bằng:

```c
fullToken = rid | 0x04000000;
```

Có thể cả hai được đọc từ nguồn đã chuẩn bị trước. 

---

# 11. Generic stack copier đã nhận diện

Một routine xuất hiện nhiều lần:

```text
RIP 0x1802B25DA
```

```asm
mov r10,qword ptr [rdx]
...
mov qword ptr [destination],r10
```

Nó chỉ forward dữ liệu.

Ví dụ RID:

```text
[0xFD825573B0] = 0x88ED
    ↓
R10 = 0x88ED
    ↓
[0xFD82557598] = 0x88ED
```

Ví dụ full token:

```text
[0xFD825573C8] = 0x040088ED
    ↓
R10 = 0x040088ED
    ↓
[0xFD82557530] = 0x040088ED
```

Không nên tiếp tục phân tích semantic của generic copier.

---

# 12. Chuỗi RID đã trace được gần nguồn

Chuỗi RID hiện tại:

```text
[0xFD82557550] = 0x88ED
        ↓
RDX = 0x88ED
        ↓
[0xFD82557360] = 0x88ED
        ↓
RDX = 0x88ED
        ↓
[0xFD82557548] = 0x88ED
        ↓
R8 = 0x88ED
        ↓
[0xFD825573B0] = 0x88ED
        ↓
R10 / generic copier
        ↓
[0xFD82557598] = 0x88ED
        ↓
R8
        ↓
[0xFD825573D0] = 0x88ED
```

Các writer quan trọng đã xác nhận:

```text
0xFD825573B0:
TTD A9BE5:45
RIP 0x1802EBF66
source R8 = 0x88ED
BEFORE pointer
AFTER  0x88ED
```

```text
0xFD82557548:
TTD A9BE4:40
RIP 0x1803504B7
source RDX = 0x88ED
BEFORE 1
AFTER  0x88ED
```

```text
0xFD82557360:
TTD A9BE3:19E5
RIP 0x1801F2249
source RDX = 0x88ED
BEFORE pointer
AFTER  0x88ED
```

---

# 13. Stack-slot reuse rất quan trọng

Cùng một địa chỉ stack được dùng cho nhiều giá trị ở các lifetime khác nhau.

Ví dụ:

```text
0xFD82557550
```

Ở giai đoạn sớm:

```text
chứa RID 0x000088ED
```

Ở giai đoạn muộn hơn:

```text
chứa full token 0x040088ED
```

Do đó không được đặt tên cố định cho stack address.

Luôn phải ghi kèm:

```text
TTD position + address + before/after
```

Ví dụ đúng:

```text
A9BE2:1493 — [0xFD82557550] = 0x88ED
```

Không nên chỉ ghi:

```text
stack slot 0xFD82557550 là RID
```

---

# 14. Phát hiện mới nhất — điểm đang dừng

Position hiện tại quan trọng nhất:

```text
TTD A9BE2:1493
RIP 0x18033491D
```

Instruction:

```asm
mov qword ptr [r10+r11*4],rax
```

Registers:

```text
R10 = 0xFD82557550
R11 = 0
RAX = 0x000088ED
```

Tương đương:

```asm
mov qword ptr [0xFD82557550],0x000088ED
```

Trạng thái:

```text
BEFORE = 0xFD82557420
AFTER  = 0x000088ED
```

Đây là writer khởi tạo có ý nghĩa của lifetime RID tại `0xFD82557550`.

Nguồn tiếp theo cần trace là:

```text
RAX tại A9BE2:1493
```

Đây hiện là điểm gần nguồn thật nhất.

---

# 15. Việc GPT mới cần làm tiếp ngay

Chạy từ exact position:

```text
!tt A9BE2:1493

.printf "RAX_RID_START RIP=%p RAX=%p EAX=%08x RSP=%p\n", @rip, @rax, @eax, @rsp

dx @$curthread.TTD.PrevRegisterWrite("rax")
```

Seek đúng một lần:

```text
dx @$curthread.TTD.PrevRegisterWrite("rax").Position.SeekTo()
```

Sau đó:

```text
.printf "RAX_RID_SOURCE RIP=%p RAX_BEFORE=%p EAX_BEFORE=%08x RSP=%p TOP=%p\n", @rip, @rax, @eax, @rsp, poi(@rsp)

ub @rip L20
u @rip L20

.printf "RAX=%p RBX=%p RCX=%p RDX=%p RBP=%p RSI=%p RDI=%p R8=%p R9=%p R10=%p R11=%p R12=%p R13=%p R14=%p R15=%p\n", @rax, @rbx, @rcx, @rdx, @rbp, @rsi, @rdi, @r8, @r9, @r10, @r11, @r12, @r13, @r14, @r15

t

.printf "RAX_RID_AFTER=%p EAX_AFTER=%08x RIP=%p\n", @rax, @eax, @rip
```

Mục tiêu:

```text
RAX_BEFORE != 0x88ED
RAX_AFTER  == 0x88ED
```

Nếu lệnh là:

```asm
mov rax,[memory]
```

thì tính EA và truy writer của memory.

Nếu lệnh là:

```asm
mov eax,<reg>
and eax,0x00FFFFFF
```

thì có thể đã gặp điểm tách RID.

Nếu lệnh là:

```asm
mov eax,0x88ED
```

thì cần reverse actual control flow để xem constant xuất phát từ đâu.

Nếu là return từ helper:

```asm
call ...
```

và sau call `RAX=0x88ED`, cần vào helper hoặc truy nơi helper set `EAX`.

---

# 16. Quy tắc thao tác TTD bắt buộc

Không gọi:

```text
PrevRegisterWrite(...).Position.SeekTo()
```

hai lần liên tiếp.

Lần thứ hai sẽ tìm writer cũ hơn từ position mới, gây nhầm.

Không dùng linear disassembly để suy đoán control flow. DNGuard có rất nhiều dead/junk instructions.

Ưu tiên:

```text
t-
PrevRegisterWrite
PrevMemoryAccess
exact !tt position
```

Không ưu tiên:

```text
p-
g-
k
linear neighboring instructions
```

Khi reverse path bị breakpoint hoặc CLR exception làm nhiễu:

```text
bc *
sxi 0xe0434352
```

Dùng `t-` từng bước.

Pseudo-register hợp lệ chỉ:

```text
@$t0 ... @$t19
```

---

# 17. Các vấn đề vẫn chưa giải quyết

Chưa biết chính xác nguồn semantic tạo:

```text
RID 0x88ED
```

Cần tiếp tục từ `RAX` tại `A9BE2:1493`.

Chưa biết runtime:

```text
tách RID từ 0x040088ED
```

hay:

```text
đọc RID và full token từ hai nguồn riêng.
```

Chưa giải virtual token:

```text
0x01800002
0x0A800003
```

Chưa xác định ý nghĩa low-28 payload của aux item type 5.

Chưa có offline reconstruction tổng quát của:

```text
virtual slot → real metadata token map
```

Đây là blocker chính để dựng prefix IL hoàn toàn offline.

---

# 18. Kết luận hiện tại

Đã chứng minh chắc chắn:

```text
virtual token 0x04800001
slot          1
real token    0x040088ED
RID           0x000088ED
```

Runtime flow:

```text
virtual IL token
    ↓ extract slot
slot 1
    ↓ std::map lookup
real token 0x040088ED
    ↓ XOR encode/decode quanh CoreCLR request
CoreCLR resolveToken
    ↓
class/field handles
```

Map runtime:

```text
map[1] = 0x040088ED
```

Điểm trace đang dừng:

```text
A9BE2:1493
RIP 0x18033491D
RAX = 0x88ED
[0xFD82557550] ← RAX
```

**Bước kế tiếp duy nhất nên làm:**

```text
PrevRegisterWrite("rax") từ A9BE2:1493
```

Không tiếp tục truy các generic stack copier đã nhận diện.
