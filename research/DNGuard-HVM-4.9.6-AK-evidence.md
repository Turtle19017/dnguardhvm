# DNGuard HVM 4.9.6 — Evidence Log từ BƯỚC AK

Mẫu đang phân tích: `LordsMobileBot.exe`

File này chỉ ghi các bước từ **AK trở đi**. Không nhập lại handoff cũ từ Notion để tránh trộn bằng chứng mới với những kết luận đã bị rút lại.

## Quy ước mức độ

- **CONFIRMED**: có dataflow, disassembly, validator khép kín hoặc số học trực tiếp.
- **STRONG**: nhiều chứng cứ độc lập cùng hướng nhưng còn mô hình thay thế hợp lý.
- **UNPROVEN**: giả thuyết làm việc, chưa có phép thử phân biệt.
- **RETRACTED**: đã bị dữ liệu mới hoặc lỗi phương pháp bác bỏ.

Mỗi bước phải ghi: câu hỏi, giả thuyết đặt trước, lệnh, đối chứng dương, dữ liệu thô, phân tích, kết luận, artifact và bước phân biệt tiếp theo.

---

# BƯỚC AK.1 — Kiểm tra parser payload và continuity của reverse stream

## Câu hỏi

1. Header payload có phải `[u8 kind][u24 len][u32 s2off][body align8(len)]` không?
2. Dải static stream quanh `HVMRun64+0x7CC5` có tiếp tục liền mạch trước và sau transaction tạo opcode `0x7F` không?

## Giả thuyết đặt trước

- **H1:** scanner cũ bỏ entry có `len >= 0x100`.
- **H2:** tồn tại suffix chain `kind=8` kết thúc đúng EOF với `next = p + 8 + align8(u24_len)`.
- **H3:** các read trước `0x180007CCE` và sau `0x180007CC5` tiếp tục phủ dải địa chỉ giảm dần.

## AK-OFFLINE — dữ liệu thô

```text
=== HEADER CANDIDATES ===
all_valid_header    : 7092
len_lt_100          : 7092
len_lt_10000        : 7092

Ví dụ len >= 0x100:
<không có>

=== EXACT KIND-8 SUFFIX CHAINS ===
số vị trí có thể bắt đầu suffix: 0

RuntimeError: Không có chuỗi kind=8 nào kết thúc đúng EOF.
```

## Kết luận AK-OFFLINE

### RETRACTED

- `b1` chắc chắn là byte thấp của `u24 len`.
- Scanner cũ bỏ gần 4.000 method chỉ vì lọc `len < 256`.
- `next = p + 8 + align8(len)` là grammar tổng quát của payload.

### CONFIRMED

- Validator hiện tại tìm được `7.092` candidate `kind=8` trên lưới 8.
- Cả `7.092/7.092` candidate đều có hai byte `p+2,p+3` bằng 0; không tồn tại population `kind=8` có `u24 >= 0x100`.

### UNPROVEN

- Ý nghĩa thật của `b1`.
- Candidate `kind=8/3` có phải top-level method directory không.
- Quan hệ `10.960 S0 record` với candidate payload.

Không dùng các file `pl_dir.csv`, `pl_dir2.csv`, `dir_full.csv` để suy body boundary.

## AK-RUNTIME — dữ liệu thô

```text
A9BDC:1815  CD3 size4
A9BDC:183D  CD2 size1
A9BDC:1860  CCE size4
A9BDC:188D  CCA size4
A9BDC:18B7  CC6 size4
A9BDC:18CC  CC5 size1
A9BDC:18F9  CC4 size1
A9BDC:1918  CC0 size4
A9BDC:1947  CB8 size8
A9BDC:1960  CB4 size4
A9BDC:1990  CB0 size4
A9BDC:19D2  CAC size4
A9BDC:1A04  CA8 size4
A9BDC:1A1E  CA7 size1
```

Invariant:

```text
union           = [0x180007CA7, 0x180007CD7)
length          = 0x30 = 48 byte
sum(read sizes) = 48 byte
gap             = 0
overlap         = 0
direction       = strictly descending
```

## Kết luận AK-RUNTIME

### CONFIRMED

- Một static encoded stream dài ít nhất 48 byte được tiêu thụ liên tục theo địa chỉ giảm.
- Đây không phải các constant rời rạc tình cờ nằm cạnh nhau.

### STRONG

- Transaction hiện tại đang chạy reverse-consumed microprogram hoặc bytecode stream.

### UNPROVEN

- RBX là PC toàn cục xuyên toàn VM.
- Stream được dùng chung cho mọi method hay chỉ stub cctor đang trace.

---

# BƯỚC AK.2 — Ánh xạ reverse stream sang các write IL

## Câu hỏi

1. Dải static stream 48 byte được chia như thế nào giữa các lần ghi IL liên tiếp?
2. RBX có tiếp tục giữ địa chỉ nguồn tại các event ngoài bốn sample ban đầu không?
3. IL buffer được dựng tuyến tính hay theo nhiều pha?

## Giả thuyết đặt trước

- **H1:** các read giữa hai write IL liên tiếp tạo một interval static không hở.
- **H2:** interval giữa write `00` và write `7F` là recipe tĩnh tạo opcode `0x7F`.
- **H3:** RBX tiếp tục bằng địa chỉ nguồn tại các sample mở rộng.

## Lệnh chính

```text
dx @$w1 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x1800)
dx @$w2 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x1A40)
dx @$sr = @$cursession.TTD.MemoryForPositionRange(0x180007CA0,0x180007CE0,"r",@$w1,@$w2)
dx @$ilw = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"w",@$w1,@$w2)
```

Đối chứng toàn emit window:

```text
dx @$ep1 = @$create("Debugger.Models.TTD.Position",0xA9BD9,0x0)
dx @$ep2 = @$create("Debugger.Models.TTD.Position",0xA9BDE,0x0)
dx @$ilw = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"w",@$ep1,@$ep2)
```

## Dữ liệu thô — static reads

```text
TTD           address          size  read IP
1815          180007CD3        4     18026B95D
183D          180007CD2        1     18029CD47
1860          180007CCE        4     18029CDF8
188D          180007CCA        4     1801C3D12
18B7          180007CC6        4     1801DD803
18CC          180007CC5        1     1802FF324
18F9          180007CC4        1     180327D04
1918          180007CC0        4     1802818AB
1947          180007CB8        8     18028EFD6
1960          180007CB4        4     180219090
1990          180007CB0        4     1801DCA52
19D2          180007CAC        4     1801BB22D
1A04          180007CA8        4     18028BC11
1A1E          180007CA7        1     18028BC7D
```

`@$sr.Count() = 0xE`.

## Dữ liệu thô — IL writes trong cửa sổ AK.2

```text
A9BDC:1886  dst 0x24100669774  size1  IP 0x1801C3CF8  -> IL[0] = 00
A9BDC:19FE  dst 0x24100669775  size1  IP 0x18037137A  -> IL[1] = 7F
```

`@$ilw.Count() = 2` trong `A9BDC:1800 -> 1A40`.

## Phân đoạn theo hai write IL liên tiếp

### Đoạn quan sát trước write `IL[0] = 00`

Read theo thời gian:

```text
CD3/4 -> CD2/1 -> CCE/4 -> write 00
```

Union theo địa chỉ:

```text
[CCE, CD7) = 9 byte
EA EB F5 B7 2E EB DE 88 B5
```

Đây chỉ là **suffix quan sát được** của recipe tạo `00`, vì cửa sổ bắt đầu ở `A9BDC:1800` và recipe có thể đã bắt đầu sớm hơn.

### Đoạn hoàn chỉnh giữa `write 00` và `write 7F`

Read theo thời gian:

```text
CCA/4 -> CC6/4 -> CC5/1 -> CC4/1 -> CC0/4
-> CB8/8 -> CB4/4 -> CB0/4 -> CAC/4 -> write 7F
```

Union theo địa chỉ:

```text
[CAC, CCE) = 0x22 = 34 byte
sum sizes  = 34 byte
gap        = 0
overlap    = 0
```

Bytes theo địa chỉ tăng:

```text
D2 29 B6 F5 AA 86 49 A2 DB 88 48 72 03 CE 6A F0
00 00 00 00 4C E2 4E 3F 05 6A 68 94 B8 BF A1 02
2D 0A
```

Byte seed đã truy trước đó nằm trong đoạn này:

```text
[CC5] = 6A
6A -> 72 -> 8D -> 9E -> 3D -> 7F
```

### Đoạn quan sát sau `write 7F`

```text
CA8/4 -> CA7/1
```

Union:

```text
[CA7, CAC) = 5 byte
5C 80 4A 56 3F
```

Đây chỉ là **prefix quan sát được** của recipe kế tiếp vì opcode `FE` được ghi muộn hơn tại `A9BDC:1ADA`, ngoài cửa sổ hiện tại.

## Full IL write chronology — đối chứng dương

Toàn emit window có `0xB = 11` write:

```text
A9BDA:11C0  dst +02  size4  -> virtual token 0x04800001
A9BDB:1533  dst +08  size4  -> virtual token 0x01800002
A9BDC:0003  dst +0D  size4  -> virtual token 0x0A800003
A9BDC:1886  dst +00  size1  -> 00
A9BDC:19FE  dst +01  size1  -> 7F
A9BDC:1ADA  dst +06  size1  -> FE
A9BDC:1BAE  dst +07  size1  -> 16
A9BDD:0658  dst +0C  size1  -> 6F
A9BDD:15CF  dst +11  size1  -> 2D
A9BDD:1725  dst +12  size1  -> 01
A9BDD:184D  dst +13  size1  -> 00
```

Điều này chứng minh IL buffer không được ghi hoàn toàn tuyến tính. Ba operand token 4 byte được điền trước, sau đó skeleton opcode/branch được ghi theo thứ tự địa chỉ còn thiếu.

Mô hình hiện tại:

```text
phase A: resolve/stage virtual-token operands
phase B: reverse static stream + native handlers -> emit opcode skeleton
phase C: clrjit nhận MethodBody đã ghép
```

## RBX sample mở rộng

### `A9BDC:1815`

```asm
mov r9d,dword ptr [rbx+rsi*2]
```

```text
RBX = 0x180007CD3
RSI = 0
EA  = RBX
```

### `A9BDC:18F9`

```asm
movzx r9d,byte ptr [rbx+r8*2-2]
```

```text
RBX = 0x180007CC4
R8  = 1
EA  = RBX
```

### `A9BDC:1947`

```asm
mov rdx,qword ptr [rbx]
```

```text
RBX = 0x180007CB8
EA  = RBX
```

### `A9BDC:1A1E`

```asm
movzx edx,byte ptr [rbx+rax-53h]
```

```text
RBX          = 0x180007CA7
resolved DS  = 0x180007CA7
```

`RAX` không được chụp ở sample cuối, nên chỉ dùng địa chỉ DS do debugger resolve; chưa tái tính độc lập phép triệt tiêu.

## Kết luận AK.2

### CONFIRMED

- Static slice tiêu thụ giữa hai write IL liền kề `00 -> 7F` là chính xác `[0x180007CAC, 0x180007CCE)`, dài `0x22` byte, không gap và không overlap.
- Write kế tiếp sau slice đó là `IL[1] = 0x7F`.
- Seed `0x6A` tại `CC5` nằm trong slice 34 byte và tham gia trực tiếp tạo `0x7F`.
- RBX bằng effective source address tại bốn sample mở rộng `1815`, `18F9`, `1947`, `1A1E`; cộng bốn sample cũ là tám điểm trải đều trên stream.
- IL buffer được dựng ít nhất hai pha: token operands trước, opcode skeleton sau.

### STRONG

- Slice `[CAC, CCE)` là encoded recipe/microprogram tạo opcode `0x7F` trong state hiện tại.
- RBX là reverse stream cursor trên toàn dải 48 byte đã quan sát.
- Reverse stream chủ yếu điều khiển skeleton opcode/control flow; token operands đi qua pipeline pool/map riêng.

### UNPROVEN

- 34 byte static là đủ để tạo `0x7F` nếu không có stack/register state ban đầu.
- Mỗi opcode có record chiều dài cố định hoặc delimiter riêng.
- Cơ chế này áp dụng nguyên vẹn cho method người dùng virtualized.
- RBX là PC toàn cục xuyên mọi transaction/method.

## Artifact

```text
Pasted text(20260806-054138).txt
162 dòng raw WinDbg TTD
```

## Bước phân biệt tiếp theo — AK.3

Mở rộng cửa sổ tới sau write `FE` để khép kín recipe kế tiếp:

```text
start = A9BDC:19FF
end   = A9BDC:1ADB
```

Cần lấy:

1. Mọi read `0x180007000..0x180008000` giữa hai vị trí.
2. Write IL tại `A9BDC:1ADA` làm boundary dương.
3. Register/disassembly tại read đầu, read cuối và ít nhất một read giữa.

Mục tiêu:

```text
exact static interval -> IL byte FE
```

Sau đó làm tương tự cho `16`, `6F`, `2D`, `01`, `00` để bắt đầu dựng bảng ISA/recipe của stub.

---

# BƯỚC AK.3 — Khép kín static slice giữa `7F` và `FE`

## Câu hỏi

1. Các read sau write `IL[1] = 0x7F` và trước write `IL[6] = 0xFE` có tạo một interval tĩnh liên tục không?
2. Interval mới có tiếp giáp chính xác với slice `[CAC, CCE)` của AK.2 không?
3. Write `FE` có phải boundary dương duy nhất trong cửa sổ không?

## Giả thuyết đặt trước

- **H1:** read chronology tiếp tục giảm địa chỉ, không gap và không overlap.
- **H2:** interval mới kết thúc đúng tại `CAC`, tức tiếp giáp slice tạo `7F`.
- **H3:** cửa sổ có đúng một write vào `IL[6]`, giá trị `FE`.

## Lệnh

```text
dx @$k1 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x19FF)
dx @$k2 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x1ADB)
dx @$rfe = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180008000,"r",@$k1,@$k2)
dx @$wfe = @$cursession.TTD.MemoryForPositionRange(0x2410066977A,0x2410066977B,"w",@$k1,@$k2)
```

## Dữ liệu thô — static reads

```text
TTD           address          size  read IP
1A04          180007CA8        4     18028BC11
1A1E          180007CA7        1     18028BC7D
1A49          180007C9F        8     1802599BC
1A60          180007C9B        4     18036E8B0
1A92          180007C97        4     1802409A3
```

```text
@$rfe.Count() = 5
```

Chronology:

```text
CA8/4 -> CA7/1 -> C9F/8 -> C9B/4 -> C97/4 -> write FE
```

## Dữ liệu thô — boundary write

```text
A9BDC:1ADA
address  = 0x2410066977A
size     = 1
IP       = 0x1802E486B
IL[6]    = FE
```

```text
@$wfe.Count() = 1
```

## Interval khép kín

Union theo địa chỉ:

```text
[0x180007C97, 0x180007CAC)
length          = 0x15 = 21 byte
sum(read sizes) = 21 byte
gap             = 0
overlap         = 0
direction       = strictly descending
```

Bytes theo địa chỉ tăng:

```text
90 0C 32 2A 2D D6 B9 8D 7A 25 92 0F 00 00 00 00
5C 80 4A 56 3F
```

## Tiling với AK.2

```text
AK.2: [0x180007CAC, 0x180007CCE) -> write IL byte 7F
AK.3: [0x180007C97, 0x180007CAC) -> write IL byte FE
```

Hai slice:

```text
adjacent        = true
shared gap      = 0
shared overlap  = 0
combined union  = [0x180007C97, 0x180007CCE)
combined length = 0x37 = 55 byte
```

Thứ tự runtime tiếp tục giảm qua boundary write `7F`:

```text
... CAC -> write 7F -> CA8 -> CA7 -> C9F -> C9B -> C97 -> write FE
```

## Kết luận AK.3

### CONFIRMED

- Static slice tiêu thụ giữa hai write IL liên tiếp `7F -> FE` là chính xác `[0x180007C97, 0x180007CAC)`, dài 21 byte.
- Slice không có gap, không overlap và được đọc theo địa chỉ giảm nghiêm ngặt.
- Cửa sổ có đúng một boundary write: `IL[6] = 0xFE` tại `A9BDC:1ADA`.
- Hai output boundary liên tiếp phân chia hai static slice tiếp giáp hoàn hảo: `[CAC,CCE)` rồi `[C97,CAC)`.
- Reverse static stream tiếp tục tuyến tính qua ít nhất hai lần emit opcode liên tiếp (`7F`, `FE`).

### STRONG

- Slice `[C97,CAC)` là encoded recipe/microprogram tạo byte IL `FE` trong state runtime hiện tại.
- Các lần ghi IL đang đóng vai trò boundary tự nhiên để phân đoạn reverse stream thành recipe theo opcode.
- RBX/cursor state nhiều khả năng được bảo toàn hoặc tái nạp sao cho stream tiếp tục đúng byte kế trước sau mỗi write.

### UNPROVEN

- Slice 21 byte tự nó đủ tạo `FE` khi không có stack/register/flags đầu vào.
- RBX bằng effective source address tại các read `1A49`, `1A60`, `1A92`; chưa chụp register/disassembly tại ba điểm này.
- Mọi opcode đều tương ứng đúng một contiguous slice.
- Cơ chế này áp dụng cho method người dùng virtualized, không chỉ stub cctor.

## Artifact

```text
Raw WinDbg TTD output do người dùng gửi trong phiên, ngày 2026-08-06.
```

## Bước phân biệt tiếp theo — AK.4

Khép kín slice kế tiếp giữa write `FE` và write `16`:

```text
start = A9BDC:1ADB
end   = A9BDC:1BAF
```

Đồng thời chụp ba read chưa kiểm tra RBX của AK.3:

```text
!tt A9BDC:1A49
r rbx,rax,rdx,rsi,rcx,r8,r9,r10,r11
u @rip L3

!tt A9BDC:1A60
r rbx,rax,rdx,rsi,rcx,r8,r9,r10,r11
u @rip L3

!tt A9BDC:1A92
r rbx,rax,rdx,rsi,rcx,r8,r9,r10,r11
u @rip L3
```

Query slice `FE -> 16`:

```text
dx @$m1 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x1ADB)
dx @$m2 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x1BAF)
dx @$r16 = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180008000,"r",@$m1,@$m2)
dx @$r16.Count()
dx -g @$r16

dx @$w16 = @$cursession.TTD.MemoryForPositionRange(0x2410066977B,0x2410066977C,"w",@$m1,@$m2)
dx @$w16.Count()
dx -g @$w16
```

Mục tiêu:

```text
exact static interval -> IL byte 16
```

---

_End of current AK evidence log._
