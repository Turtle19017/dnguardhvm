# BƯỚC AK.20 — Virtual KEY là số thứ tự per-method, và join metadata ↔ IL

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Rút lại hai phát biểu của peer review trước AK.19

### RETRACTED — "call `[B]` có token thật ⇒ mục tiêu A gần xong bằng một hook"

AK.19 cho thấy method `0x060008E1` đi vào `compileMethod` với sáu virtual token còn nguyên:

```text
0x04800001
0x01800002
0x0A800003
0x06800004
0x06800005
0x0A800006
```

Phát biểu cũ được suy từ đúng một method (`[B]`), và `[B]` là bootstrap của HVM chứ không phải method người dùng. Đây đúng là lỗi lấy mẫu đã được cảnh báo trong AK.11. Cảnh báo của wwh1004 về token bị mã hoá **vẫn áp dụng** cho build này.

### RETRACTED — "buffer 20 byte không phải code thật"

AK.19 chứng minh 20 byte đó khớp byte-for-byte với `IL[0x00,0x14)` của body `0x2D` byte của `0x060008E1`. AK.2–AK.10 đã trace đúng phần đầu của một method thật.

### RETRACTED — hai hằng đặt trước trong AK.15A

```text
dự đoán [@rdx]      = 0x180035100   sai, thật là 0x1800739E8
dự đoán [vtable+E0] = 0x180007850   sai, thật là 0x18003EE80
```

Hai giá trị này lấy từ sổ chứng cứ cũ mà không kiểm lại. AK.16 rút đúng.

## Câu hỏi của bước này

1. Virtual KEY được đánh số theo phạm vi nào?
2. Có nối được record metadata với method RID không?
3. `[R8+0x20]` là trường gì?
4. Vì sao AK.17 không thể tìm thấy slot vtable?

## Quan sát chưa được ghi nhận trong AK.19

### KEY là số thứ tự 1-based trong từng method

Sáu virtual token của `0x060008E1` có KEY đúng bằng `1, 2, 3, 4, 5, 6` theo thứ tự xuất hiện trong IL. Không ngắt quãng, bắt đầu từ 1.

```text
IL_0001  ldsflda      0x04800001   KEY 1
IL_0006  constrained. 0x01800002   KEY 2
IL_000C  callvirt     0x0A800003   KEY 3
IL_001B  call         0x06800004   KEY 4
IL_0020  call         0x06800005   KEY 5
IL_0026  call         0x0A800006   KEY 6
```

Vậy KEY không phải chỉ số vào một bảng toàn cục. Giả thuyết: KEY là **chỉ số 1-based vào mảng `items[]` của chính record method đó**.

```c
struct Record { u8 maxStack; u24 codeSize; u16 itemBytes; u16 ehCount;
                u16 itemCount; u16 ehDataSize; u32 items[itemCount]; ... };
```

Trung bình `48324 / 10960 = 4.41` item mỗi record, nên `itemCount = 6` là hợp lý.

Nếu đúng, đây là khớp nối metadata ↔ IL còn thiếu từ BƯỚC AI, và mọi token của mọi method tái tạo được **offline** từ metadata, không cần chạy chương trình.

### `[R8+0x20]` là `EHcount`

Layout `CORINFO_METHOD_INFO`:

```text
+0x00 ftn        +0x18 ILCodeSize   +0x24 options
+0x08 scope      +0x1C maxStack     +0x28 regionKind
+0x10 ILCode     +0x20 EHcount
```

AK.14 đã in `[R8+0x20] = 1` cho `[B]` nhưng chưa gán nhãn. Decode của AK.16 cho `[B]` có `leave.s / pop / leave.s`, tức một khối try/catch, khớp `EHcount = 1`.

Đã có sẵn đối chứng dương và âm:

```text
[B]          có try/catch  -> EHcount phải = 1   (đã quan sát)
0x060008E1   không có leave -> EHcount phải = 0   (chưa dump)
```

Một lệnh `dd @r8+18 L4` xác nhận cả layout và mở đường tới `getEHinfo`.

### Vì sao AK.17 không thể tìm thấy gì

Hai lý do độc lập.

**Một.** Trong chính dữ liệu AK.19, tại entry `0x180007850`:

```text
[RSP+0x00] = 0x18003EEE7
```

Địa chỉ trả về nằm trong `HVMRun64`, không phải `clrjit`. Nên `0x180007850` là helper nội bộ do code HVM gọi, **không phải slot vtable**. Search nó trong vtable không thể trúng.

**Hai.** Phạm vi search quá nhỏ. `L400` = 1024 byte = 128 slot, trong khi `ICorJitInfo` (gồm `ICorStaticInfo` và `ICorDynamicInfo`) có khoảng 170–180 method ảo. Cần ít nhất `L1000`.

Slot vtable thật nằm một frame phía trên — hàm mà clrjit gọi trực tiếp.

### `[B]` giải nghĩa được, và nó giải thích prefix

```text
try {
  Field_040088ED = new Type_0A001D9C(0x000002416A17DE02)
  Field_040088EE = 0x00000001800093E0     // trong HVMRun64, sát VMRuntime base 0x180009D80
} catch { }
```

`0x040088ED` chính là field mà prologue của method bảo vệ làm `ldsflda 0x04800001` lên. Vậy prefix 20 byte là **guard khởi tạo runtime**, và `[B]` là cái cài field đó. Đây là bằng chứng ngữ nghĩa cho "prefix là template dùng chung", mạnh hơn việc chỉ khớp byte trên một mẫu.

## Điểm chiến lược

```text
82 site43 calls / 10960 records = 0.75%
```

Runtime oracle sẽ không bao giờ liệt kê hết corpus, kể cả chạy nhiều lần. Do đó **join qua metadata là con đường duy nhất tới mục tiêu offline**, và AK.19 vừa đưa khoá join. Vì vậy bước này đảo thứ tự ưu tiên: đọc `methods.csv` quan trọng hơn đọc debugger.

## Nhánh A — join metadata (Python, không cần debugger)

Dữ liệu đã có tại `C:\hvm\methods.csv`.

```text
1. Record ở index 2272 và 2273 có gì?
2. Bao nhiêu record thoả codeSize==0x2D & maxStack==8 & ehCount==0 & itemCount==6?
3. Với record khớp: in 6 items[] dạng (tag, low24)
```

Quét đồng thời cả hai giả thuyết về `codeSize`:

```text
H1  codeSize == 0x2D   -> record lưu độ dài full body, prefix được lưu
H2  codeSize == 0x19   -> record lưu suffix riêng, prefix được sinh lúc chạy
```

Chỉ một trong hai đúng, và biết cái nào cho ta biết prefix được lưu hay được sinh.

### Phán quyết đặt trước

- Record `[2272]` hoặc `[2273]` khớp cả bốn trường → **RID ↔ record index CONFIRMED**, mô hình `KEY = index vào items[]` lên STRONG.
- Đúng một record khớp bốn trường nhưng ở index khác → có ánh xạ nhưng không phải identity; đo độ lệch rồi kiểm lại bằng mẫu thứ hai từ nhánh C.
- Nhiều hơn một record khớp → bộ bốn trường không đủ làm khoá; chờ thêm mẫu từ nhánh C trước khi kết luận.
- Không record nào khớp ở cả H1 và H2 → record không index theo RID; phải tìm bảng ánh xạ riêng.

## Nhánh B — đóng resolver và tìm slot vtable thật

Từ `11A7E4:8EC`:

```text
dps @rsp L20
```

Tìm địa chỉ trả về đầu tiên nằm trong `[0x7FFAD4E10000, 0x7FFAD4F10000)`. Địa chỉ HVM ngay dưới nó nằm trong hàm là slot vtable. Lấy base bằng `ln`, rồi:

```text
s -q 0x1800739A8 L1000 <base_ham>
s -q 0x1800739E8 L1000 <base_ham>
? <dia_chi_match> - 0x1800739A8
```

Chia 8 ra chỉ số slot, đối chiếu thứ tự `ICorJitInfo` để gọi đúng tên. Dùng `L1000`, không dùng `L400`.

Bắt output của resolver:

```text
bp 0x18003EEE7
g
r rax
dq @rsp L8
s -d @rsp L200 040088ed
```

Đặt trước: `RAX` hoặc một slot stack chứa `0x040088ED`.

Lấy nốt năm key còn lại:

```text
bp 0x180007850
g     $$ r rdx  ky vong 0x01800002
g     $$ r rdx  ky vong 0x0A800003
g     $$ r rdx  ky vong 0x06800004
g     $$ r rdx  ky vong 0x06800005
g     $$ r rdx  ky vong 0x0A800006
```

### Phán quyết

- Đến đúng thứ tự 1→6 → **KEY = số thứ tự theo IL** CONFIRMED.
- `0x5F39B97D` cũng xuất hiện → nó không phải junk và clrjit resolve cả dead code. Ghi lại, không bỏ qua.
- Thiếu key nào → clrjit lazy-resolve. Không kết luận thiếu là không tồn tại.

## Nhánh C — năm mẫu còn lại, để nuôi nhánh A

Các vị trí đã enumerate ở AK.18:

```text
121937:1754
129170:768
144D1C:1E0A
162182:14E4
164BB7:8A2
```

Tại mỗi vị trí, đúng năm lệnh:

```text
!tt <vi_tri_that>
r rdx,r8
dq @r8 L3
dd @r8+18 L4
db poi(@r8+10) L<SIZE_THAT>
!dumpmd poi(@r8)
```

`dd @r8+18 L4` lấy một lượt `ILCodeSize`, `maxStack`, `EHcount`, `options`.

Ghi cho mỗi mẫu: `mdToken`, `codeSize`, `maxStack`, `EHcount`, số virtual key, KEY lớn nhất, có prefix 20 byte hay không.

### Phán quyết

- Cả 5 mở đầu bằng đúng 20 byte prefix → template dùng chung CONFIRMED, và nhánh A phải test cả H1 lẫn H2.
- KEY của mỗi mẫu chạy liên tục `1..N` theo thứ tự IL → mô hình per-method ordinal CONFIRMED trên 6 mẫu.
- Mẫu nào có token thật như `[B]` → không phải mọi arena body là user method; cần tách thêm một lớp nữa.

Sáu bộ `(mdToken, codeSize, maxStack, ehCount, itemCount)` là đủ để đóng ánh xạ record ↔ RID ở nhánh A. Vì vậy nhánh C chạy trước nhánh B nếu phải chọn.

## Backlog

- Consumer của buffer `0x24100669774`: AK.19 đã trả lời gián tiếp — nó là staging của prefix, và prefix là template. Không còn giá trị phân biệt.
- Wide static census `[0x180007000, 0x180040000)`: vẫn nên chạy để đóng con số `0x444`, nhưng đã tụt hạng. Nếu nhánh A đúng thì ISA của stream tĩnh không còn nằm trên đường tới mục tiêu offline.

---

## AK.20.1 — Kết quả đo trực tiếp: row-index identity bị bác, EH layout đóng, proxy wrapper được định danh

### `methods.csv` hiện tại chỉ là bảng tóm tắt

Schema thực tế:

```text
recOff
maxStack
codeSize
nLocals
ehCount
ilOffset
```

Tổng số dòng:

```text
10960
```

File này **không có** `itemCount`, `itemBytes` hoặc `items[]`. Vì vậy nó đủ để lọc candidate record theo hình dạng, nhưng chưa thể tự nó kiểm chứng phép nối:

```text
virtual KEY k -> items[k-1]
```

Muốn đóng phép nối đó phải đọc record đầy đủ từ metadata đã giải hoặc mở rộng exporter.

### Row 2272 và 2273 không phải identity mapping của RID `0x8E1`

```text
row[2272]:
  recOff   = 0x150c4
  maxStack = 4
  codeSize = 0x4e
  nLocals  = 0
  ehCount  = 0
  ilOffset = 0xffdec

row[2273]:
  recOff   = 0x150d0
  maxStack = 3
  codeSize = 0x3c
  nLocals  = 2
  ehCount  = 0
  ilOffset = 0xffe3a
```

Trong khi method `0x060008E1` tại JIT entry có:

```text
ILCodeSize = 0x2D
maxStack   = 8
EHcount    = 0
```

### CONFIRMED

- `methods.csv` có đúng `10960` records.
- `row[2272]` và `row[2273]` không khớp method `0x060008E1` theo `codeSize` hoặc `maxStack`.
- Giả thuyết đơn giản `RID == row index` và hai biến thể off-by-one bị **RETRACTED**.

### UNPROVEN

- Record của `0x060008E1` nằm ở row nào.
- `codeSize` record lưu full body `0x2D` hay suffix `0x19`.
- `nLocals` của record tương ứng; IL dùng local 0 nhưng record có thể chứa thêm local không dùng.

### Quét candidate tiếp theo

```powershell
function H([string]$s) { [Convert]::ToInt32(($s -replace '^0x',''), 16) }

$h1 = $rows | Where-Object {
    (H $_.codeSize) -eq 0x2d -and
    [int]$_.maxStack -eq 8 -and
    [int]$_.ehCount -eq 0
}

$h2 = $rows | Where-Object {
    (H $_.codeSize) -eq 0x19 -and
    [int]$_.maxStack -eq 8 -and
    [int]$_.ehCount -eq 0
}

"H1 full-body candidates = $($h1.Count)"
$h1 | Select-Object recOff,maxStack,codeSize,nLocals,ehCount,ilOffset | Format-Table -AutoSize

"H2 suffix candidates = $($h2.Count)"
$h2 | Select-Object recOff,maxStack,codeSize,nLocals,ehCount,ilOffset | Format-Table -AutoSize
```

Không dùng `itemCount=6` trong lần lọc này vì CSV hiện tại không chứa trường đó.

## `CORINFO_METHOD_INFO` layout được xác nhận trên sample S1

Tại `11A7E3:D6C`:

```text
[R8+0x18] = 0x2D ; ILCodeSize
[R8+0x1C] = 0x08 ; maxStack
[R8+0x20] = 0x00 ; EHcount
[R8+0x24] = 0x00 ; options
```

Kết hợp với sample `[B]`, nơi `[R8+0x20] = 1`, đây là đối chứng dương/âm trực tiếp cho layout đang dùng.

### CONFIRMED

```text
CORINFO_METHOD_INFO+0x18 = ILCodeSize
CORINFO_METHOD_INFO+0x1C = maxStack
CORINFO_METHOD_INFO+0x20 = EHcount
CORINFO_METHOD_INFO+0x24 = options
```

cho các sample đã đo trong trace này.

## Slot `+0xE0` là wrapper của đường token resolution

Disassembly tại `0x18003EE80`:

```text
0x18003EE94 mov rdi,rdx
0x18003EE97 mov rbx,rcx
...
0x18003EEA5 mov r8d,[rdi+10h]
0x18003EEA9 mov eax,r8d
0x18003EEAC and eax,0FF000000h
0x18003EEB1 cmp eax,70000000h
...
0x18003EEBF and r8d,800000h
0x18003EEC6 je  0x18003EEE9

; virtual-token path
0x18003EEC8 mov [rbx+30h],14h
0x18003EECF mov [rbx+290h],rdi
0x18003EEDB lea rcx,[rbx+8]
0x18003EEDF mov edx,[rdi+10h]
0x18003EEE2 call 0x180007850
0x18003EEE7 jmp  0x18003EEFD

; non-virtual/delegation path
0x18003EEE9 mov rcx,[rbx+328h]
0x18003EEF0 mov rax,[rcx]
0x18003EEF3 mov rdx,rdi
0x18003EEF6 call qword ptr [rax+0E0h]
```

### CONFIRMED

- Vtable entry `0x18003EE80` là proxy wrapper của đường xử lý token.
- Input structure nằm ở `RDX`; token DWORD được đọc tại `[RDX+0x10]`.
- Wrapper kiểm virtual bit `0x00800000`.
- Token ảo được chuyển cho helper `0x180007850` với:

```text
RCX = proxy + 8
EDX = virtual token
```

- Token không ảo được delegate tới object nền tại `[proxy+0x328]`, cùng slot `+0xE0`.
- `0x180007850` là helper nội bộ, còn `0x18003EE80` mới là direct proxy-vtable callback.

### RETRACTED

Lập luận cũ:

```text
[vtable+0xE0] != 0x180007850
-> slot +0xE0 không thuộc token-resolution path
```

là sai tầng. Giá trị thật tạo chuỗi:

```text
vtable +0xE0 -> wrapper 0x18003EE80
                  -> helper 0x180007850
                  -> return 0x18003EEE7
```

Về chức năng, wrapper này là token-resolution callback; tên interface chính xác `ICorJitInfo::resolveToken` phù hợp mạnh với ABI và việc token nằm tại structure offset `+0x10`.

## Output không nằm trong `RAX`, stack hoặc proxy region đã search

Tại continuation `0x18003EEE7` sau helper:

```text
RAX = 5
RDI = 0xFD8257ADD0 ; input/output structure pointer giữ từ wrapper RDX
```

Không tìm thấy `0x040088ED` trong:

```text
[RSP, RSP+0x100)
[proxy, proxy+0x200)
```

### RETRACTED

Dự đoán đặt trước:

```text
RAX hoặc stack slot sẽ chứa real token 0x040088ED
```

không khớp lần resolve đầu tiên.

Điều này không bác mapping. Callback có thể điền resolved handles vào structure ở `RDI` mà không thay token DWORD bằng CLR metadata token thật.

### Bước phân biệt tiếp theo: so sánh structure trước/sau helper

Tại vị trí hiện tại `11CA36:26C`:

```text
dq @rdi L8
dd @rdi L10
s -d @rdi L80 040088ed
```

So sánh cùng structure tại helper entry và return:

```text
!tt 11A7E4:8EC
dq 0x000000fd8257add0 L8
dd 0x000000fd8257add0 L10

!tt 11CA36:26C
dq 0x000000fd8257add0 L8
dd 0x000000fd8257add0 L10
```

Liệt kê exact writes trong helper window:

```text
dx @$pa20 = @$create("Debugger.Models.TTD.Position",0x11A7E4,0x8EC)
dx @$pb20 = @$create("Debugger.Models.TTD.Position",0x11CA36,0x26C)
dx @$wrt20 = @$cursession.TTD.MemoryForPositionRange(0x000000fd8257add0,0x000000fd8257ae10,"w",@$pa20,@$pb20)
dx @$wrt20.Count()
```

Chỉ khi count dương:

```text
dx -g @$wrt20
```

Mục tiêu là xác định callback điền `hClass`, `hMethod`, `hField` hoặc field nào khác trong resolved-token structure; không tiếp tục mặc định rằng real metadata token phải xuất hiện nguyên dạng trong output.