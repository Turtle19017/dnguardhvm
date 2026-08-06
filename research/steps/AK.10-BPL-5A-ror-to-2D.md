# BƯỚC AK.10 — Exact transform `BPL 0x5A -> 0x2D`

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Dữ liệu thô

Reverse execution từ store `BP = 0x002D` tại `A9BDD:6F5` cho thấy `RBP` giữ `0x2D` ngược đến `A9BDD:6EB`.

Tại `A9BDD:6EB`:

```asm
00000001`8035166d 40d0cd
ror bpl,1
```

Register trước instruction:

```text
RBP = 0x000000000000005A
BPL = 0x5A
```

Ngay sau instruction, tại `A9BDD:6EC`:

```text
RBP = 0x000000000000002D
BPL = 0x2D
```

Số học:

```text
0x5A = 01011010b
ROR8(0x5A,1) = 00101101b = 0x2D
```

## Provenance hiện tại

```text
BPL = 0x5A
    ↓ ror bpl,1
BPL = 0x2D
    ↓
BP = 0x002D
    ↓ store word tại A9BDD:6F5
[0xFD825575A6..A7] = 2D 00
    ↓ load low byte vào R9B tại A9BDD:15CC
R9B = 0x2D
    ↓ commit tại A9BDD:15CF
IL[17] = 0x2D
```

## Kết luận

### CONFIRMED

- Instruction tạo trực tiếp `BPL = 0x2D` là `ror bpl,1` tại `A9BDD:6EB`.
- Input trực tiếp là `BPL = 0x5A`.
- Output trực tiếp là `BPL = 0x2D`.
- Provenance `0x5A -> ROR1 -> 0x2D -> BP -> stack word -> R9B -> IL[17]` đã khép.

### STRONG

- `0x2D` là kết quả decode cuối của một byte state `0x5A`, không phải raw byte được chép nguyên dạng từ static page trong transition `6F -> 2D`.

### UNPROVEN

- Exact source/writer đã tạo `BPL = 0x5A` trước `A9BDD:6EB`.
- `0x5A` là byte raw từ stream, state trung gian hay kết quả của transform trước đó.

## Điểm bàn giao

Đây là checkpoint an toàn để dừng và bàn giao cho tác nhân khác. Bước tiếp theo là reverse tiếp từ `A9BDD:6EB` để truy writer gần nhất của `BPL = 0x5A`.
