# BƯỚC AK.5 — Khép kín static slice giữa `16` và `6F`

Mẫu: `LordsMobileBot.exe`  
Repo: `Turtle19017/dnguardhvm`  
Ngày: 2026-08-06

## Câu hỏi

1. Các read sau write `IL[7] = 0x16` và trước write `IL[12] = 0x6F` có tạo một interval tĩnh liên tục không?
2. Interval mới có tiếp giáp chính xác với slice `[0x180007C7F,0x180007C97)` của AK.4 không?
3. Ba sample RBX đại diện của AK.4 có tiếp tục cho effective source bằng RBX không?

## Giả thuyết đặt trước

- **H1:** chronology tiếp tục giảm địa chỉ, không gap và không overlap.
- **H2:** slice mới kết thúc đúng tại `0x180007C7F`.
- **H3:** cửa sổ chứa đúng một write vào `IL[12]`, giá trị `0x6F`.
- **H4:** RBX tiếp tục là source cursor tại các sample đại diện.

## Lệnh

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

## Dữ liệu thô — static reads

```text
@$r6f.Count() = 0x42 = 66
```

Chronology địa chỉ/cỡ:

```text
7C77/8 -> 7C73/4 -> 7C72/1 -> 7C6E/4 -> 7C6A/4 -> 7C66/4
-> 7C65/1 -> 7C64/1 -> 7C60/4 -> 7C5F/1 -> 7C5B/4 -> 7C57/4
-> 7C56/1 -> 7C55/1 -> 7C51/4 -> 7C50/1 -> 7C4C/4 -> 7C4B/1
-> 7C47/4 -> 7C46/1 -> 7C42/4 -> 7C41/1 -> 7C3D/4 -> 7C3C/1
-> 7C38/4 -> 7C34/4 -> 7C33/1 -> 7C32/1 -> 7C2E/4 -> 7C2D/1
-> 7C29/4 -> 7C28/1 -> 7C24/4 -> 7C20/4 -> 7C1F/1 -> 7C1E/1
-> 7C1A/4 -> 7C19/1 -> 7C15/4 -> 7C13/2 -> 7C0F/4 -> 7C0B/4
-> 7C07/4 -> 7C06/1 -> 7C02/4 -> 7C01/1 -> 7BFD/4 -> 7BF9/4
-> 7BF8/1 -> 7BF4/4 -> 7BF0/4 -> 7BEF/1 -> 7BEB/4 -> 7BEA/1
-> 7BE6/4 -> 7BE2/4 -> 7BDE/4 -> 7BD6/8 -> 7BD2/4 -> 7BCE/4
-> 7BCA/4 -> 7BC9/1 -> 7BC8/1 -> 7BC4/4 -> 7BC3/1 -> 7BBF/4
-> write 6F
```

## Dữ liệu thô — boundary write

```text
A9BDD:658
address  = 0x24100669780
size     = 1
IP       = 0x18023CA66
IL[12]   = 6F
```

```text
@$w6f.Count() = 1
```

## Interval khép kín

Union theo địa chỉ:

```text
[0x180007BBF, 0x180007C7F)
length          = 0xC0 = 192 byte
sum(read sizes) = 192 byte
gap             = 0
overlap         = 0
direction       = strictly descending
```

Toàn bộ 66 interval con thỏa:

```text
next.address + next.size == previous.address
```

Do đó đây là một phép lát exact, không chỉ là `Sum(Size) ≈ Max-Min`.

Bytes theo địa chỉ tăng:

```text
86 CC AA 40 06 E4 D6 8D 40 75 DC CF 3A E9 37 84
5D B9 94 38 3E E8 D6 FD BD 64 70 01 00 00 00 90
21 B7 0D 20 C7 B1 D6 DB A0 06 BF FF 59 B7 76 BF
C8 BB FB 38 B1 71 76 D8 C0 B6 B7 E0 D3 2C 86 0E
93 9D AE 33 DE 50 5D 7A E9 3F 9F 8F DE 95 DD 06
1A 2D 02 3F 9E A0 07 11 18 40 EB C2 28 91 40 47
57 33 78 58 BF 03 AD 4F BF 3A F4 C2 7B BF 95 B5
45 67 BF 9C 54 C1 9D FD 40 3E E7 BF 40 43 19 D7
93 40 98 3D 2F 02 BF 01 B7 39 65 BF 36 4F BC CC
40 23 CA F7 93 40 6F 7F C3 A7 5A BF F3 72 4D BF
CA E4 1D 79 BF 85 14 67 F4 DF 37 B2 E8 AD 18 33
76 2B 41 32 38 78 44 F2 7F 40 6E 70 01 00 00 00
```

## Tiling với AK.2, AK.3 và AK.4

```text
AK.2  [0x180007CAC,0x180007CCE) -> IL 7F   len 0x22
AK.3  [0x180007C97,0x180007CAC) -> IL FE   len 0x15
AK.4  [0x180007C7F,0x180007C97) -> IL 16   len 0x18
AK.5  [0x180007BBF,0x180007C7F) -> IL 6F   len 0xC0
```

Bốn slice:

```text
adjacent        = true
shared gaps     = 0
shared overlap  = 0
combined union  = [0x180007BBF,0x180007CCE)
combined length = 0x10F = 271 byte
```

Chronology xuyên các boundary:

```text
... CAC -> write 7F
    CA8 ... C97 -> write FE
    C93 ... C7F -> write 16
    C77 ... BBF -> write 6F
```

## Đóng lineage RBX của AK.4

### `A9BDC:1AE0`

```asm
mov ecx,dword ptr [rbx]
```

```text
RBX = 0x180007C93
EA  = RBX
```

**CONFIRMED**.

### `A9BDC:1B24`

```asm
mov rdx,qword ptr [rcx+rbx-63h]
```

```text
RBX = 0x180007C87
RCX = 0x63
EA  = RBX + 0x63 - 0x63 = RBX
```

**CONFIRMED**.

### `A9BDC:1B7D`

```asm
mov edx,dword ptr [rbx+rax-77B91E97h]
```

```text
RBX = 0x180007C7F
RAX = 0x77B91E97
EA  = RBX + 0x77B91E97 - 0x77B91E97 = RBX
```

**CONFIRMED**.

## Phân tích semantic

Slice AK.5 dài `0xC0`, lớn hơn đáng kể ba slice trước. Khoảng IL giữa hai boundary là:

```text
16 <virtual token 0x01800002> 6F
```

Operand token đã được stage trước trong IL buffer, nhưng runtime vẫn tiêu thụ 192 byte static stream trước khi emit `6F`. Vì vậy không được giản lược AK.5 thành “192 byte chỉ giải mã opcode 6F”. Mô hình an toàn hơn:

```text
slice [BBF,C7F)
    -> hoàn tất semantic/control state của constrained. + operand đã stage
    -> chuyển transaction tới callvirt
    -> emit byte 6F
```

Điều này cho thấy boundary write IL là boundary phân đoạn hữu ích, nhưng slice có thể đại diện cho cả một transition IL-level, không chỉ một phép biến đổi byte-to-byte.

## Kết luận AK.5

### CONFIRMED

- Static slice giữa hai write IL liên tiếp `16 -> 6F` là `[0x180007BBF,0x180007C7F)`, dài `0xC0 = 192` byte.
- 66 read lát kín interval này: không gap, không overlap, địa chỉ giảm nghiêm ngặt.
- Cửa sổ có đúng một boundary write: `IL[12] = 0x6F` tại `A9BDD:658`.
- AK.2 đến AK.5 lát kín một reverse stream liên tục dài `0x10F = 271` byte từ `0x180007BBF` tới `0x180007CCE`.
- RBX/effective source được xác nhận trực tiếp tại ba sample AK.4: `1AE0`, `1B24`, `1B7D`.
- Reverse static stream tiếp tục tuyến tính qua bốn write opcode liên tiếp: `7F`, `FE`, `16`, `6F`.

### STRONG

- Các write IL là boundary tự nhiên để phân đoạn reverse-consumed microprogram thành các transition IL-level.
- Slice `[BBF,C7F)` thực hiện transition từ `constrained.`/operand state tới `callvirt`, rồi emit `6F`.
- Mô hình linear reverse microprogram hiện mạnh hơn mô hình các constant record rời rạc.

### UNPROVEN

- Slice 192 byte tự nó đủ tạo transition nếu không có stack/register/flags đầu vào.
- Mọi opcode hoặc IL instruction đều tương ứng đúng một contiguous slice độc lập.
- Cơ chế này áp dụng nguyên vẹn cho method người dùng virtualized.
- RBX là program counter toàn cục cho mọi transaction/method.
- Static stream chứa raw VM ISA hay là native-emitter microprogram sinh riêng cho stub.

## Artifact

```text
Pasted text(20260806-061417).txt
206 dòng raw WinDbg TTD
```

## Bước phân biệt tiếp theo — AK.6

Khép kín slice giữa write `6F` và write `2D`:

```text
start = A9BDD:659
end   = A9BDD:15D0
```

Query:

```text
dx @$p1 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x659)
dx @$p2 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x15D0)
dx @$r2d = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180008000,"r",@$p1,@$p2)
dx @$r2d.Count()
dx -g @$r2d

dx @$w2d = @$cursession.TTD.MemoryForPositionRange(0x24100669785,0x24100669786,"w",@$p1,@$p2)
dx @$w2d.Count()
dx -g @$w2d
```

Chụp RBX tại read đầu, một read giữa và read cuối của AK.5:

```text
!tt A9BDC:1BB6
r rbx,rax,rdx,rsi,rdi,rcx,r8,r9,r10,r11
u @rip L3

!tt A9BDC:2058
r rbx,rax,rdx,rsi,rdi,rcx,r8,r9,r10,r11
u @rip L3

!tt A9BDC:259B
r rbx,rax,rdx,rsi,rdi,rcx,r8,r9,r10,r11
u @rip L3
```

Mục tiêu:

```text
exact static interval -> IL transition ending at byte 2D
```
