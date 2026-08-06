# BƯỚC AK.4 — Khép kín static slice giữa `FE` và `16`

Mẫu: `LordsMobileBot.exe`  
Repo: `Turtle19017/dnguardhvm`  
Ngày: 2026-08-06

## Câu hỏi

1. Các read sau write `IL[6] = 0xFE` và trước write `IL[7] = 0x16` có tạo một interval tĩnh liên tục không?
2. Interval này có tiếp giáp chính xác với slice `[0x180007C97,0x180007CAC)` của AK.3 không?
3. Các sample RBX chưa đóng ở AK.3 có tiếp tục cho effective source bằng RBX không?

## Giả thuyết đặt trước

- **H1:** chronology tiếp tục giảm địa chỉ, không gap và không overlap.
- **H2:** slice mới kết thúc đúng tại `0x180007C97`.
- **H3:** cửa sổ chứa đúng một write vào `IL[7]`, giá trị `0x16`.
- **H4:** RBX tiếp tục là source cursor tại các read đại diện.

## Lệnh

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

Lưu ý công cụ: `MemoryForPositionRange(...)` phải nhập trên một dòng trong WinDbg; phiên bản xuống dòng bị parser thực thi từng dòng riêng và báo syntax error.

## Dữ liệu thô — static reads

```text
TTD           address          size  read IP
1AE0          180007C93        4     180218140
1B03          180007C8F        4     18020D26E
1B24          180007C87        8     180336FAE
1B4B          180007C83        4     1802E9161
1B7D          180007C7F        4     1802844C9
```

```text
@$r16.Count() = 5
```

Chronology:

```text
C93/4 -> C8F/4 -> C87/8 -> C83/4 -> C7F/4 -> write 16
```

## Dữ liệu thô — boundary write

```text
A9BDC:1BAE
address  = 0x2410066977B
size     = 1
IP       = 0x18036FA66
IL[7]    = 16
```

```text
@$w16.Count() = 1
```

## Interval khép kín

Union theo địa chỉ:

```text
[0x180007C7F, 0x180007C97)
length          = 0x18 = 24 byte
sum(read sizes) = 24 byte
gap             = 0
overlap         = 0
direction       = strictly descending
```

Bytes theo địa chỉ tăng:

```text
2A C3 36 0A 16 E8 2C B4
0B D5 9B 0F 00 00 00 00
70 4C B6 8C 78 D8 CB F5
```

## Tiling với AK.2 và AK.3

```text
AK.2  [0x180007CAC,0x180007CCE) -> IL 7F   len 0x22
AK.3  [0x180007C97,0x180007CAC) -> IL FE   len 0x15
AK.4  [0x180007C7F,0x180007C97) -> IL 16   len 0x18
```

Ba slice:

```text
adjacent        = true
shared gaps     = 0
shared overlap  = 0
combined union  = [0x180007C7F,0x180007CCE)
combined length = 0x4F = 79 byte
```

Chronology xuyên các boundary:

```text
... CAC -> write 7F
    CA8 ... C97 -> write FE
    C93 ... C7F -> write 16
```

## Đóng lineage RBX của AK.3

### `A9BDC:1A49`

```asm
mov r8,qword ptr [rbx]
```

```text
RBX = 0x180007C9F
EA  = RBX
```

**CONFIRMED**.

### `A9BDC:1A60`

```asm
add r9d,dword ptr [rbx+rsi-3AC1h]
```

```text
RBX = 0x180007C9B
RSI = 0x3AC1
EA  = RBX + 0x3AC1 - 0x3AC1 = RBX
```

**CONFIRMED**.

### `A9BDC:1A92`

```asm
mov r8d,dword ptr [rbx+rdi-6533ED86h]
```

```text
RBX         = 0x180007C97
resolved DS = 0x180007C97
```

Debugger xác nhận effective address bằng RBX, nhưng lần chụp register không gồm `RDI`; không được tự dùng `RSI=0x6533ED86` thay cho RDI. Vì vậy:

- source address `0x180007C97`: **CONFIRMED**;
- phép triệt tiêu độc lập bằng register snapshot: **UNPROVEN** cho sample này.

## Kết luận AK.4

### CONFIRMED

- Static slice tiêu thụ giữa hai write IL liên tiếp `FE -> 16` là `[0x180007C7F,0x180007C97)`, dài 24 byte.
- Slice không có gap, không overlap và được đọc theo địa chỉ giảm nghiêm ngặt.
- Cửa sổ có đúng một boundary write: `IL[7] = 0x16` tại `A9BDC:1BAE`.
- AK.2, AK.3 và AK.4 lát kín một dải 79 byte liên tục từ `0x180007C7F` tới `0x180007CCE`.
- Reverse static stream tiếp tục tuyến tính qua ba lần emit opcode liên tiếp: `7F`, `FE`, `16`.
- RBX/effective source đã được xác nhận tại hai sample mới `1A49`, `1A60`; sample `1A92` có DS resolve bằng RBX nhưng thiếu RDI để tái tính độc lập.

### STRONG

- Các write IL là boundary tự nhiên phân đoạn một reverse-consumed microprogram thành slice theo opcode.
- Slice `[C7F,C97)` là encoded recipe tạo byte IL `16` trong state runtime hiện tại.
- Mô hình linear reverse bytecode/microprogram hiện mạnh hơn mô hình các constant record rời rạc.

### UNPROVEN

- Slice 24 byte tự nó đủ tạo `16` nếu không có stack/register/flags đầu vào.
- Mọi opcode của stub đều có đúng một contiguous slice.
- Cùng cơ chế này được dùng nguyên vẹn cho method người dùng virtualized.
- RBX là program counter toàn cục cho mọi transaction/method.

## Artifact

```text
Raw WinDbg TTD output do người dùng gửi trong phiên ngày 2026-08-06.
```

## Bước phân biệt tiếp theo — AK.5

Khép kín slice giữa write `16` và write `6F`:

```text
start = A9BDC:1BAF
end   = A9BDD:0659
```

Query:

```text
dx @$n1 = @$create("Debugger.Models.TTD.Position",0xA9BDC,0x1BAF)
dx @$n2 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x659)
dx @$r6f = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180008000,"r",@$n1,@$n2)
dx @$r6f.Count()
dx -g @$r6f

dx @$w6f = @$cursession.TTD.MemoryForPositionRange(0x24100669780,0x24100669781,"w",@$n1,@$n2)
dx @$w6f.Count()
dx -g @$w6f
```

Chụp RBX tại read đầu, một read giữa và read cuối của AK.4:

```text
!tt A9BDC:1AE0
r rbx,rax,rdx,rsi,rdi,rcx,r8,r9,r10,r11
u @rip L3

!tt A9BDC:1B24
r rbx,rax,rdx,rsi,rdi,rcx,r8,r9,r10,r11
u @rip L3

!tt A9BDC:1B7D
r rbx,rax,rdx,rsi,rdi,rcx,r8,r9,r10,r11
u @rip L3
```

Mục tiêu:

```text
exact static interval -> IL byte 6F
```
