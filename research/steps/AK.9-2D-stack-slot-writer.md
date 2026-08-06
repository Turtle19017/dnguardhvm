# BƯỚC AK.9 — Writer của stack/state slot chứa `0x2D`

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Instruction nào đã đặt byte `0x2D` vào stack/state slot `0xFD825575A6`?
2. Slot này được ghi một byte hay một word?
3. Giá trị `0x2D` có được stage sớm trước khi commit vào IL buffer không?

## Lệnh

```text
dx @$s1 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x659)
dx @$s2 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x15CC)
dx @$wsrc2d = @$cursession.TTD.MemoryForPositionRange(0xFD825575A6,0xFD825575A7,"w",@$s1,@$s2)
dx @$wsrc2d.Count()
dx -g @$wsrc2d
```

## Dữ liệu thô

Query trả đúng một event ghi chồng lên byte `0xFD825575A6`:

```text
A9BDD:6F5
Address    = 0xFD825575A6
IP         = 0x1801D78E6
Size       = 2
AccessType = Write
Value      = 0x024100669560002D
```

Theo quy tắc TTD `Value`, chỉ dùng `Size=2` byte thấp:

```text
low word = 0x002D
```

Tại event:

```asm
00000001`801d78e6 66892c57
mov word ptr [rdi+rdx*2],bp
```

Registers:

```text
RDI = 0xFD825575A6
RDX = 0
RBP = 0x000000000000002D
BP  = 0x002D
```

Effective destination:

```text
RDI + RDX*2 = 0xFD825575A6
```

Instruction ghi hai byte:

```text
[0xFD825575A6] = 0x2D
[0xFD825575A7] = 0x00
```

Debugger hiển thị memory trước write là `0x9780`; event thay word này bằng `0x002D`.

## Exact provenance mở rộng

```text
BP = 0x002D
    ↓
0x1801D78E6: mov word ptr [0xFD825575A6],bp
    ↓
[0xFD825575A6..A7] = 2D 00
    ↓
0x1802DBB9D: mov r9b,byte ptr [0xFD825575A6]
    ↓
R9B = 0x2D
    ↓
0x18020333B: mov byte ptr [IL+17],r9b
    ↓
IL[17] = 0x2D
```

## Temporal placement

Stack/state word được stage tại:

```text
A9BDD:6F5
```

và chỉ được load vào `R9B` tại:

```text
A9BDD:15CC
```

sau đó commit vào IL tại:

```text
A9BDD:15CF
```

Không có write nào khác vào byte `0xFD825575A6` trong cửa sổ `659 -> 15CC`.

Do đó `0x2D` được stage rất sớm, gần đầu transition `6F -> 2D`, rồi giữ trong stack/state slot cho tới cuối transaction.

## Kết luận

### CONFIRMED

- Có đúng một write vào slot `0xFD825575A6` trong transition đang xét.
- Writer là `0x1801D78E6` tại `A9BDD:6F5`.
- Instruction ghi một word `0x002D`, không chỉ một byte.
- Nguồn trực tiếp là `BP = 0x002D`.
- Low byte `0x2D` sau đó được load vào `R9B` và commit thành `IL[17]`.
- Provenance `BP -> stack word -> R9B -> IL[17]` đã khép hoàn toàn.

### STRONG

- Transition `6F -> 2D` hoạt động theo mô hình stage-then-commit:
  1. stage `0x002D` vào stack/state slot;
  2. thực hiện phần lớn transaction;
  3. load low byte;
  4. commit opcode `2D` vào IL buffer.
- Byte cao `0x00` có thể là zero-extension/padding hoặc state 16-bit, nhưng chưa đủ dữ liệu để chọn.

### UNPROVEN

- Exact writer/transform đã tạo `BP = 0x002D` trước `A9BDD:6F5`.
- `0x002D` là raw IL opcode được stage nguyên dạng hay kết quả decode cuối cùng.
- Ý nghĩa ổn định của byte cao `0x00`.
- Slot `0xFD825575A6` có vai trò cố định giữa các transaction hay chỉ là stack address tái sử dụng.

## Bước tiếp theo — AK.10

Truy exact provenance của `BP = 0x002D` trước store tại `A9BDD:6F5`.

Bắt đầu từ event:

```text
!tt A9BDD:6F5
r rip,rbp,efl
```

Reverse thủ công:

```text
t-
r rip,rbp,efl
u @rip L1
```

Lặp đến khi gặp instruction đầu tiên ghi `RBP/EBP/BP/BPL` hoặc khi low 16-bit của `RBP` đổi khỏi `0x002D`.

Không dùng linear `ub` để suy execution provenance.

Mục tiêu:

```text
exact seed/transform -> BP = 0x002D
```
